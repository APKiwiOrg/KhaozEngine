using System;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Gpu;

internal sealed record CoveragePerfDistribution(int Count, double Mean, double Median, double P95, double Minimum, double Maximum);
internal sealed record CoveragePerfPoseMean(int PoseId, int Count, double Mean);
internal sealed record CoveragePerfBlockSummary(int Index, CoveragePerfDistribution? Cpu, CoveragePerfDistribution? Gpu,
    ImmutableArray<CoveragePerfPoseMean> PerPoseCpu, CoveragePerfDistribution? WarmupCpu,
    int WarmupProductionWaits, long WarmupResourceGrowth)
{
    internal ImmutableArray<CoveragePerfPoseMean> PerPoseGpu { get; init; } = [];
    internal CoveragePerfDistribution? WarmupGpu { get; init; }
    internal int WarmupFrames { get; init; }
    internal int MeasuredFrames { get; init; }
    internal int HarnessDrains { get; init; }
    internal long HarnessDrainTicks { get; init; }
    internal long ObservedAllocatedBytes { get; init; }
    internal CoveragePerfSubmissionSummary? WarmupSubmission { get; init; }
    internal CoveragePerfSubmissionSummary? MeasuredSubmission { get; init; }
    internal CoveragePerfWallSummary? WarmupWall { get; init; }
    internal CoveragePerfWallSummary? MeasuredWall { get; init; }
}

/// <summary>
/// Wall diagnostics in milliseconds over recorded raw values only. They are never part of cpu.framePreparation or a gate.
/// </summary>
internal sealed record CoveragePerfWallSummary(int Frames, CoveragePerfDistribution? FrameWall, int? MaxFrameWallFrame,
    CoveragePerfDistribution? FrameEntryToAggregate, CoveragePerfDistribution? PresentToHostReturn,
    CoveragePerfDistribution? HostReturnToFrameEnd);

internal static class CoveragePerfStatistics
{
    internal static CoveragePerfDistribution Summarize(ImmutableArray<double> values)
    {
        if (values.IsDefaultOrEmpty || values.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("A distribution requires finite, nonempty raw observations.", nameof(values));
        var sorted = values.Order().ToArray();
        int count = sorted.Length;
        double median = count % 2 == 0 ? sorted[count / 2 - 1] / 2 + sorted[count / 2] / 2 : sorted[count / 2];
        return new(count, values.Average(), median, sorted[(int)Math.Ceiling(.95 * count) - 1], sorted[0], sorted[^1]);
    }

    internal static CoveragePerfBlockSummary Summarize(CoveragePerfBlockResult block, long frequency) => new(
        block.Block.Index, Distribution(block.Samples, s => CpuMilliseconds(s, frequency)),
        Distribution(block.Samples, GpuMilliseconds), PoseMeans(block.Samples, s => CpuMilliseconds(s, frequency)),
        Distribution(block.Warmup, s => CpuMilliseconds(s, frequency)), block.Warmup.Sum(s => s.ProductionWaits),
        block.Warmup.Sum(s => s.ResourceGrowth))
    {
        PerPoseGpu = PoseMeans(block.Samples, GpuMilliseconds),
        WarmupGpu = Distribution(block.Warmup, GpuMilliseconds),
        WarmupFrames = block.Warmup.Length,
        MeasuredFrames = block.Samples.Length,
        HarnessDrains = block.Warmup.Sum(s => s.HarnessDrains) + block.Samples.Sum(s => s.HarnessDrains),
        HarnessDrainTicks = block.Warmup.Sum(s => s.HarnessDrainTicks) + block.Samples.Sum(s => s.HarnessDrainTicks),
        ObservedAllocatedBytes = block.Samples.Sum(s => s.AllocatedBytes),
        WarmupSubmission = CoveragePerfSharedSubmission.Summarize(block.Warmup),
        MeasuredSubmission = CoveragePerfSharedSubmission.Summarize(block.Samples),
        WarmupWall = Wall(block.Warmup, frequency),
        MeasuredWall = Wall(block.Samples, frequency),
    };

    internal static CoveragePerfWallSummary Wall(ImmutableArray<CoveragePerfFrameSample> samples, long frequency)
    {
        int? maxFrame = null;
        long maxTicks = 0;
        foreach (var sample in samples)
            if (sample.FrameWallTicks is long ticks && ticks >= 0 && (maxFrame is null || ticks > maxTicks))
            { maxTicks = ticks; maxFrame = sample.FrameIndex; }
        return new(samples.Length, Recorded(samples, s => s.FrameWallTicks, frequency), maxFrame,
            Recorded(samples, s => s.FrameEntryToAggregateTicks, frequency),
            Recorded(samples, s => s.PresentToHostReturnTicks, frequency),
            Recorded(samples, s => s.HostReturnToFrameEndTicks, frequency));
    }

    static CoveragePerfDistribution? Recorded(ImmutableArray<CoveragePerfFrameSample> samples,
        Func<CoveragePerfFrameSample, long?> select, long frequency)
    {
        if (frequency <= 0) return null;
        var values = samples.Select(select).Where(v => v is >= 0).Select(v => v!.Value * 1000d / frequency).ToImmutableArray();
        return values.IsEmpty ? null : Summarize(values);
    }

    internal static double? CpuMilliseconds(CoveragePerfFrameSample sample, long frequency) =>
        frequency > 0 && sample.CpuFramePreparationTicks >= 0 ? sample.CpuFramePreparationTicks * 1000d / frequency : null;

    internal static double? GpuMilliseconds(CoveragePerfFrameSample sample)
    {
        if (!sample.CompletedFrameTail || sample.Gpu is not { Valid: true, Disjoint: false } gpu || gpu.Ticks.IsDefaultOrEmpty ||
            gpu.Ticks.Length != (gpu.Layout == CoveragePerfTimerLayout.Total ? 2 : gpu.Layout == CoveragePerfTimerLayout.Phases ? 5 : 0) ||
            gpu.Ticks.Length < 2 || !double.IsFinite(gpu.PeriodNanoseconds) || gpu.PeriodNanoseconds <= 0) return null;
        for (int i = 1; i < gpu.Ticks.Length; i++) if (gpu.Ticks[i] < gpu.Ticks[i - 1]) return null;
        double milliseconds = (gpu.Ticks[^1] - gpu.Ticks[0]) * gpu.PeriodNanoseconds / 1000000;
        return double.IsFinite(milliseconds) ? milliseconds : null;
    }

    static CoveragePerfDistribution? Distribution(ImmutableArray<CoveragePerfFrameSample> samples,
        Func<CoveragePerfFrameSample, double?> select)
    {
        var values = samples.Select(select).ToArray();
        return values.Length == 0 || values.Any(v => !v.HasValue || !double.IsFinite(v.Value))
            ? null : Summarize(values.Select(v => v!.Value).ToImmutableArray());
    }

    static ImmutableArray<CoveragePerfPoseMean> PoseMeans(ImmutableArray<CoveragePerfFrameSample> samples,
        Func<CoveragePerfFrameSample, double?> select) => samples.GroupBy(s => s.PoseId).OrderBy(g => g.Key)
        .Select(g => (g, distribution: Distribution(g.ToImmutableArray(), select))).Where(p => p.distribution is not null)
        .Select(p => new CoveragePerfPoseMean(p.g.Key, p.distribution!.Count, p.distribution.Mean)).ToImmutableArray();
}
