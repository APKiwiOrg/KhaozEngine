using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
[Trait("Category", "Diagnostic")]
public sealed partial class SwingCostDiagnostics(ITestOutputHelper output)
{
    const int WearerCount = 32;
    const int WarmFrames = 120;
    const int MeasuredFrames = 600;
    const string SmokeFramesVariable = "KE_SWING_COST_SMOKE_FRAMES";
    const int MaxReportedAllocationFrames = 8;
    // About two seconds of sequential frames and over half a second of parallel ones on the development Mac,
    // several times the tiering delay.
    const int SettleFrames = 1200;
    const int CloakChains = 5, CloakJoints = 5;
    const int StrapChains = 4, StrapJoints = 3;

    [Fact]
    public void Report_swing_preparation_cost()
    {
        (int warmFrames, int measuredFrames) = FrameCounts();
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(new RegionRun(0, 4));
        SwingCostPiece cloak = SwingCostFixture.LoadPiece(rig, "cloak", AttachmentLayer.Outerwear, "hip",
            CloakChains, CloakJoints, new Vector3(-.24f, 0, -.17f), linkStretch: .25f);
        SwingCostPiece straps = SwingCostFixture.LoadPiece(rig, "straps", AttachmentLayer.Garment, "knee",
            StrapChains, StrapJoints, new Vector3(-.18f, 0, .16f));
        LoadedBodyColliders colliders = SwingCostFixture.LoadColliders(rig);
        var (measurements, heldMeasurement, _) = MeasureOutfit(rig, body, colliders, [cloak, straps],
            WearerCount, warmFrames, measuredFrames, includeHeldSample: true);
        int parallelWorkerCount = Environment.ProcessorCount - 1;
        using var workers = new WorkerJobScheduler(parallelWorkerCount);
        var (layeredMeasurements, _, parallelLayeredMeasurements) =
            MeasureOutfit(rig, body, colliders, LayeredSwingCostOutfit.Load(rig),
                LayeredSwingCostOutfit.SimulatedWearers, warmFrames, measuredFrames, includeHeldSample: false, workers);

        string json = JsonSerializer.Serialize(new
        {
            diagnostic = nameof(SwingCostDiagnostics),
            wearerCount = WearerCount,
            settleFrames = measuredFrames == MeasuredFrames ? SettleFrames : 0,
            warmFrames,
            measuredFrames,
            metricDefinitions = new
            {
                frameMs = "Wall time for preparation and draw writing, excluding host posing and scene submission.",
                stepMeanMs = "Wall time in Step, including scheduling and the join in parallel cases.",
                writeDrawsMeanMs = "Wall time spent writing prepared draws on the caller.",
                summedWearerStepWallMeanMs = "Parallel only. Sum of caller batch preparation and wearer solve wall times, including preemption.",
                summedWearerStepCpuMeanMs = "Parallel only. Sum of caller batch preparation and wearer solve thread CPU times. Null if unavailable.",
                managedBytesAllocated = "All measuring-thread bytes in the frame window, including probes.",
                allocationBreakdown = "BatchStepBytes is the gross batch window. Fill and batch boundaries exclude surrounding timing probes. The wearer loop includes its timing probes.",
                probeOverheadBytes = "Residual outside the fill, batch and wearer loop windows, plus the batch and job wrapper probes.",
                schedulerBytes = "Bytes in the scheduler call on the measuring thread, excluding the caller job wrappers.",
                callerJobBytes = "Bytes inside wearer solve bodies that ran on the measuring thread.",
                batchOutsideSchedulerBytes = "Bytes in Step outside the scheduler wrapper, including preparation and scope cleanup.",
            },
            environment = new
            {
                processorCount = Environment.ProcessorCount,
                os = RuntimeInformation.OSDescription,
                runtime = RuntimeInformation.FrameworkDescription,
            },
            measurements,
            heldMeasurement,
            layeredSimulatedWearerCount = LayeredSwingCostOutfit.SimulatedWearers,
            layeredHeldWearerCount = WearerCount - LayeredSwingCostOutfit.SimulatedWearers,
            layeredMeasurements,
            parallelWorkerCount,
            parallelLayeredMeasurements,
            parallelWorkerAllocationProof = "WorkerJobSchedulerTests.Warm_fanout_allocates_zero_bytes_on_the_caller_and_every_worker",
        }, JsonSerializerOptions.Web);
        output.WriteLine(json);
    }

    static (Measurement[] Measurements, Measurement? HeldMeasurement, Measurement[]? ParallelMeasurements)
        MeasureOutfit(OutfitRig rig, LoadedBodyRegions body, LoadedBodyColliders colliders, SwingCostPiece[] pieces,
            int simulatedWearers, int warmFrames, int measuredFrames, bool includeHeldSample, IJobScheduler? scheduler = null)
    {
        var options = new ChainSimulationOptions();
        SwingCostWearer[] wearers = CreateWearers(rig, body, colliders, pieces, options);
        try
        {
            const int fastestFrameRate = 144;
            ReadOnlySpan<SwingCostWearer> simulated = wearers.AsSpan(0, simulatedWearers);
            // Frame zero resets motion. A very short smoke sample can finish warming before the first grid step.
            if ((warmFrames - 1) * (1f / fastestFrameRate) < options.MaxStepSeconds)
                PrimeSolver(simulated, options.MaxStepSeconds);

            Measurement[] measurements =
            [
                Measure(wearers, fastestFrameRate, warmFrames, measuredFrames, simulatedWearers),
                Measure(wearers, 60, warmFrames, measuredFrames, simulatedWearers),
            ];
            Measurement? heldMeasurement = includeHeldSample ?
                Measure(wearers, fastestFrameRate, warmFrames, measuredFrames, 0) : null;
            for (int index = 0; index < measurements.Length; index++)
            {
                Measurement sample = measurements[index];
                measurements[index] = sample with
                {
                    SegmentAxisQueriesPerSimulatedWearerStep =
                        CountQueries(simulated, options, 1f / sample.FrameRateHz, warmFrames, measuredFrames),
                };
            }
            Measurement[]? parallelMeasurements = scheduler is null ? null :
            [
                Measure(wearers, fastestFrameRate, warmFrames, measuredFrames, simulatedWearers, scheduler),
                Measure(wearers, 60, warmFrames, measuredFrames, simulatedWearers, scheduler),
            ];
            return (measurements, heldMeasurement, parallelMeasurements);
        }
        finally
        {
            DisposeWearers(wearers);
        }
    }

    static SwingCostWearer[] CreateWearers(OutfitRig rig, LoadedBodyRegions body, LoadedBodyColliders colliders,
        SwingCostPiece[] pieces, ChainSimulationOptions options)
    {
        var wearers = new SwingCostWearer[WearerCount];
        try
        {
            for (int index = 0; index < wearers.Length; index++)
                wearers[index] = new SwingCostWearer(rig, body, pieces, colliders, options, index, WearerCount);
            return wearers;
        }
        catch
        {
            DisposeWearers(wearers);
            throw;
        }
    }

    static void DisposeWearers(SwingCostWearer[] wearers)
    {
        foreach (SwingCostWearer wearer in wearers) wearer?.Dispose();
    }

    static (int Warm, int Measured) FrameCounts()
    {
        string? value = Environment.GetEnvironmentVariable(SmokeFramesVariable);
        if (value is null) return (WarmFrames, MeasuredFrames);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
            count < 1 || count >= WarmFrames)
            throw new ArgumentException($"{SmokeFramesVariable} must be a frame count from 1 through {WarmFrames - 1}.");
        return (count, count);
    }

    static void PrimeSolver(ReadOnlySpan<SwingCostWearer> wearers, float stepTime)
    {
        foreach (SwingCostWearer wearer in wearers)
        {
            wearer.Reset(MotionLevel.Simulated);
            wearer.Pose(0);
            wearer.Step(0);
            wearer.Pose(stepTime);
            wearer.Step(stepTime);
            wearer.WriteDraws();
        }
    }

    static Measurement Measure(SwingCostWearer[] wearers, int frameRate, int warmFrames, int measuredFrames,
        int simulatedWearers, IJobScheduler? scheduler = null,
        SwingCostWorkerAllocationWindow? workerAllocations = null)
    {
        bool measureTiming = measuredFrames == MeasuredFrames;
        var timedJobs = scheduler is null ? null : new TimedWearerJobs(scheduler, wearers.Length, measureTiming);
        var batch = timedJobs is null ? null : new WornMotionBatch(timedJobs);
        float frameTime = 1f / frameRate;
        // A path that is new to the process, such as the parallel batch, can still be tiering up after the warm
        // frames, and its one-time compilation work then lands in the window. Settle it first, then restart from
        // rest so the warm frames and the window cover the same poses as every other case.
        if (measureTiming)
        {
            ResetWearers(wearers, simulatedWearers);
            RunFrames(wearers, batch, frameTime, SettleFrames);
        }
        ResetWearers(wearers, simulatedWearers);
        RunFrames(wearers, batch, frameTime, warmFrames);
        foreach (SwingCostWearer wearer in wearers) wearer.ValidateDraws();

        var ticks = measureTiming ? new long[measuredFrames] : [];
        // A frame that allocates is reported with the stage that allocated, so a nonzero total names its source.
        var allocationFrames = new AllocationFrame[MaxReportedAllocationFrames];
        int allocationFrameCount = 0;
        long allocated = 0, stepTicks = 0, drawTicks = 0, summedWearerWallTicks = 0;
        long? summedWearerCpuNanoseconds = 0;
        workerAllocations?.Begin();
        using (AllocationRegion.Start("swing preparation frames"))
        {
            for (int frame = 0; frame < measuredFrames; frame++)
            {
                foreach (SwingCostWearer wearer in wearers) wearer.Pose((warmFrames + frame) * frameTime);

                long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
                long start = measureTiming ? Stopwatch.GetTimestamp() : 0;
                long beforeBatch = 0, fillBytes = 0, batchStartBytes = 0, batchEndBytes = 0;
                long? beforeBatchCpu = null;
                if (batch is not null)
                {
                    long beforeFill = GC.GetAllocatedBytesForCurrentThread();
                    FillBatch(batch, wearers, frameTime);
                    fillBytes = GC.GetAllocatedBytesForCurrentThread() - beforeFill;
                    beforeBatch = measureTiming ? Stopwatch.GetTimestamp() : 0;
                    beforeBatchCpu = measureTiming ? ThreadCpuClock.Read() : null;
                    batchStartBytes = GC.GetAllocatedBytesForCurrentThread();
                    batch.Step();
                    batchEndBytes = GC.GetAllocatedBytesForCurrentThread();
                    if (measureTiming) stepTicks += Stopwatch.GetTimestamp() - beforeBatch;
                }

                long beforeWearers = GC.GetAllocatedBytesForCurrentThread();
                foreach (SwingCostWearer wearer in wearers)
                {
                    long beforeStep = measureTiming ? Stopwatch.GetTimestamp() : 0;
                    if (batch is null) wearer.Step(frameTime);
                    long afterStep = batch is null && measureTiming ? Stopwatch.GetTimestamp() : beforeStep;
                    wearer.WriteDraws();
                    long afterDraws = measureTiming ? Stopwatch.GetTimestamp() : 0;
                    stepTicks += afterStep - beforeStep;
                    drawTicks += afterDraws - afterStep;
                }
                long wearerLoopBytes = GC.GetAllocatedBytesForCurrentThread() - beforeWearers;
                long end = measureTiming ? Stopwatch.GetTimestamp() : 0;
                long frameBytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
                allocated += frameBytes;
                if (frameBytes != 0 && allocationFrameCount < allocationFrames.Length)
                {
                    long batchStepBytes = batchEndBytes - batchStartBytes;
                    long probeBytes = frameBytes - fillBytes - batchStepBytes - wearerLoopBytes;
                    var split = timedJobs?.SplitCallerAllocation(batchStartBytes, batchEndBytes);
                    allocationFrames[allocationFrameCount++] = new AllocationFrame(frame, fillBytes,
                        batchStepBytes, wearerLoopBytes, probeBytes + (split?.ProbeBytes ?? 0))
                    {
                        BatchOutsideSchedulerBytes = split?.BatchOutsideSchedulerBytes,
                        SchedulerBytes = split?.SchedulerBytes,
                        CallerJobBytes = split?.CallerJobBytes,
                        CallerJob = split is { FirstAllocatingJob: >= 0 } ? split.Value.FirstAllocatingJob : null,
                    };
                }
                if (!measureTiming) continue;
                ticks[frame] = end - start;
                if (timedJobs is not null)
                {
                    var (wallTicks, cpuNanoseconds) = timedJobs.SumJobTimes();
                    summedWearerWallTicks += timedJobs.StartedAt - beforeBatch + wallTicks;
                    summedWearerCpuNanoseconds += timedJobs.StartedCpu - beforeBatchCpu + cpuNanoseconds;
                }
            }
        }
        workerAllocations?.End();
        foreach (SwingCostWearer wearer in wearers) wearer.ValidateDraws();

        AllocationFrame[]? reportedFrames = allocationFrameCount == 0 ? null : allocationFrames[..allocationFrameCount];
        if (!measureTiming)
            return new Measurement(frameRate, null, null, null, null, allocated, null, null)
            {
                AllocationFrames = reportedFrames,
            };

        Array.Sort(ticks);
        double msPerTick = 1000d / Stopwatch.Frequency;
        return new Measurement(frameRate, ticks.Average() * msPerTick, Percentile(ticks, .50) * msPerTick,
            Percentile(ticks, .95) * msPerTick, ticks[^1] * msPerTick, allocated,
            stepTicks * msPerTick / measuredFrames, drawTicks * msPerTick / measuredFrames)
        {
            SummedWearerStepWallMeanMs = timedJobs is null ? null : summedWearerWallTicks * msPerTick / measuredFrames,
            SummedWearerStepCpuMeanMs = timedJobs is null ? null : summedWearerCpuNanoseconds / (1_000_000d * measuredFrames),
            AllocationFrames = reportedFrames,
        };
    }

    static void ResetWearers(SwingCostWearer[] wearers, int simulatedWearers)
    {
        for (int index = 0; index < wearers.Length; index++)
            wearers[index].Reset(index < simulatedWearers ? MotionLevel.Simulated : MotionLevel.Held);
    }

    static void RunFrames(SwingCostWearer[] wearers, WornMotionBatch? batch, float frameTime, int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            foreach (SwingCostWearer wearer in wearers) wearer.Pose(frame * frameTime);
            if (batch is not null)
            {
                FillBatch(batch, wearers, frameTime);
                batch.Step();
            }
            foreach (SwingCostWearer wearer in wearers)
            {
                if (batch is null) wearer.Step(frameTime);
                wearer.WriteDraws();
            }
        }
    }

    static void FillBatch(WornMotionBatch batch, SwingCostWearer[] wearers, float frameTime)
    {
        batch.Clear();
        foreach (SwingCostWearer wearer in wearers) wearer.AddTo(batch, frameTime);
    }

    static double? CountQueries(ReadOnlySpan<SwingCostWearer> wearers, ChainSimulationOptions options,
        float frameTime, int warmFrames, int measuredFrames)
    {
        // Replay only the simulated wearers. Their clocks start together and the first frame resets to rest.
        // The circling walk has no gaps or teleports, so all wearers take the same fixed substeps after frame zero.
        foreach (SwingCostWearer wearer in wearers) wearer.Reset(MotionLevel.Simulated);
        var clock = new ChainStepClock(options.MaxStepSeconds, options.MaxStepsPerFrame);
        for (int frame = 0; frame < warmFrames; frame++)
        {
            if (frame > 0) clock.Advance(frameTime, out _);
            foreach (SwingCostWearer wearer in wearers)
            {
                wearer.Pose(frame * frameTime);
                wearer.Step(frameTime);
            }
        }

        var previousCounts = ChainContactCounts.Active;
        var counts = new ChainContactCounts();
        ChainContactCounts.Active = counts;
        try
        {
            int steps = 0;
            for (int frame = 0; frame < measuredFrames; frame++)
            {
                steps += clock.Advance(frameTime, out _);
                foreach (SwingCostWearer wearer in wearers)
                {
                    wearer.Pose((warmFrames + frame) * frameTime);
                    wearer.Step(frameTime);
                }
            }
            double samples = (double)steps * wearers.Length;
            return samples == 0 ? null : counts.SegmentAxis / samples;
        }
        finally
        {
            ChainContactCounts.Active = previousCounts;
        }
    }

    static long Percentile(long[] sorted, double rank) => sorted[(int)Math.Ceiling(rank * sorted.Length) - 1];

    sealed record Measurement(int FrameRateHz, double? MeanMs, double? P50Ms, double? P95Ms, double? MaxMs,
        long ManagedBytesAllocated, double? StepMeanMs, double? WriteDrawsMeanMs)
    {
        public double? SegmentAxisQueriesPerSimulatedWearerStep { get; init; }
        public double? SummedWearerStepWallMeanMs { get; init; }
        public double? SummedWearerStepCpuMeanMs { get; init; }
        public AllocationFrame[]? AllocationFrames { get; init; }
    }

    // BatchStepBytes is the gross batch window. Its detailed fields separate work from the probe residual.
    sealed record AllocationFrame(int Frame, long FillBytes, long BatchStepBytes, long WearerLoopBytes, long ProbeOverheadBytes)
    {
        public long? BatchOutsideSchedulerBytes { get; init; }
        public long? SchedulerBytes { get; init; }
        public long? CallerJobBytes { get; init; }
        public int? CallerJob { get; init; }
    }
}
