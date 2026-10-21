using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using static KhaozEngine.Tests.Gpu.CoverageGpuConformTestData;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageComputeConformGpuTests
{
    [GpuFact]
    public void Stripes_and_replay_preserve_serial_words_prefixes_and_single_face_notes_for_the_finite_matrix()
    {
        const uint poison = 0x7fc00001;
        var c = CoverageReferenceCases.Get("cost-limit-l2-lifted-w1");
        var items = CoverageOverlayTapeReference.Cases();
        Assert.True(c.Commands.Length > 1);
        // Reverse command and actor submission and use both ends of the face table with the same cases.
        var rows = items.Select(i => (Item: i, Face: 0, Reverse: false))
            .Concat(items.Reverse().Select(i => (Item: i, Face: c.Topology.TriangleCount - 1, Reverse: true))).ToArray();
        var cases = rows.Select(r => r.Reverse ? c with { Commands = c.Commands.Reverse().ToArray() } : c).ToArray();
        var scratch = cases.Select(item => Prepare(item)).ToArray();
        using var gpu = GpuDeviceContext.CreateHeadless();
        var device = gpu.GpuDevice;
        using var pipeline = new CoverageGpuPipeline(device);
        Log(gpu, pipeline, output);
        using var session = new CoverageGpuTestSession(device, rows.Select(_ => c.Topology).ToArray(),
            rows.Select(_ => new[] { Matrix4x4.Identity, Matrix4x4.Identity }).ToArray(),
            cases.Select(item => item.Commands).ToArray(), scratch, minimumOverlayCapacity: 9);
        session.PoisonContinuations(poison);
        var host = CoverageGpuUpload.Host(c.Topology, 2);
        var cold = new List<uint>(new uint[17]);
        for (int actor = 0; actor < rows.Length; actor++)
        {
            uint[] copy = (uint[])host.Words.Clone();
            copy[copy[14] + (uint)rows[actor].Face * 3u + 1u] = 0; // Region zero maps to the prepared command slot.
            cold.AddRange(copy);
        }
        for (int actor = 0; actor < rows.Length; actor++)
        {
            var (item, face, _) = rows[actor];
            uint offset = checked((uint)cold.Count);
            cold.AddRange(PackOverlaySupport(item));
            uint command = session.Batch.Actors[actor].FrameBase + 16u + 32u;
            for (uint slot = 0; slot < c.Commands.Length; slot++)
                device.UpdateBuffer(session.Batch.FrameData, CoverageGpuLayout.Bytes(command + slot * 80u + 67u), new[] { offset });
            uint[] header = CoverageGpuLayout.WorkHeader(host, 9);
            uint work = session.Batch.Actors[actor].WorkBase;
            var continuation = Enumerable.Repeat(poison, c.Topology.TriangleCount * 12).ToArray();
            int p = face * 12;
            if (item.State == 1)
                for (int corner = 0; corner < 3; corner++)
                {
                    Vector3 point = item.Host[corner];
                    continuation[p + corner * 3] = CoverageOverlayTapeReference.Bits(point.X);
                    continuation[p + corner * 3 + 1] = CoverageOverlayTapeReference.Bits(point.Y);
                    continuation[p + corner * 3 + 2] = CoverageOverlayTapeReference.Bits(point.Z);
                }
            continuation[p + 11] = item.State;
            device.UpdateBuffer(session.Batch.Work, CoverageGpuLayout.Bytes(work + header[36]), continuation);
            device.UpdateBuffer(session.Batch.Work, CoverageGpuLayout.Bytes(work + header[44]),
                Enumerable.Repeat(poison, checked((int)header[45] * 17 + (int)header[47])).ToArray());
        }
        session.Batch.UploadStatic(cold.ToArray());
        using var commands = device.Factory.CreateCommandList();
        commands.Begin();
        pipeline.RecordPhases(commands, session.Batch, CoverageGpuPhase.CorrectFacesOverlay, CoverageGpuPhase.CorrectFacesOverlayReplay);
        var reduce = pipeline.Pipelines.Single(p => p.Phase == CoverageGpuPhase.ReduceFaces).Pipeline;
        commands.SetComputePipeline(reduce);
        commands.Dispatch(1, (uint)rows.Length, 1);
        session.RecordReadback(commands);
        commands.End(); device.Submit(commands); device.WaitForIdle(); session.CompleteReadback();
        for (int actor = 0; actor < rows.Length; actor++)
        {
            var (item, face, _) = rows[actor];
            uint[] continuation = session.ReadWords(actor, 36), notes = session.ReadWords(actor, 24), header = session.ReadHeader(actor);
            if (item.State != 1)
            {
                Assert.Equal(poison, continuation[face * 12 + 10]);
                Assert.Equal(item.State, continuation[face * 12 + 11]);
                Assert.All(session.ReadWords(actor, 44), word => Assert.Equal(poison, word));
                Assert.All(session.ReadWords(actor, 46), word => Assert.Equal(poison, word));
                Assert.Equal(0u, header[26]);
                continue;
            }
            var expected = CoverageOverlayTapeReference.Serial(item);
            var tape = CoverageOverlayTapeReference.Striped(item);
            Assert.True(expected.Maximum == continuation[face * 12 + 10], item.Name + " maximum");
            Assert.Equal(expected.Ok ? 2u : 0u, continuation[face * 12 + 11]);
            uint code = expected.Ok ? 0u : 4u;
            Assert.Equal(code, header[26]);
            Assert.Equal(expected.Ok ? uint.MaxValue : (uint)scratch[actor].RegionCommands[0], header[27]);
            Assert.Equal(expected.Ok ? 0u : 1u, header[28]);
            Assert.Equal(code, notes[face * 18]);
            Assert.Equal(expected.Ok ? 0u : 1u, notes[face * 18 + 4]);
            if (!expected.Ok)
            {
                Assert.Equal((uint)face, notes[face * 18 + 2]);
                int slot = scratch[actor].RegionCommands[0];
                var command = cases[actor].Commands[scratch[actor].Frames[slot].Command];
                var failure = session.ReadFailure(actor);
                Assert.Equal(command.EquipmentInstanceId, failure.EquipmentInstanceId);
                Assert.Equal(command.Id, failure.CommandId);
                Assert.Equal(1, failure.Count);
            }
            uint[] actualPairs = session.ReadWords(actor, 44), actualGuards = session.ReadWords(actor, 46);
            Assert.Equal(tape.Guards, actualGuards.AsSpan(face * 4, 4).ToArray());
            if (tape.Guards.All(g => g == 1))
                for (int pair = 0; pair < item.Sheets.Length; pair++)
                {
                    var actual = actualPairs.AsSpan((face * 9 + pair) * 17, 17);
                    Assert.Equal(tape.Pairs[pair][15], actual[15]);
                    Assert.Equal(tape.Pairs[pair][16], actual[16]);
                    for (int candidate = 0; candidate < 15; candidate++)
                        if ((actual[15] & (1u << candidate)) != 0) Assert.Equal(tape.Pairs[pair][candidate], actual[candidate]);
                }
        }
        output.WriteLine($"Matched serial overlay words, prefix tapes and guard attribution for {rows.Length} finite case/face/submission rows");
    }

    // Deliberately malformed device records bypass public support certification to exercise the kernel's guards.
    static uint[] PackOverlaySupport(CoverageOverlayTapeReference.Case item)
    {
        var words = new List<uint>(new uint[16]);
        words[5] = (uint)item.Sheets.Length;
        words[7] = (uint)words.Count;
        foreach (var sheet in item.Sheets)
            foreach (Vector3 p in sheet) words.AddRange(new[] { CoverageOverlayTapeReference.Bits(p.X), CoverageOverlayTapeReference.Bits(p.Y), CoverageOverlayTapeReference.Bits(p.Z) });
        words[8] = (uint)words.Count;
        words.AddRange(Enumerable.Range(0, item.Sheets.Length).Select(i => (uint)i));
        words[9] = (uint)words.Count;
        for (uint i = 0; i < item.Sheets.Length; i++) words.AddRange(new uint[] { 0, 0, i, 0, 0, 3 * i, 3 * i + 1, 3 * i + 2 });
        words[10] = words[11] = (uint)words.Count;
        words.AddRange(new uint[257]);
        words[12] = (uint)words.Count;
        var f = new CoverageReferenceArithmetic();
        foreach (var sheet in item.Sheets)
        {
            var area = f.Edge(sheet[0], sheet[1], sheet[2]);
            words.Add(unchecked((uint)area.Sign)); words.Add(CoverageOverlayTapeReference.Bits(area.Value));
        }
        return words.ToArray();
    }
}
