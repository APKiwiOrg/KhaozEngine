using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Native test storage and observation. Tests own submission and the final assertion drain.</summary>
internal sealed class CoverageGpuTestSession : IDisposable
{
    readonly IGpuDevice device;
    readonly CoverageGpuHostUpload[] hosts;
    readonly uint[][] headers;
    readonly IGpuBuffer staging;
    readonly uint[] captured;
    readonly int workWordCount;
    readonly uint[][] commandSupports;

    internal CoverageGpuTestSession(IGpuDevice device, CoverageRefinement[] topologies, Matrix4x4[][] palettes,
        CoverageCommandPose[][]? commands = null, CoverageReferenceScratch[]? scratch = null)
    {
        if (topologies.Length != palettes.Length) throw new ArgumentException("One palette is required per actor.");
        this.device = device;
        hosts = topologies.Select((t, i) => CoverageGpuUpload.Host(t, palettes[i].Length)).ToArray();
        headers = hosts.Select(CoverageGpuLayout.WorkHeader).ToArray();
        commandSupports = hosts.Select(_ => new uint[4]).ToArray();
        int frames = checked((int)hosts.Sum(h => (long)CoverageGpuLayout.FrameWords(h.BoneCount)));
        int workWords = checked((int)hosts.Sum(h => (long)CoverageGpuLayout.WorkWords(h)));
        workWordCount = workWords;
        int surfaces = checked((int)hosts.Sum(h => (long)CoverageGpuLayout.SurfaceWords(h)));
        Batch = new(device);
        Batch.Reserve(hosts.Length, frames, workWords, surfaces);
        int supportWords = commands is null ? 0 : commands.Sum(c => c.Sum(p => CoverageGpuUpload.Support(p.Support).Words.Length));
        uint[] cold = new uint[checked(17 + hosts.Sum(h => h.Words.Length) + supportWords)];
        uint supportBase = checked((uint)(17 + hosts.Sum(h => h.Words.Length)));
        uint staticBase = 17, frameBase = (uint)hosts.Length, workBase = 0, surfaceBase = 0;
        Batch.BeginFrame();
        for (int actor = 0; actor < hosts.Length; actor++)
        {
            var h = hosts[actor];
            h.Words.CopyTo(cold, (int)staticBase);
            var reservation = CoverageGpuLayout.CreateReservation(actor, staticBase, frameBase, workBase, surfaceBase, surfaceBase, h);
            uint[] frame = new uint[CoverageGpuLayout.FrameWords(h.BoneCount)];
            frame[0] = staticBase; frame[1] = workBase; frame[2] = surfaceBase; frame[3] = surfaceBase; frame[4] = (uint)actor * 16;
            frame[5] = (uint)h.SourceCount; frame[6] = (uint)h.VertexCount; frame[7] = (uint)h.TriangleCount;
            frame[8] = (uint)h.PositionGroupCount; frame[9] = (uint)h.AliasCount; frame[10] = (uint)h.ShadingGroupCount; frame[11] = (uint)h.BoneCount;
            for (int bone = 0; bone < h.BoneCount; bone++) CoverageGpuUpload.WriteMatrix(palettes[actor][bone], frame.AsSpan(16 + bone * 16));
            Array.Fill(frame, uint.MaxValue, 16 + h.BoneCount * 16 + 4 * 80 + 4, 32);
            if (commands is not null && scratch is not null)
            {
                for (int slot = 0; slot < commands[actor].Length; slot++)
                {
                    var reference = scratch[actor].Frames[slot];
                    var command = commands[actor][reference.Command];
                    uint[] support = CoverageGpuUpload.Support(command.Support).Words;
                    support.CopyTo(cold, (int)supportBase);
                    commandSupports[actor][slot] = supportBase;
                    supportBase += (uint)support.Length;
                }
                WriteCommands(frame, actor, commands[actor], scratch[actor]);
            }
            Batch.Add(reservation, frame);
            device.UpdateBuffer(Batch.Work, CoverageGpuLayout.Bytes(workBase), headers[actor]);
            staticBase += (uint)h.Words.Length; frameBase += (uint)frame.Length;
            workBase += CoverageGpuLayout.WorkWords(h); surfaceBase += CoverageGpuLayout.SurfaceWords(h);
        }
        Batch.UploadStatic(cold);
        Batch.UploadFrame();
        captured = new uint[checked(workWords + hosts.Length * 16)];
        staging = device.Factory.CreateBuffer(new(CoverageGpuLayout.Bytes((uint)captured.Length), GpuBufferUsage.Staging));
    }

    internal CoverageGpuBatch Batch { get; }

    void WriteCommands(Span<uint> frame, int actor, CoverageCommandPose[] commands, CoverageReferenceScratch scratch)
    {
        frame[12] = (uint)commands.Length;
        int start = 16 + hosts[actor].BoneCount * 16;
        for (int slot = 0; slot < commands.Length; slot++)
        {
            var reference = scratch.Frames[slot];
            CoverageGpuUpload.WriteCommand(reference, commands[reference.Command], commandSupports[actor][slot], frame[(start + slot * 80)..]);
            frame[start + 320 + slot] = (uint)scratch.Order[slot];
        }
        frame.Slice(start + 324, 32).Fill(uint.MaxValue);
        for (int region = 0; region < scratch.RegionCommands.Length; region++) frame[start + 324 + region] = unchecked((uint)scratch.RegionCommands[region]);
    }

    internal void UploadCommands(int actor, CoverageCommandPose[] commands, CoverageReferenceScratch scratch)
    {
        uint[] frame = new uint[CoverageGpuLayout.FrameWords(hosts[actor].BoneCount)];
        WriteCommands(frame, actor, commands, scratch);
        uint start = 16u + (uint)hosts[actor].BoneCount * 16u;
        uint f = Batch.Actors[actor].FrameBase;
        device.UpdateBuffer(Batch.FrameData, CoverageGpuLayout.Bytes(f + 12u), frame.AsSpan(12, 1));
        device.UpdateBuffer(Batch.FrameData, CoverageGpuLayout.Bytes(f + start), frame.AsSpan((int)start));
    }

    internal uint[] ReadWords(int actor, int pair)
    {
        int stride = pair switch { 0 or 2 or 4 or 6 or 8 or 10 or 32 => 16, 12 => 3, 22 => 2, 24 or 34 => 6, 36 => 12, _ => 1 };
        int start = checked((int)(Batch.Actors[actor].WorkBase + headers[actor][pair]));
        return captured.AsSpan(start, checked((int)headers[actor][pair + 1] * stride)).ToArray();
    }

    internal void UploadPosed(ReadOnlySpan<ModelVertex> posed, int actorSlot)
    {
        if (posed.Length != hosts[actorSlot].SourceCount) throw new ArgumentException("Posed input must match its actor.", nameof(posed));
        uint[] words = new uint[posed.Length * 16];
        for (int i = 0; i < posed.Length; i++) CoverageGpuUpload.WriteModel(posed[i], words.AsSpan(i * 16));
        uint offset = Batch.Actors[actorSlot].WorkBase + headers[actorSlot][0];
        device.UpdateBuffer(Batch.Work, CoverageGpuLayout.Bytes(offset), words);
    }

    internal void PoisonBefore(uint word)
    {
        for (int actor = 0; actor < hosts.Length; actor++)
        {
            uint[] words = new uint[hosts[actor].VertexCount * 16]; Array.Fill(words, word);
            device.UpdateBuffer(Batch.Work, CoverageGpuLayout.Bytes(Batch.Actors[actor].WorkBase + headers[actor][4]), words);
        }
    }

    internal void PoisonContinuations(uint word)
    {
        for (int actor = 0; actor < hosts.Length; actor++)
            foreach (var (pair, stride) in new[] { (34, 6), (36, 12) })
            {
                uint[] words = new uint[headers[actor][pair + 1] * stride];
                Array.Fill(words, word);
                device.UpdateBuffer(Batch.Work, CoverageGpuLayout.Bytes(Batch.Actors[actor].WorkBase + headers[actor][pair]), words);
            }
    }

    internal void RecordReadback(IGpuCommandList commands)
    {
        uint bytes = CoverageGpuLayout.Bytes((uint)workWordCount);
        commands.CopyBuffer(Batch.Work, 0, staging, 0, bytes);
        commands.CopyBuffer(Batch.Results, 0, staging, bytes, (uint)hosts.Length * 64);
    }

    internal unsafe void CompleteReadback()
    {
        MappedData map = device.Map(staging, GpuMapMode.Read);
        try { new ReadOnlySpan<uint>((void*)map.Data, captured.Length).CopyTo(captured); }
        finally { device.Unmap(staging); }
    }

    internal uint[] ReadHeader(int actorSlot) => captured.AsSpan((int)Batch.Actors[actorSlot].WorkBase, 64).ToArray();
    internal uint[] ReadResult(int actorSlot) => captured.AsSpan(workWordCount + actorSlot * 16, 16).ToArray();
    internal ModelVertex[] ReadBefore(int actorSlot) => ReadVertices(actorSlot, 4, hosts[actorSlot].VertexCount);
    internal ModelVertex[] ReadSource(int actorSlot) => ReadVertices(actorSlot, 2, hosts[actorSlot].SourceCount);

    ModelVertex[] ReadVertices(int actor, int row, int count)
    {
        var result = new ModelVertex[count];
        int start = checked((int)(Batch.Actors[actor].WorkBase + headers[actor][row]));
        for (int i = 0; i < count; i++)
        {
            int p = start + i * 16;
            float F(int field) => BitConverter.UInt32BitsToSingle(captured[p + field]);
            result[i] = new(new(F(0), F(1), F(2)), new(F(3), F(4), F(5)), new(F(6), F(7), F(8), F(9)),
                new(F(10), F(11)), new(F(12), F(13), F(14), F(15)));
        }
        return result;
    }

    public void Dispose() { staging.Dispose(); Batch.Dispose(); }
}
