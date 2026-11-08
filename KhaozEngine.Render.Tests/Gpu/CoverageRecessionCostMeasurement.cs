using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Fixed adjacent three-frame batches. Only the completed batch drains or resolves timestamps.
/// Uses main's timestamp ring and completed-End contract, including its failure and quarantine policy.</summary>
internal static class CoverageRecessionCostMeasurement
{
    internal const int ConditioningFrames = 96;
    internal const int BatchFrames = 3;
    internal static readonly string[] Layouts = ["carry-only", "baseline-scene", "covered-scene"];

    internal static IEnumerable<(int Start, int Count, bool Reverse)> Schedule(int count)
    {
        for (int start = 0; start < count; start += BatchFrames)
            yield return (start, Math.Min(BatchFrames, count - start), start / BatchFrames % 2 != 0);
    }

    internal static Row[] Measure(IGpuDevice device, CoverageRecessionBodySizeFixture fixture, int actors, int observed, long deadline)
    {
        var rows = Layouts.Select(layout => new Driver(device, fixture, actors, layout, observed)).ToArray();
        try
        {
            Run(ConditioningFrames, retain: false);
            Run(observed, retain: true);
            return rows.Select(row => row.Result()).ToArray();
        }
        finally { foreach (var row in rows) row.Dispose(); }

        void Run(int count, bool retain)
        {
            foreach (var batch in Schedule(count))
            {
                for (int index = 0; index < rows.Length; index++)
                {
                    var row = rows[batch.Reverse ? rows.Length - index - 1 : index];
                    for (int i = 0; i < batch.Count; i++)
                    {
                        CheckDeadline(deadline);
                        row.Frame(batch.Start + i, retain);
                    }
                    // No waits, readbacks, timestamp resolution or oracle work inside measured frames.
                    device.WaitForIdle();
                    CheckDeadline(deadline);
                    row.Harvest(deadline);
                }
            }
        }
    }

    internal static void CheckDeadline(long deadline)
    {
        if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException("The single bounded coverage unit exceeded its deadline. No rerun is authorized.");
    }

    internal sealed record Statistics(double Min, double Median, double P95, double P99, double Max);
    internal static Statistics Stats(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new InvalidOperationException("A measurement row has no samples.");
        double P(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new(sorted[0], P(.5), P(.95), P(.99), sorted[^1]);
    }

    internal sealed record Sample(int Frame, int Sequence, double GpuMicroseconds, double CpuPreparationMicroseconds,
        double CpuEncodingMicroseconds, double CpuSubmissionMicroseconds, long WarmManagedBytes,
        int DispatchCount, int DrawCount, ulong[] Ticks, double PeriodNanoseconds);
    internal sealed record Row(string Layout, int Actors, int SlotsPerActor, long SurfaceBytes, long CandidateBytes,
        int CarryDispatchCount, Statistics GpuMicroseconds, Statistics CpuPreparationMicroseconds,
        Statistics CpuEncodingMicroseconds, Statistics CpuSubmissionMicroseconds, Statistics WarmManagedBytes, Sample[] Samples);

    sealed class Driver : IDisposable
    {
        readonly IGpuDevice device;
        readonly CoverageRecessionNativeScene scene;
        readonly string layout;
        readonly bool carry;
        readonly CoverageCarryBatch? batch;
        readonly GpuRetireQueue? retired;
        readonly IGpuCommandList[] native;
        readonly CommandTallyGpuCommandList[] commands;
        readonly GpuPerfTimestampRing ring;
        readonly Sample?[] samples;
        readonly GpuPerfTimestampResult?[] timestamps;
        readonly double[] cpuPrepare, cpuEncode, cpuSubmit;
        readonly long[] allocated;
        readonly int[] dispatches, draws;
        readonly int[] slots;
        readonly CoverageSurfaceRange[] lastSurfaces;
        readonly int observed;
        int next;
        long surfaceBytes;

        internal Driver(IGpuDevice device, CoverageRecessionBodySizeFixture fixture, int actors, string layout, int observed)
        {
            this.device = device; this.layout = layout; this.observed = observed;
            carry = layout == "carry-only";
            scene = new(device, fixture, actors, layout != "baseline-scene");
            if (carry)
            {
                retired = GpuRetireQueue.CreateFrameCounted(device, 3);
                batch = new(device, retired);
                batch.Reserve(actors, fixture.Source.Vertices.Count * actors, fixture.Topology.VertexCount * actors, fixture.Topology.TriangleCount * actors);
            }
            int capacity = ConditioningFrames + observed;
            samples = new Sample[observed]; timestamps = new GpuPerfTimestampResult[capacity];
            cpuPrepare = new double[capacity]; cpuEncode = new double[capacity]; cpuSubmit = new double[capacity];
            allocated = new long[capacity]; dispatches = new int[capacity]; draws = new int[capacity];
            slots = new int[actors];
            lastSurfaces = new CoverageSurfaceRange[actors];
            native = Enumerable.Range(0, BatchFrames).Select(_ => device.Factory.CreateCommandList()).ToArray();
            commands = native.Select(list => new CommandTallyGpuCommandList(list)).ToArray();
            ring = new(device, capacity, 2, (id, result) => timestamps[id] = result);
        }

        internal void Frame(int frame, bool retain)
        {
            int id = next++, slot = id % BatchFrames;
            long before;
            long prepareBytes, openingBytes, encodeBytes, submitBytes;
            // Region setup is outside the measured counter. Only warmed preparation and encoding count.
            using (AllocationRegion.Start("coverage preparation", enabled: retain))
            {
                long allocation = GC.GetAllocatedBytesForCurrentThread();
                before = Stopwatch.GetTimestamp();
                if (carry)
                {
                    scene.Submission.Begin(); scene.Scene.BeginFrame(scene.Submission); batch!.PollCompleted();
                    scene.Queue(frame);
                    for (int actor = 0; actor < scene.Owners.Length; actor++)
                    {
                        var owner = scene.Owners[actor];
                        if (!batch.TryAcquireOutput(owner, out slots[actor], out var failure)) throw new CoverageFrameStateException(failure);
                        bool staged = frame % 3 == 2
                            ? batch.TryStageRaw(owner, slots[actor], scene.Palette, owner.Request.Model, out failure)
                            : batch.TryStage(owner, owner.Request.EntryIndex, slots[actor], owner.Request.Palette,
                                owner.Request.Model, owner.Uniforms.Commands, out failure);
                        if (!staged) throw new CoverageFrameStateException(failure);
                    }
                }
                else scene.Prepare(frame);
                prepareBytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
                cpuPrepare[id] = Stopwatch.GetElapsedTime(before).TotalMicroseconds;
            }
            IGpuTimestampPool? pool;
            IGpuFence? completion = null;
            using (AllocationRegion.Start("coverage recording begin", enabled: retain))
            {
                long allocation = GC.GetAllocatedBytesForCurrentThread();
                commands[slot].Clear(); native[slot].Begin();
                if (!ring.TryBegin(native[slot], scene.Submission, id, 2, out pool)) throw new InvalidOperationException(ring.Failure);
                if (carry && !scene.Submission.TryRetain(batch!, out completion)) throw new InvalidOperationException("Carry completion retention failed.");
                openingBytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
            }
            using (AllocationRegion.Start("coverage encoding", enabled: retain))
            {
                long allocation = GC.GetAllocatedBytesForCurrentThread();
                before = Stopwatch.GetTimestamp();
                if (carry) batch!.Record(commands[slot]);
                else scene.Render(commands[slot]);
                ((IGpuCommandListTimestampEnd)native[slot]).FinishTimestampsOnEnd(pool, 1);
                native[slot].End();
                encodeBytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
                cpuEncode[id] = Stopwatch.GetElapsedTime(before).TotalMicroseconds;
            }
            using (AllocationRegion.Start("coverage submission", enabled: retain))
            {
                long allocation = GC.GetAllocatedBytesForCurrentThread();
                before = Stopwatch.GetTimestamp(); scene.Submission.Submit(native[slot]);
                if (carry)
                    for (int actor = 0; actor < scene.Owners.Length; actor++)
                        lastSurfaces[actor] = batch!.Surface(scene.Owners[actor], slots[actor], batch.CommittedSlot(scene.Owners[actor]));
                else if (layout == "covered-scene")
                    for (int actor = 0; actor < scene.Owners.Length; actor++) lastSurfaces[actor] = scene.Owners[actor].Request.Surface;
                if (carry) batch!.CommitSubmitted(completion);
                cpuSubmit[id] = Stopwatch.GetElapsedTime(before).TotalMicroseconds;
                submitBytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
            }
            allocated[id] = prepareBytes + openingBytes + encodeBytes + submitBytes;
            dispatches[id] = commands[slot].Tally[GpuCommandKind.Dispatch];
            draws[id] = commands[slot].Tally[GpuCommandKind.Draw] + commands[slot].Tally[GpuCommandKind.DrawIndexed];
            if (carry)
            {
                Assert.Equal(5, dispatches[id]);
                surfaceBytes = batch!.Surface(scene.Owners[0], slots[0], -1).Current.SizeInBytes;
            }
            else if (layout == "covered-scene")
            {
                scene.AssertPublished();
                surfaceBytes = scene.Owners[0].Request.Surface.Current.SizeInBytes;
                Assert.True(dispatches[id] >= 5);
            }
            if (!carry) Assert.True(draws[id] > 0);
        }

        internal void Harvest(long deadline)
        {
            foreach (var wait in ring.AwaitAvailability(Stopwatch.GetTimestamp, deadline))
                Assert.True(wait.Wait.Ready);
            ring.PollCompleted();
            Assert.Null(ring.Failure);
        }

        internal Row Result()
        {
            Assert.Equal(ConditioningFrames + observed, next); Assert.True(ring.CompleteBlock(), ring.Failure);
            for (int i = 0; i < observed; i++)
            {
                int id = ConditioningFrames + i;
                var time = timestamps[id] ?? throw new InvalidOperationException("A timestamp sample is missing.");
                Assert.True(time.Valid); Assert.True(time.Ticks[^1] > 0);
                samples[i] = new(i, id, time.Ticks[^1] * time.Period!.Value / 1000, cpuPrepare[id], cpuEncode[id], cpuSubmit[id],
                    allocated[id], dispatches[id], draws[id], time.Ticks.ToArray(), time.Period.Value);
            }
            var final = samples.Select(s => s!).ToArray();
            int k = batch?.SlotsPerActor ?? GpuRetireQueue.FramesInFlightFor(device) + 2;
            if (layout != "baseline-scene")
            {
                Assert.Equal((long)k * scene.Owners.Length * scene.Fixture.Topology.VertexCount * 64, surfaceBytes);
                // Evidence readback happens once after all observed frames, outside every timing bracket.
                uint[] words = GpuReadback.ReadBuffer<uint>(device, lastSurfaces[0].Current, checked((int)surfaceBytes / 4));
                foreach (var surface in lastSurfaces)
                {
                    Check(surface.CurrentBase, observed - 1);
                    Check(surface.PreviousBase, observed - 2);
                }
                void Check(uint start, int frame)
                {
                    int entry = frame % 3 == 2 ? -1 : frame % 2;
                    CoverageRecessionComputeGpuTests.AssertWords(scene.Fixture,
                        words.AsSpan((int)start, scene.Fixture.Topology.VertexCount * 16).ToArray(), 0, entry, Vector3.UnitZ, 1, Vector3.Zero);
                }
            }
            return new(layout, scene.ActorCount, k, surfaceBytes,
                layout == "baseline-scene" ? 0 : (long)scene.ActorCount * scene.Fixture.Topology.VertexCount * 64,
                layout == "baseline-scene" ? 0 : 5, Stats(final.Select(s => s.GpuMicroseconds)),
                Stats(final.Select(s => s.CpuPreparationMicroseconds)), Stats(final.Select(s => s.CpuEncodingMicroseconds)),
                Stats(final.Select(s => s.CpuSubmissionMicroseconds)), Stats(final.Select(s => (double)s.WarmManagedBytes)), final);
        }

        public void Dispose()
        {
            // Submission owns completion. Drain and release it before disposing query pools or surfaces.
            scene.Submission.Dispose(); ring.Dispose(); batch?.Dispose(); retired?.Dispose();
            foreach (var list in native) list.Dispose();
            scene.Dispose();
        }
    }
}
