using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class CoverageCarryProfileGpuTests(ITestOutputHelper output)
{
    const int WarmupFrames = 12;

    [CoverageDispatchProfileFact]
    public void F0_complete_carry_and_ordinary_skinning_floor_for_1_and_32_actors()
    {
        using var context = GpuDeviceContext.CreateHeadless();
        int observed = Environment.GetEnvironmentVariable("KE_COVERAGE_CARRY_MEASURED_FRAMES") switch
        {
            null => context.Backend == GpuBackendKind.MetalNative ? 9 : 32,
            "9" => 9,
            "32" => 32,
            _ => throw new InvalidOperationException("Carry diagnostic accepts 9 or 32 observed frames.")
        };
        if (context.Backend == GpuBackendKind.MetalNative) Assert.Equal(9, observed);
        var device = context.GpuDevice;
        var fixture = new CoverageCarryFixture();
        var rows = new List<object>();
        foreach (int count in new[] { 1, 32 })
        {
            using var carry = new CoverageCarryDevice(device, fixture, count);
            rows.Add(Measure(device, carry, observed, ordinary: false));
            rows.Add(Measure(device, carry, observed, ordinary: true));
        }
        string json = JsonSerializer.Serialize(new
        {
            schema = "khaozengine.coverage-carry-profile/1",
            label = CoverageCarryReference.Label,
            diagnosticOnly = true,
            warmupFrames = WarmupFrames,
            measuredFrames = observed,
            fixture = CoveragePerfManifest.FixtureVersion,
            fixtureTopology = fixture.Geometry.Topology,
            device = device.Capabilities.DeviceName,
            backend = context.Backend.ToString(),
            timingBoundary = "One outer pair includes frame and bounds-reset uploads, all carry dispatches, current/previous publication and tight bounds. No in-frame readback.",
            exclusions = "Resident shared topology, kernels and full exact anchors. No certificate validity, atlas lookup, certification or refresh. No drawing. Ordinary control uses the renderer's raw skin graph in compute storage with the same uploads, bounds and history.",
            rows,
        });
        string directory = Environment.GetEnvironmentVariable("KE_COVERAGE_PROFILE_RESULTS")
            ?? Path.Combine(CoveragePerfDispatch.RepositoryRoot(AppContext.BaseDirectory), "KhaozEngine.Render.Tests", "TestResults");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "coverage-carry-profile.json");
        File.WriteAllText(path, json + Environment.NewLine);
        output.WriteLine("COVERAGE_CARRY_PROFILE_JSON=" + json);
        output.WriteLine("JSON file: " + path);
    }

    static object Measure(IGpuDevice device, CoverageCarryDevice carry, int observed, bool ordinary)
    {
        var slots = Enumerable.Range(0, 3).Select(_ => new Slot(device)).ToArray();
        var samples = new List<Sample>(observed);
        try
        {
            Run(0, WarmupFrames, retain: false);
            Run(WarmupFrames, observed, retain: true);
            Assert.Equal(observed, samples.Count);
            uint[] results = carry.Read(5), current = carry.Read(3);
            var expectedByPose = new ModelVertex[2][];
            for (int actor = 0; actor < carry.Count; actor++)
            {
                var status = carry.Results(results, actor, WarmupFrames + observed - 1);
                Assert.Equal(0u, status[6]);
                Assert.Equal((uint)(WarmupFrames + observed), status[7]);
                var input = carry.Actors[actor];
                var topology = input.Anchor.Topology;
                int pose = actor % 2;
                var expected = expectedByPose[pose] ?? new ModelVertex[ordinary ? topology.SourceVertexCount : topology.VertexCount];
                if (ordinary)
                {
                    if (expectedByPose[pose] is null)
                        for (int v = 0; v < expected.Length; v++)
                            expected[v] = SkinningMath.SkinVertex(topology.Source.SkinVertices[v], input.Palette);
                    uint[] actual = carry.Surface(current, actor);
                    for (int v = 0; v < expected.Length; v++)
                        for (int axis = 0; axis < 3; axis++)
                        {
                            float value = BitConverter.UInt32BitsToSingle(actual[v * 16 + axis]);
                            float want = axis == 0 ? expected[v].Position.X : axis == 1 ? expected[v].Position.Y : expected[v].Position.Z;
                            Assert.True(float.IsFinite(value) && MathF.Abs(want - value) < 1e-5f);
                        }
                }
                else
                {
                    if (expectedByPose[pose] is null)
                        Assert.True(CoverageCarryReference.TryCarry(topology, input.Palette, input.Commands, input.Anchor,
                            new(topology), expected, out var failure), failure.ToString());
                    Assert.Equal(CoverageCarryReferenceTests.Words(expected), carry.Surface(current, actor));
                    Assert.Equal(CoverageCarryBounds.Of(expected), status.AsSpan(0, 6).ToArray());
                }
                expectedByPose[pose] = expected;
            }
            return new
            {
                actors = carry.Count,
                path = ordinary ? "ordinary skinning compute control" : CoverageCarryReference.Label,
                verticesPerActor = ordinary ? carry.Actors[0].Anchor.Topology.SourceVertexCount : carry.Actors[0].Anchor.Topology.VertexCount,
                dispatches = ordinary ? 1 : 4,
                groupsX = ordinary ? carry.Groups[..1] : carry.Groups,
                groupsY = 1,
                carry.UploadBytes,
                carry.ScratchBytes,
                carry.PersistentBytes,
                carry.AnchorBytes,
                gpuMicroseconds = Stats(samples.Select(s => s.GpuMicroseconds)),
                cpuEncodingMicroseconds = Stats(samples.Select(s => s.CpuEncodingMicroseconds)),
                cpuSubmissionMicroseconds = Stats(samples.Select(s => s.CpuSubmissionMicroseconds)),
                warmManagedAllocatedBytes = Stats(samples.Select(s => (double)s.AllocatedBytes)),
                samples,
            };
        }
        finally { foreach (var slot in slots) slot.Dispose(); }

        void Run(int start, int count, bool retain)
        {
            for (int offset = 0; offset < count; offset += slots.Length)
            {
                int batch = Math.Min(slots.Length, count - offset);
                for (int i = 0; i < batch; i++)
                {
                    var slot = slots[i];
                    int frame = start + offset + i;
                    long allocated = GC.GetAllocatedBytesForCurrentThread(), begin = Stopwatch.GetTimestamp();
                    slot.Commands.Begin();
                    slot.Commands.BeginTimestamps(slot.Pool, 2); slot.Commands.WriteTimestamp(slot.Pool, 0);
                    carry.Prepare(frame); carry.Record(slot.Commands, ordinary);
                    slot.Commands.WriteTimestamp(slot.Pool, 1); slot.Commands.EndTimestamps(slot.Pool); slot.Commands.End();
                    slot.Cpu = Stopwatch.GetElapsedTime(begin).TotalMicroseconds;
                    long submission = Stopwatch.GetTimestamp();
                    device.Submit(slot.Commands, slot.Fence);
                    slot.Submission = Stopwatch.GetElapsedTime(submission).TotalMicroseconds;
                    slot.Allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                }
                // Harness-only drain after a batch. No completion wait or readback is inside a measured frame.
                device.WaitForIdle();
                uint[] statuses = carry.Read(5);
                for (int i = 0; i < batch; i++)
                {
                    var slot = slots[i];
                    for (int actor = 0; actor < carry.Count; actor++)
                    {
                        var status = carry.Results(statuses, actor, start + offset + i);
                        Assert.Equal(0u, status[6]);
                        Assert.Equal((uint)(start + offset + i + 1), status[7]);
                    }
                    Assert.Equal(GpuTimestampStatus.Ready, slot.Pool.TryResolve(slot.Fence, slot.Ticks, out double period));
                    Assert.True(double.IsFinite(period) && period > 0 && slot.Ticks[0] == 0 && slot.Ticks[1] > 0);
                    if (retain) samples.Add(new(start + offset + i, slot.Ticks[1] * period / 1000, slot.Cpu,
                        slot.Allocated, slot.Submission, (ulong[])slot.Ticks.Clone(), period));
                    slot.Fence.Reset();
                }
            }
        }
    }

    static object Stats(IEnumerable<double> source)
    {
        var sorted = source.Order().ToArray();
        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new { min = sorted[0], median = Percentile(.5), p95 = Percentile(.95), p99 = Percentile(.99), max = sorted[^1] };
    }

    sealed record Sample(int Frame, double GpuMicroseconds, double CpuEncodingMicroseconds, long AllocatedBytes,
        double CpuSubmissionMicroseconds, ulong[] Ticks, double PeriodNanoseconds);

    sealed class Slot : IDisposable
    {
        internal readonly IGpuCommandList Commands;
        internal readonly IGpuTimestampPool Pool;
        internal readonly IGpuFence Fence;
        internal readonly ulong[] Ticks = new ulong[2];
        internal double Cpu, Submission;
        internal long Allocated;
        internal Slot(IGpuDevice device)
        { Commands = device.Factory.CreateCommandList(); Pool = device.Factory.CreateTimestampPool(2); Fence = device.Factory.CreateFence(); }
        public void Dispose() { Pool.Dispose(); Fence.Dispose(); Commands.Dispose(); }
    }
}
