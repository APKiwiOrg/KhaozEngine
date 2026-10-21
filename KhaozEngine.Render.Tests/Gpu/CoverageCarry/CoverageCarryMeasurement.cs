using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Separate outer and stage layouts, each conditioned independently. Readbacks stay outside frame timing.</summary>
internal static class CoverageCarryMeasurement
{
    internal const int WarmupFrames = 96;
    static readonly string[] StageNames = ["skin", "refine and transport", "basis and normals", "gather and publish"];

    internal static object Measure(IGpuDevice device, CoverageCarryDevice carry, int observed,
        bool ordinary, bool nativeFloat = false, bool stages = false)
    {
        if (ordinary && stages) throw new ArgumentException("The ordinary control uses the outer layout only.");
        int markers = stages ? 10 : 2;
        var slots = Enumerable.Range(0, 3).Select(_ => new Slot(device, markers)).ToArray();
        var samples = new List<Sample>(observed);
        var errors = Enumerable.Range(0, carry.Count).Select(actor => new CoverageCarryError(actor)).ToArray();
        try
        {
            Run(0, WarmupFrames, retain: false);
            Run(WarmupFrames, observed, retain: true);
            Assert.Equal(observed, samples.Count);
            CheckSurface(WarmupFrames + observed - 1, carry.Read(5));
            return new
            {
                actors = carry.Count,
                path = ordinary ? "ordinary skinning compute control" : nativeFloat ? CoverageCarryNativeShader.Label : CoverageCarryReference.Label,
                exact = !ordinary && !nativeFloat,
                timingLayout = stages ? "stages (diagnostic only)" : "outer pair (decision number)",
                timestampMarkers = markers,
                verticesPerActor = ordinary ? carry.Actors[0].Anchor.Topology.SourceVertexCount : carry.Actors[0].Anchor.Topology.VertexCount,
                dispatches = ordinary ? 1 : 4,
                groupsX = ordinary ? carry.Groups[..1] : carry.Groups,
                groupsY = 1,
                carry.UploadBytes,
                carry.ScratchBytes,
                carry.PersistentBytes,
                carry.AnchorBytes,
                gpuMicroseconds = Stats(samples.Select(s => s.GpuMicroseconds)),
                halfMeans = HalfMeans(samples.Select(s => s.GpuMicroseconds).ToArray()),
                stageGpuMicroseconds = stages ? StageNames.Select((name, stage) => new
                { name, stats = Stats(samples.Select(s => s.StageMicroseconds[stage])) }).ToArray() : null,
                cpuEncodingMicroseconds = Stats(samples.Select(s => s.CpuEncodingMicroseconds)),
                cpuSubmissionMicroseconds = Stats(samples.Select(s => s.CpuSubmissionMicroseconds)),
                warmManagedAllocatedBytes = Stats(samples.Select(s => (double)s.AllocatedBytes)),
                nativeFloatErrorsPerActor = nativeFloat ? errors : null,
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
                    slot.Commands.BeginTimestamps(slot.Pool, (uint)markers); slot.Commands.WriteTimestamp(slot.Pool, 0);
                    carry.Prepare(frame); carry.Record(slot.Commands, ordinary, stages ? slot.Pool : null);
                    slot.Commands.WriteTimestamp(slot.Pool, (uint)markers - 1); slot.Commands.EndTimestamps(slot.Pool); slot.Commands.End();
                    slot.Cpu = Stopwatch.GetElapsedTime(begin).TotalMicroseconds;
                    long submission = Stopwatch.GetTimestamp();
                    device.Submit(slot.Commands, slot.Fence);
                    slot.Submission = Stopwatch.GetElapsedTime(submission).TotalMicroseconds;
                    slot.Allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                }
                // Drain only between batches. Every frame status is checked. Surface comparisons run after
                // the entire observed block, keeping offline reference work out of the conditioning sequence.
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
                        for (int axis = 0; axis < 3; axis++) Assert.True(status[axis] <= status[axis + 3]);
                    }
                    Assert.Equal(GpuTimestampStatus.Ready, slot.Pool.TryResolve(slot.Fence, slot.Ticks, out double period));
                    Assert.True(double.IsFinite(period) && period > 0 && slot.Ticks[0] == 0 && slot.Ticks[^1] > 0);
                    for (int marker = 1; marker < markers; marker++) Assert.True(slot.Ticks[marker] >= slot.Ticks[marker - 1]);
                    if (retain) samples.Add(new(start + offset + i, slot.Ticks[^1] * period / 1000, slot.Cpu,
                        slot.Allocated, slot.Submission, (ulong[])slot.Ticks.Clone(), period, stages
                            ? Enumerable.Range(0, 4).Select(stage => (slot.Ticks[stage * 2 + 2] - slot.Ticks[stage * 2 + 1]) * period / 1000).ToArray() : []));
                    slot.Fence.Reset();
                }
            }
        }

        void CheckSurface(int frame, uint[] results)
        {
            uint[] current = carry.Read(3);
            var expectedByPose = new ModelVertex[2][];
            for (int actor = 0; actor < carry.Count; actor++)
            {
                var input = carry.Actors[actor];
                var topology = input.Anchor.Topology;
                int pose = actor % 2;
                var expected = expectedByPose[pose] ?? new ModelVertex[ordinary ? topology.SourceVertexCount : topology.VertexCount];
                if (expectedByPose[pose] is null)
                {
                    if (ordinary)
                        for (int vertex = 0; vertex < expected.Length; vertex++)
                            expected[vertex] = SkinningMath.SkinVertex(topology.Source.SkinVertices[vertex], input.Palette);
                    else Assert.True(CoverageCarryReference.TryCarry(topology, input.Palette, input.Commands, input.Anchor,
                        new(topology), expected, out var failure), failure.ToString());
                }
                uint[] actual = carry.Surface(current, actor);
                if (nativeFloat) errors[actor].Include(expected, actual);
                else if (!ordinary) Assert.Equal(CoverageCarryReferenceTests.Words(expected), actual);
                else
                    for (int vertex = 0; vertex < expected.Length; vertex++)
                        for (int axis = 0; axis < 3; axis++)
                        {
                            float value = BitConverter.UInt32BitsToSingle(actual[vertex * 16 + axis]);
                            float want = axis == 0 ? expected[vertex].Position.X : axis == 1 ? expected[vertex].Position.Y : expected[vertex].Position.Z;
                            Assert.True(float.IsFinite(value) && MathF.Abs(want - value) < 1e-5f);
                        }
                Assert.Equal(CoverageCarryBounds.OfWords(actual.AsSpan(0, expected.Length * 16)),
                    carry.Results(results, actor, frame).AsSpan(0, 6).ToArray());
                expectedByPose[pose] = expected;
            }
        }
    }

    internal readonly record struct Halves(int FirstHalfCount, int SecondHalfCount, double FirstHalfMean,
        double SecondHalfMean, double SecondOverFirst);

    internal static Halves HalfMeans(double[] values)
    {
        int half = values.Length / 2;
        double first = values.Take(half).Average(), second = values.Skip(half).Average();
        return new(half, values.Length - half, first, second, second / first);
    }

    static object Stats(IEnumerable<double> source)
    {
        var sorted = source.Order().ToArray();
        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new { min = sorted[0], median = Percentile(.5), p95 = Percentile(.95), p99 = Percentile(.99), max = sorted[^1] };
    }

    sealed record Sample(int Frame, double GpuMicroseconds, double CpuEncodingMicroseconds, long AllocatedBytes,
        double CpuSubmissionMicroseconds, ulong[] Ticks, double PeriodNanoseconds, double[] StageMicroseconds);

    sealed class Slot : IDisposable
    {
        internal readonly IGpuCommandList Commands;
        internal readonly IGpuTimestampPool Pool;
        internal readonly IGpuFence Fence;
        internal readonly ulong[] Ticks;
        internal double Cpu, Submission;
        internal long Allocated;
        internal Slot(IGpuDevice device, int markers)
        {
            Commands = device.Factory.CreateCommandList(); Pool = device.Factory.CreateTimestampPool((uint)markers);
            Fence = device.Factory.CreateFence(); Ticks = new ulong[markers];
        }
        public void Dispose() { Pool.Dispose(); Fence.Dispose(); Commands.Dispose(); }
    }
}
