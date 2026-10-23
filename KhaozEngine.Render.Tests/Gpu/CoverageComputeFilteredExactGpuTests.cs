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

public sealed class CoverageComputeFilteredExactGpuTests(ITestOutputHelper output)
{
    // These are distinct queue workloads over the same frozen input words, not repeated timing samples.
    [GpuFact]
    public void Frozen_sparse_and_dense_queues_preserve_every_observable_word()
    {
        var cases = CoverageReferenceCases.All();
        using var gpu = GpuDeviceContext.CreateHeadless();
        var exactDevice = new CoverageBatchDevice(gpu.GpuDevice, capabilities: gpu.Capabilities with { SupportsFloat32Rte = false });
        Snapshot[] exact = Capture(exactDevice, "exact");
        var patterns = new List<string> { "ordinary", "sparse", "dense" };
        if (gpu.Backend == GpuBackendKind.VulkanNative && gpu.Capabilities.SupportsFloat32DenormFlush) patterns.Add("flush");
        foreach (string pattern in patterns)
        {
            string refusal = pattern switch { "sparse" => "gl_GlobalInvocationID.x % 17u == 0u", "dense" => "true", _ => "false" };
            var device = new CoverageBatchDevice(gpu.GpuDevice, source => source.Replace(
                "bool CovFilterRefused = false;", "bool CovFilterRefused = " + refusal + ";", StringComparison.Ordinal),
                pattern == "flush" ? gpu.Capabilities with { SupportsFloat32DenormPreserve = false } : null);
            Snapshot[] actual = Capture(device, pattern);
            for (int actor = 0; actor < cases.Length; actor++)
            {
                Assert.Equal(exact[actor].Final, actual[actor].Final);
                Assert.Equal(exact[actor].Failure, actual[actor].Failure);
                Assert.Equal(exact[actor].Rows.Keys, actual[actor].Rows.Keys);
                foreach (int row in exact[actor].Rows.Keys) Assert.Equal(exact[actor].Rows[row], actual[actor].Rows[row]);
            }
        }

        Snapshot[] Capture(IGpuDevice device, string pattern)
        {
            var scratch = cases.Select(c => Prepare(c)).ToArray();
            using var pipeline = new CoverageGpuPipeline(device);
            Assert.Equal(CoverageGpuFilterPolicy.Resolve(device.Backend, device.Capabilities), pipeline.FilterPath);
            using var session = Session(device, cases, scratch);
            CoverageFilterNativeEvidence.Batch(gpu.GpuDevice, pipeline, session.Batch, pattern, output);
            var actualPath = pipeline.PathFor(session.Batch);
            output.WriteLine($"FILTER PATH backend={gpu.Backend} adapter={gpu.Capabilities.DeviceName} path={actualPath} supported={pipeline.FilterPath} pattern={pattern}");
            session.PoisonContinuations(0x7fc00001u);
            for (int actor = 0; actor < cases.Length; actor++) session.UploadPosed(cases[actor].Posed, actor);
            Execute(device, pipeline, session, CoverageGpuPhase.Select);
            var observations = new Snapshot[cases.Length];
            long queuedFaces = 0, activeFaces = 0, queuedPairs = 0, activePairs = 0;
            for (int actor = 0; actor < cases.Length; actor++)
            {
                var c = cases[actor]; var expected = new ModelVertex[c.Topology.VertexCount];
                Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands, scratch[actor], expected, out var failure));
                Assert.Equal(failure, session.ReadFailure(actor));
                uint[] final = Words(session.ReadFinal(actor, sourceFallback: false));
                Assert.Equal(Words(expected), final);
                var rows = new Dictionary<int, uint[]>();
                foreach (int pair in new[] { 12, 14, 16, 20, 22, 34 }) rows.Add(pair, session.ReadWords(actor, pair));
                uint[] faces = session.ReadWords(actor, 36);
                // An idle continuation has no published payload. Compare every defined payload and every state.
                for (int face = 0; face < faces.Length / 12; face++)
                    if (faces[face * 12 + 11] == 0u) Array.Clear(faces, face * 12, 11);
                rows.Add(36, faces);
                uint[] header = session.ReadHeader(actor);
                observations[actor] = new(final, header.AsSpan(26, 6).ToArray(), rows);
                if (actualPath != CoverageGpuFilterPath.Exact)
                {
                    queuedFaces += header[52]; queuedPairs += header[53]; activeFaces += header[54]; activePairs += header[55];
                    Assert.InRange(header[52], 0u, header[54]); Assert.InRange(header[53], 0u, header[55]);
                    if (pattern == "dense") { Assert.Equal(header[54], header[52]); Assert.Equal(header[55], header[53]); }
                }
            }
            if (actualPath != CoverageGpuFilterPath.Exact)
            {
                Assert.True(activeFaces > 0 && activePairs > 0);
                if (pattern == "sparse") Assert.True(queuedPairs > 0 && queuedPairs < activePairs);
                output.WriteLine($"FILTER QUEUE pattern={pattern} frozen=72 faces={queuedFaces}/{activeFaces} pairs={queuedPairs}/{activePairs} differences=0");
            }
            else output.WriteLine($"FILTER UNSUPPORTED filteredExecuted=false FILTER QUEUE pattern={pattern} frozen=72 path=exact differences=0");
            return observations;
        }
    }

    sealed record Snapshot(uint[] Final, uint[] Failure, Dictionary<int, uint[]> Rows);
}
