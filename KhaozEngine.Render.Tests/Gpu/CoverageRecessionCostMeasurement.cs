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
    internal static readonly string[] Layouts = ["carry-only", "baseline-scene", "covered-scene",
        "carry-only-outer", "covered-scene-outer", "carry-only-inline-outer", "covered-scene-inline-outer",
        "baseline-scene-passes", "covered-scene-passes", "covered-scene-inline-passes"];
    internal static string[] DispatchSchedule => CoverageCarryShaders.Kernels.Select(kernel => kernel.Name["CoverageCarry".Length..]).ToArray();

    internal static IEnumerable<(int Start, int Count, bool Reverse)> Schedule(int count)
    {
        for (int start = 0; start < count; start += BatchFrames)
            yield return (start, Math.Min(BatchFrames, count - start), start / BatchFrames % 2 != 0);
    }

    internal sealed record Measurement(Row[] Rows, CoverageRecessionUnavailableRow[] UnavailableRows);

    internal static Measurement Measure(IGpuDevice device, CoverageRecessionBodySizeFixture fixture, int actors, int observed, long deadline,
        IReadOnlyList<string>? layouts = null)
    {
        var rows = new Driver?[(layouts ?? Layouts).Count];
        var unavailable = new List<CoverageRecessionUnavailableRow>();
        var results = new List<Row>();
        try
        {
            for (int i = 0; i < rows.Length; i++)
            {
                string layout = (layouts ?? Layouts)[i];
                try { rows[i] = new Driver(device, fixture, actors, layout, observed); }
                catch (CoverageRecessionRowException error) { Unavailable(error); }
            }
            Run(ConditioningFrames, retain: false);
            Run(observed, retain: true);
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] is not { } row) continue;
                try { results.Add(row.Result()); }
                catch (CoverageRecessionRowException error) { Drop(i, error); }
            }
            return new(results.ToArray(), unavailable.ToArray());
        }
        finally { foreach (var row in rows) row?.Dispose(); }

        void Unavailable(CoverageRecessionRowException error)
        {
            if (IsAcceptance(error.Layout)) throw error;
            unavailable.Add(CoverageRecessionUnavailableRow.From(error));
        }
        void Drop(int index, CoverageRecessionRowException error)
        {
            Unavailable(error);
            rows[index]!.Dispose(); rows[index] = null;
        }
        void Run(int count, bool retain)
        {
            foreach (var batch in Schedule(count))
                for (int index = 0; index < rows.Length; index++)
                {
                    int ordinal = batch.Reverse ? rows.Length - index - 1 : index;
                    if (rows[ordinal] is not { } row) continue;
                    try
                    {
                        for (int i = 0; i < batch.Count; i++)
                        {
                            row.Frame(batch.Start + i, retain, deadline);
                        }
                        // No waits, readbacks, timestamp resolution or oracle work inside measured frames.
                        device.WaitForIdle();
                        CheckDeadline(deadline);
                        row.Harvest(deadline);
                    }
                    catch (Exception error) { Drop(ordinal, row.Failure(error)); }
                }
        }
    }

    internal static bool IsAcceptance(string layout) => layout is "carry-only" or "baseline-scene" or "covered-scene";

    internal static double? DiagnosticDifference(IReadOnlyList<Row> rows, int actors, string plus, string minus)
    {
        var a = rows.SingleOrDefault(r => r.Actors == actors && r.Layout == plus);
        var b = rows.SingleOrDefault(r => r.Actors == actors && r.Layout == minus);
        return a is null || b is null ? null : a.GpuMicroseconds.Median - b.GpuMicroseconds.Median;
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
        int DispatchCount, int DrawCount, ulong[] Ticks, double PeriodNanoseconds,
        CoverageRecessionStageTiming.StageSample[] StageGpuMicroseconds,
        CoverageRecessionPassTiming.PassSample[] PassGpuMicroseconds, Scene3DPassTimingsMs CpuPassRecordingMs)
    {
        public CoverageRecessionPassTiming.ClearAbsorption[] UnscopedDeferredClears { get; init; } = [];
        public double? UnscopedGpuMicroseconds => PassGpuMicroseconds.Length == 0 ? null
            : GpuMicroseconds - PassGpuMicroseconds.Sum(p => p.ExclusiveGpuMicroseconds);
    }
    internal sealed record Row(string Layout, int Actors, int SlotsPerActor, long SurfaceBytes, long CandidateBytes,
        int CarryDispatchCount, Statistics GpuMicroseconds, Statistics CpuPreparationMicroseconds,
        Statistics CpuEncodingMicroseconds, Statistics CpuSubmissionMicroseconds, Statistics WarmManagedBytes, Sample[] Samples)
    {
        public bool PassTimingEnabled => Layout.EndsWith("-passes", StringComparison.Ordinal);
        public bool InlinePreciseHelpers => Layout.Contains("-inline-", StringComparison.Ordinal);
        public Statistics? UnscopedGpuMicroseconds => PassTimingEnabled ? Stats(Samples.Select(s => s.UnscopedGpuMicroseconds!.Value)) : null;
        public object[] PassGpuMicroseconds => Samples.SelectMany(s => s.PassGpuMicroseconds).GroupBy(p => p.Pass).Select(g => (object)new
        {
            Pass = g.Key,
            ExecutedFrames = g.Count(p => p.Executed),
            FusedMotionFrames = g.Count(p => p.FusedMotion),
            MetalClearAttributionAvailable = g.All(p => p.MetalClearAttributionAvailable),
            AbsorbedDeferredClears = g.SelectMany(p => p.AbsorbedDeferredClears).GroupBy(c => c)
                .Select(c => new { Clear = c.Key, Frames = c.Count() }).ToArray(),
            InclusiveGpuMicroseconds = Stats(g.Select(p => p.InclusiveGpuMicroseconds)),
            ExclusiveGpuMicroseconds = Stats(g.Select(p => p.ExclusiveGpuMicroseconds)),
        }).ToArray();
        /// <summary>Only the outer timestamp pair, the same marker count as the baseline scene. A backend whose
        /// marker ends the native encoder charges every stage bracket to the row that carries it.</summary>
        public bool OuterMarkersOnly => Layout.EndsWith("-outer", StringComparison.Ordinal);
        public string[] DispatchSchedule => CarryDispatchCount == 0 ? [] : CoverageRecessionCostMeasurement.DispatchSchedule;
        public object[] StageGpuMicroseconds => OuterMarkersOnly ? [] : DispatchSchedule.Select((stage, i) => (object)new
        { Stage = stage, GpuMicroseconds = Stats(Samples.Select(s => s.StageGpuMicroseconds[i].GpuMicroseconds)) }).ToArray();
    }

    internal sealed class Driver : IDisposable
    {
        internal bool OmitCarryRecordingForTests { get; init; }
        internal (long Preparation, long Opening, long Encoding, long Submission) LastAllocation { get; private set; }
        readonly IGpuDevice device;
        readonly CoverageRecessionObservedDevice observer;
        readonly int actors;
        string phase = "conditioning";
        int frameNumber = -1, recordingSlot = -1, currentSlot = -1;
        bool disposed;
        readonly CoverageRecessionNativeScene scene = null!;
        readonly string layout;
        readonly bool carry, baseline, staged;
        readonly CoverageRecessionPassTiming? passTiming;
        readonly Scene3DPassTimingsMs[] cpuPassRecording;
        readonly CoverageCarryBatch? batch;
        readonly GpuRetireQueue? retired;
        readonly IGpuCommandList[] native = new IGpuCommandList[BatchFrames];
        readonly CommandTallyGpuCommandList[] commands = new CommandTallyGpuCommandList[BatchFrames];
        readonly GpuPerfTimestampRing ring = null!;
        readonly CoverageRecessionStageTiming stageTiming;
        readonly uint markerCount;
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
            observer = new CoverageRecessionObservedDevice(device, layout.Contains("-inline-", StringComparison.Ordinal));
            this.device = device = observer; this.layout = layout; this.observed = observed; this.actors = actors;
            try
            {
                stageTiming = new(observer.CarryStages);
                carry = layout.StartsWith("carry-only", StringComparison.Ordinal);
                baseline = layout.StartsWith("baseline-scene", StringComparison.Ordinal);
                staged = !baseline && !layout.EndsWith("-outer", StringComparison.Ordinal) && !layout.EndsWith("-passes", StringComparison.Ordinal);
                scene = new(device, fixture, actors, !baseline);
                if (carry)
                {
                    retired = GpuRetireQueue.CreateFrameCounted(device, 3);
                    batch = new(device, retired);
                    batch.Reserve(actors, fixture.Source.Vertices.Count * actors, fixture.Topology.VertexCount * actors, fixture.Topology.TriangleCount * actors);
                }
                int capacity = ConditioningFrames + observed;
                cpuPassRecording = new Scene3DPassTimingsMs[capacity];
                samples = new Sample[observed]; timestamps = new GpuPerfTimestampResult[capacity];
                cpuPrepare = new double[capacity]; cpuEncode = new double[capacity]; cpuSubmit = new double[capacity];
                allocated = new long[capacity]; dispatches = new int[capacity]; draws = new int[capacity];
                slots = new int[actors];
                lastSurfaces = new CoverageSurfaceRange[actors];
                for (int i = 0; i < BatchFrames; i++)
                { native[i] = device.Factory.CreateCommandList(); commands[i] = new(native[i]); }
                if (layout.EndsWith("-passes", StringComparison.Ordinal))
                {
                    passTiming = new(observer.CarryStages);
                    scene.Scene.EnableTiming = true;
                    scene.Scene.PassObserver = passTiming;
                    foreach (var list in commands)
                    { list.ComputeDispatchObserver = passTiming.Dispatch; passTiming.AttachCommands(list); }
                    // Discover only after native targets and the lazy point owner have been built.
                    // These cold submitted frames precede the unchanged conditioning schedule.
                    for (int frame = 0; frame < BatchFrames; frame++)
                    {
                        frameNumber = frame; passTiming.Discover(); scene.Prepare(frame); RequirePublished();
                        currentSlot = 0; commands[0].Clear(); recordingSlot = 0; native[0].Begin(); scene.Render(commands[0]);
                        passTiming.Complete(); native[0].End(); recordingSlot = -1; passTiming.CompleteNativeEnd(native[0]);
                        scene.Submission.Submit(native[0]); device.WaitForIdle();
                    }
                    passTiming.Freeze(capacity);
                    markerCount = passTiming.MarkerCount;
                }
                else
                {
                    markerCount = staged ? CoverageRecessionStageTiming.MarkerCount : 2;
                    if (staged) foreach (var list in commands) list.ComputeDispatchObserver = stageTiming.Dispatch;
                }
                frameNumber = -1; currentSlot = -1;
                stageTiming.Reset(); passTiming?.ResetCarryBrackets();
                ring = new(device, capacity, markerCount, (id, result) => timestamps[id] = result);
            }
            catch (Exception error)
            {
                var failure = Failure(error);
                Dispose();
                throw failure;
            }
        }

        internal void Frame(int frame, bool retain, long deadline = long.MaxValue)
        {
            frameNumber = frame; phase = retain ? "observed" : "conditioning"; currentSlot = -1;
            stageTiming.Reset(); passTiming?.ResetCarryBrackets();
            try { CheckDeadline(deadline); FrameCore(frame, retain); }
            catch (Exception error) { throw Failure(error); }
        }

        void FrameCore(int frame, bool retain)
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
                else { scene.Prepare(frame); RequirePublished(); }
                prepareBytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
                cpuPrepare[id] = Stopwatch.GetElapsedTime(before).TotalMicroseconds;
            }
            IGpuTimestampPool? pool;
            IGpuFence? completion = null;
            using (AllocationRegion.Start("coverage recording begin", enabled: retain))
            {
                long allocation = GC.GetAllocatedBytesForCurrentThread();
                currentSlot = slot; commands[slot].Clear(); recordingSlot = slot; native[slot].Begin();
                if (!ring.TryBegin(native[slot], scene.Submission, id, markerCount, out pool)) throw new InvalidOperationException(ring.Failure);
                if (passTiming is not null) passTiming.BeginFrame(native[slot], pool, id);
                else if (staged) stageTiming.Begin(native[slot], pool);
                if (carry && !scene.Submission.TryRetain(batch!, out completion)) throw new InvalidOperationException("Carry completion retention failed.");
                openingBytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
            }
            using (AllocationRegion.Start("coverage encoding", enabled: retain))
            {
                long allocation = GC.GetAllocatedBytesForCurrentThread();
                before = Stopwatch.GetTimestamp();
                if (!OmitCarryRecordingForTests)
                {
                    if (carry) batch!.Record(commands[slot]);
                    else scene.Render(commands[slot]);
                }
                if (passTiming is not null) passTiming.Complete();
                else if (staged && !stageTiming.Complete()) throw Failure(null, CoverageRecessionRowRefusal.CarryBracketsIncomplete);
                cpuPassRecording[id] = scene.Scene.PassTimingsMs;
                ((IGpuCommandListTimestampEnd)native[slot]).FinishTimestampsOnEnd(pool, markerCount - 1);
                native[slot].End(); recordingSlot = -1;
                passTiming?.CompleteNativeEnd(native[slot]);
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
                else if (!baseline)
                    for (int actor = 0; actor < scene.Owners.Length; actor++) lastSurfaces[actor] = scene.Owners[actor].Request.Surface;
                if (carry) batch!.CommitSubmitted(completion);
                cpuSubmit[id] = Stopwatch.GetElapsedTime(before).TotalMicroseconds;
                submitBytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
            }
            allocated[id] = prepareBytes + openingBytes + encodeBytes + submitBytes;
            LastAllocation = (prepareBytes, openingBytes, encodeBytes, submitBytes);
            dispatches[id] = commands[slot].Tally[GpuCommandKind.Dispatch];
            draws[id] = commands[slot].Tally[GpuCommandKind.Draw] + commands[slot].Tally[GpuCommandKind.DrawIndexed];
            if (carry)
            {
                Assert.Equal(5, dispatches[id]);
                surfaceBytes = batch!.Surface(scene.Owners[0], slots[0], -1).Current.SizeInBytes;
            }
            else if (!baseline)
            {
                scene.AssertPublished();
                surfaceBytes = scene.Owners[0].Request.Surface.Current.SizeInBytes;
                Assert.True(dispatches[id] >= 5);
            }
            if (!carry) Assert.True(draws[id] > 0);
        }

        internal void Harvest(long deadline)
        {
            try { HarvestCore(deadline); }
            catch (Exception error) { throw Failure(error); }
        }

        void HarvestCore(long deadline)
        {
            foreach (var wait in ring.AwaitAvailability(Stopwatch.GetTimestamp, deadline))
                Assert.True(wait.Wait.Ready);
            ring.PollCompleted();
            Assert.Null(ring.Failure);
        }

        internal Row Result()
        {
            try { return ResultCore(); }
            catch (Exception error) { throw Failure(error); }
        }

        Row ResultCore()
        {
            Assert.Equal(ConditioningFrames + observed, next); Assert.True(ring.CompleteBlock(), ring.Failure);
            for (int i = 0; i < observed; i++)
            {
                int id = ConditioningFrames + i;
                var time = timestamps[id] ?? throw new InvalidOperationException("A timestamp sample is missing.");
                Assert.True(time.Valid); Assert.True(time.Ticks[^1] > 0);
                var passes = passTiming?.Resolve(id, time.Ticks, time.Period!.Value) ?? [];
                if (passTiming is not null && device.Backend == GpuBackendKind.MetalNative)
                    Assert.All(passes, pass => Assert.True(pass.MetalClearAttributionAvailable));
                samples[i] = new(i, id, time.Ticks[^1] * time.Period!.Value / 1000, cpuPrepare[id], cpuEncode[id], cpuSubmit[id],
                    allocated[id], dispatches[id], draws[id], time.Ticks.ToArray(), time.Period.Value,
                    passTiming is null ? CoverageRecessionStageTiming.Resolve(time.Ticks, time.Period.Value)
                        : passes.Where(p => p.Pass.Id.StartsWith("carry.", StringComparison.Ordinal))
                            .Select(p => new CoverageRecessionStageTiming.StageSample(p.Pass.Id[6..], p.InclusiveGpuMicroseconds)).ToArray(),
                    passes, cpuPassRecording[id])
                { UnscopedDeferredClears = passTiming?.UnscopedDeferredClears(id) ?? [] };
            }
            var final = samples.Select(s => s!).ToArray();
            int k = batch?.SlotsPerActor ?? GpuRetireQueue.FramesInFlightFor(device) + 2;
            if (!baseline)
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
                    try
                    {
                        CoverageRecessionComputeGpuTests.AssertWords(scene.Fixture,
                            words.AsSpan((int)start, scene.Fixture.Topology.VertexCount * 16).ToArray(), 0, entry, Vector3.UnitZ, 1, Vector3.Zero);
                    }
                    catch (Xunit.Sdk.XunitException error)
                    { throw Failure(error, CoverageRecessionRowRefusal.ArithmeticMismatch); }
                }
            }
            return new(layout, scene.ActorCount, k, surfaceBytes,
                baseline ? 0 : (long)scene.ActorCount * scene.Fixture.Topology.VertexCount * 64,
                baseline ? 0 : 5, Stats(final.Select(s => s.GpuMicroseconds)),
                Stats(final.Select(s => s.CpuPreparationMicroseconds)), Stats(final.Select(s => s.CpuEncodingMicroseconds)),
                Stats(final.Select(s => s.CpuSubmissionMicroseconds)), Stats(final.Select(s => (double)s.WarmManagedBytes)), final);
        }

        void RequirePublished()
        {
            if (baseline || carry) return;
            foreach (var owner in scene.Owners)
                if (!owner.Request.Published) throw Failure(null, CoverageRecessionRowRefusal.CoverageNotPublished);
        }

        internal CoverageRecessionRowException Failure(Exception? error, CoverageRecessionRowRefusal reason = CoverageRecessionRowRefusal.OperationFailed)
        {
            if (error is CoverageRecessionRowException detailed) return detailed;
            return new(layout, actors, phase, frameNumber, reason,
                passTiming?.CarryBracketsSeen ?? stageTiming?.BracketsSeen ?? 0, staged || passTiming is not null && !baseline ? 10 : 0,
                currentSlot < 0 ? 0 : commands[currentSlot]?.Tally[GpuCommandKind.Dispatch] ?? 0,
                scene is null || carry || baseline ? [] : scene.Owners.Select(owner => scene.Scene.CoverageFailureFor(owner.Actor).Code).Distinct().ToArray(),
                observer.Failures.ToArray(), error);
        }

        public void Dispose()
        {
            if (disposed) return;
            // An unsubmitted native recording must close before the submission's teardown drain.
            // Dispose discards it. Never End or submit a partial timestamp batch.
            if (recordingSlot >= 0)
            { native[recordingSlot].Dispose(); native[recordingSlot] = null!; recordingSlot = -1; }
            scene?.Submission.Dispose();
            ring?.Dispose(); batch?.Dispose(); retired?.Dispose();
            foreach (var list in native) list?.Dispose();
            scene?.Dispose(); disposed = true;
        }
    }
}
