using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Gpu.CoverageGpuConformTestData;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageComputeFilteredQueueGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Empty_single_partial_queues_and_failed_prefix_replay_match_exact_then_reuse_poisoned_storage()
    {
        var c = CoverageFilteredQueueFixture.Case();
        var cases = new[] { c, c with { Commands = c.Commands.Select(command => command with { Id = command.Id + 16, EquipmentInstanceId = command.EquipmentInstanceId + 98 }).Reverse().ToArray() } };
        using var gpu = GpuDeviceContext.CreateHeadless();
        var modes = CoverageFilterNativeEvidence.Modes(gpu.GpuDevice, output).ToArray();
        if (modes.Length == 0) return;
        int first = CoverageFilteredQueueFixture.Faces(c)[0] * 4;
        var exact = Capture(new CoverageBatchDevice(gpu.GpuDevice, capabilities: gpu.Capabilities with { SupportsFloat32Rte = false }), "exact", false);
        var exactFailure = Capture(new CoverageBatchDevice(gpu.GpuDevice, capabilities: gpu.Capabilities with { SupportsFloat32Rte = false }), "exact", true);
        foreach (var mode in modes)
            foreach (string pattern in new[] { "zero", "single", "partial", "failed-prefix", "failed-prefix-dense" })
            {
                string refusal = pattern switch { "single" => $"gl_GlobalInvocationID.x == {first}u", "partial" or "failed-prefix-dense" => "true", _ => "false" };
                var caps = mode == CoverageGpuFilterPath.FilteredFlush ? gpu.Capabilities with { SupportsFloat32DenormPreserve = false } : gpu.Capabilities;
                var device = new CoverageBatchDevice(gpu.GpuDevice, source =>
                {
                    bool face = source.Contains("CovWorkWords[covRow(w,44u)+t] = 2u;", StringComparison.Ordinal);
                    string condition = face && pattern == "single" ? $"gl_GlobalInvocationID.x == {first / 4}u" : refusal;
                    return source.Replace("bool CovFilterRefused = false;", "bool CovFilterRefused = " + condition + ";", StringComparison.Ordinal);
                }, caps);
                bool failed = pattern.StartsWith("failed-prefix", StringComparison.Ordinal);
                var actual = Capture(device, pattern, failed);
                var expected = failed ? exactFailure : exact;
                for (int actor = 0; actor < cases.Length; actor++)
                    foreach (int row in expected[actor].Keys) Assert.Equal(expected[actor][row], actual[actor][row]);
            }

        Dictionary<int, uint[]>[] Capture(IGpuDevice device, string pattern, bool failed)
        {
            var scratch = cases.Select(x => Prepare(x)).ToArray();
            using var pipeline = new CoverageGpuPipeline(device);
            using var session = Session(device, cases, scratch);
            var path = pipeline.PathFor(session.Batch);
            Assert.Equal(pattern == "exact" ? CoverageGpuFilterPath.Exact : CoverageGpuFilterPolicy.Resolve(device.Backend, device.Capabilities), path);
            CoverageFilterNativeEvidence.Batch(gpu.GpuDevice, pipeline, session.Batch, pattern, output);
            session.PoisonContinuations(0x7fc00001u);
            session.PoisonFilterStorage(0xdeadbeefu);
            for (int actor = 0; actor < cases.Length; actor++) session.UploadPosed(cases[actor].Posed, actor);
            var result = new Dictionary<int, uint[]>[cases.Length];
            using (var seeded = new CoverageFilteredQueueFrame(device, session, cases, failed))
            {
                Execute(device, pipeline, session, CoverageGpuPhase.Select);
                uint[] observed = seeded.Read();
                for (int actor = 0; actor < cases.Length; actor++)
                {
                    uint[] header = seeded.Header(observed, actor), continuations = seeded.Row(observed, actor, 36, 12);
                    var active = seeded.Active[actor];
                    foreach (int face in active)
                    {
                        Assert.Equal(failed ? 0u : 2u, continuations[face * 12 + 11]);
                        Assert.Equal(BitConverter.SingleToUInt32Bits(failed ? 1f : 9f), continuations[face * 12 + 10]);
                    }
                    for (int face = 0; face < continuations.Length / 12; face++)
                        if (!active.Contains(face)) Array.Clear(continuations, face * 12, 11);
                    result[actor] = new()
                    {
                        [36] = continuations,
                        [24] = seeded.Row(observed, actor, 24, 6),
                        [26] = session.ReadHeader(actor).AsSpan(26, 6).ToArray(),
                        [60] = session.ReadResult(actor),
                        [62] = Words(session.ReadFinal(actor, sourceFallback: session.ReadRefusedFlag(actor) != 0))
                    };
                    foreach (int row in new[] { 12, 14, 16, 20, 22, 34 }) result[actor][row] = session.ReadWords(actor, row);
                    if (failed)
                    {
                        Assert.Equal(CoverageFailureCode.UnsupportedTransform, session.ReadFailure(actor).Code);
                        var command = cases[actor].Commands.MinBy(command => command.Id)!;
                        Assert.Equal(active.Count(face => cases[actor].Topology.Triangles[face].RegionIndex == command.RegionIndex), session.ReadFailure(actor).Count);
                        Assert.Equal(command.Id, session.ReadFailure(actor).CommandId);
                        Assert.Equal(7u, session.ReadHeader(actor)[29]);
                    }
                    if (path == CoverageGpuFilterPath.Exact) continue;
                    uint count = pattern switch { "zero" or "failed-prefix" => 0u, "single" => 1u, _ => 524u };
                    Assert.Equal(524u, header[55]); Assert.Equal(count, header[53]);
                    Assert.Equal(144u, header[54]);
                    uint faceCount = count == 524 ? 144u : count;
                    Assert.Equal(faceCount, header[52]);
                    uint[] faceQueue = seeded.Row(observed, actor, 48, 1).Take((int)faceCount).ToArray();
                    uint[] core = Enumerable.Range(0, cases[actor].Topology.TriangleCount)
                        .Where(face => cases[actor].Topology.Triangles[face].Role == CoverageTriangleRole.Core)
                        .Select(face => (uint)face).Take((int)faceCount).ToArray();
                    Assert.Equal(core, faceQueue);
                    uint[] queue = seeded.Row(observed, actor, 50, 1).Take((int)count).ToArray();
                    uint[] wanted = active.SelectMany(face => Enumerable.Range(0, 4).Select(pair => (uint)(face * 4 + pair)))
                        .Where(item => count > 0 && (pattern != "single" || item == first)).ToArray();
                    Assert.Equal(wanted, queue);
                    Assert.Equal(queue.Length, queue.Distinct().Count());
                    uint[] pairs = seeded.Row(observed, actor, 46, 3);
                    foreach (int face in active)
                    {
                        for (int pair = 0; pair < 4; pair++) Assert.Equal(2u, pairs[(face * 4 + pair) * 3]);
                        if (failed)
                        {
                            Assert.Equal(0u, pairs[(face * 4 + 1) * 3 + 2]); Assert.Equal(0u, pairs[(face * 4 + 3) * 3 + 2]);
                            Assert.Equal(0x41100000u, pairs[(face * 4 + 2) * 3 + 1]);
                        }
                    }
                    output.WriteLine($"FILTER BOUNDARY mode={path} pattern={pattern} actor={actor} faces={faceCount}/144 active=524 queued={count} partial={count % 64} prefix={(failed ? 1 : 9)} differences=0");
                }
            }
            // A real successful construction follows the aborted/seeded frame on the same physical storage.
            session.PoisonContinuations(0x7fc00001u);
            session.PoisonFilterStorage(0xdeadbeefu);
            foreach (int actor in new[] { 1, 0 })
                for (int slot = 0; slot < cases[actor].Commands.Length; slot++)
                    device.UpdateBuffer(session.Batch.StaticData, session.SupportOffset(actor, slot) * 4, CoverageGpuUpload.Support(cases[actor].Commands[0].Support).Words);
            Execute(device, pipeline, session, CoverageGpuPhase.Select);
            for (int actor = 0; actor < cases.Length; actor++)
            {
                var expected = new ModelVertex[cases[actor].Topology.VertexCount];
                Assert.True(CoverageReferenceConstructor.TryConstruct(cases[actor].Topology, cases[actor].Posed, cases[actor].Model,
                    cases[actor].Commands, scratch[actor], expected, out var failure));
                Assert.Equal(failure, session.ReadFailure(actor)); Assert.Equal(Words(expected), Words(session.ReadFinal(actor, false)));
            }
            output.WriteLine($"FILTER REUSE path={path} pattern={pattern} actors=2 success=true differences=0");
            return result;
        }
    }
}
