using System;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Gpu;

internal sealed record CoveragePerfComparison(string Name, int OffBlock, int OnBlock, double? DeltaMilliseconds,
    double? Percent, CoveragePerfBudgetStatus Status, ImmutableArray<string> Reasons)
{
    internal double? Microseconds => DeltaMilliseconds * 1000;
}
internal sealed record CoveragePerfGate(CoveragePerfBudgetStatus Status, ImmutableArray<string> Reasons,
    ImmutableArray<CoveragePerfComparison> Pairs, double? PooledDeltaMilliseconds = null, double? OrderDifferenceMilliseconds = null);
internal sealed record CoveragePerfVerdict(CoveragePerfGate Cpu, CoveragePerfGate Gpu,
    ImmutableArray<CoveragePerfComparison> SharedSubmission, CoveragePerfComparison? OneActor,
    string SharedSubmissionAcceptance, CoveragePerfAllocationProof AllocationProof)
{
    internal ImmutableArray<string> SharedSubmissionDisclosure { get; init; } = CoveragePerfSharedSubmission.Disclosure;
    internal ImmutableArray<CoveragePerfSharedSubmissionObservation> SharedSubmissionObservations { get; init; } = [];
    internal ImmutableArray<CoveragePerfWarmupSubmissionDeviations> WarmupSubmissionDeviations { get; init; } = [];
}

internal static class CoveragePerfBudgets
{
    internal static CoveragePerfVerdict Evaluate(CoveragePerfReport report)
    {
        var invalid = CoveragePerfValidity.Reasons(report);
        var cpu = Unproved("The documented 32-actor CPU gate requires C1.", invalid);
        var gpu = Unproved("The completed-frame GPU gate requires G1, G2 or G3 on its exact backend.", invalid);
        ImmutableArray<CoveragePerfComparison> shared = [];
        CoveragePerfComparison? one = null;
        switch (report.Manifest?.Preset)
        {
            case "C0":
                shared = [Compare(report, "empty shared submission", 0, 1, false, null, invalid, true)];
                one = Compare(report, "one actor observed delta, microseconds per actor", 2, 3, false, null, invalid);
                break;
            case "C1":
                cpu = Gate(report, false, 2, (1, 2), (4, 3), invalid);
                shared = [Compare(report, "32 actors direct then shared", 0, 1, false, null, invalid, true),
                    Compare(report, "32 actors shared then direct", 5, 4, false, null, invalid, true)];
                break;
            case "G1":
            case "G2":
            case "G3":
                gpu = Gate(report, true, 15, (4, 5), (7, 6), invalid);
                break;
        }
        return new(cpu, gpu, shared, one,
            "Shared-submission overhead has no threshold. Owner acceptance required. The 2 ms coverage budget does not apply.",
            report.AllocationProof)
        {
            SharedSubmissionObservations = CoveragePerfSharedSubmission.Observe(report),
            WarmupSubmissionDeviations = CoveragePerfSharedSubmission.WarmupDeviations(report),
        };
    }

    static CoveragePerfGate Gate(CoveragePerfReport report, bool gpu, double limit,
        (int Off, int On) first, (int Off, int On) second, ImmutableArray<string> invalid)
    {
        var pairs = ImmutableArray.Create(
            Compare(report, gpu ? "off then on GPU" : "off then on CPU", first.Off, first.On, gpu, limit, invalid, gpu),
            Compare(report, gpu ? "on then off GPU" : "on then off CPU", second.Off, second.On, gpu, limit, invalid, gpu));
        var reasons = pairs.SelectMany(p => p.Reasons).Distinct().ToImmutableArray();
        var status = pairs.Any(p => p.Status == CoveragePerfBudgetStatus.Unproved) ? CoveragePerfBudgetStatus.Unproved
            : pairs.Any(p => p.Status == CoveragePerfBudgetStatus.Fail) ? CoveragePerfBudgetStatus.Fail : CoveragePerfBudgetStatus.Pass;
        // Every approved block has the same even count, so this is also the pooled observation delta.
        double? pooled = status == CoveragePerfBudgetStatus.Unproved ? null :
            (pairs[0].DeltaMilliseconds + pairs[1].DeltaMilliseconds) / 2;
        double? difference = status == CoveragePerfBudgetStatus.Unproved ? null :
            pairs[0].DeltaMilliseconds - pairs[1].DeltaMilliseconds;
        return new(status, reasons, pairs, pooled, difference);
    }

    static CoveragePerfComparison Compare(CoveragePerfReport report, string name, int offIndex, int onIndex,
        bool gpu, double? limit, ImmutableArray<string> invalid, bool percentage = false)
    {
        var reasons = invalid.ToBuilder();
        var off = report.Blocks.FirstOrDefault(b => b.Block.Index == offIndex);
        var on = report.Blocks.FirstOrDefault(b => b.Block.Index == onIndex);
        if (off is null || on is null) reasons.Add($"Missing pair blocks {offIndex}/{onIndex}.");
        else reasons.AddRange(CoveragePerfValidity.PairReasons(off, on));
        if (reasons.Count != 0) return new(name, offIndex, onIndex, null, null, CoveragePerfBudgetStatus.Unproved, reasons.Distinct().ToImmutableArray());
        long frequency = report.Identity.Execution.ClockFrequency;
        var offValues = off!.Samples.Select(Value).ToArray();
        var onValues = on!.Samples.Select(Value).ToArray();
        if (offValues.Length == 0 || onValues.Length == 0 || offValues.Any(v => !v.HasValue) || onValues.Any(v => !v.HasValue))
            return new(name, offIndex, onIndex, null, null, CoveragePerfBudgetStatus.Unproved, ["Unresolved aggregate baseline or samples."]);
        double baseline = offValues.Average(v => v!.Value), covered = onValues.Average(v => v!.Value);
        double delta = covered - baseline;
        if (percentage && baseline <= 0)
            return new(name, offIndex, onIndex, delta, null, CoveragePerfBudgetStatus.Unproved, ["Nonpositive baseline cannot be a percentage denominator."]);
        double? percent = percentage ? 100 * delta / baseline : null;
        if (!double.IsFinite(delta) || percent is double p && !double.IsFinite(p))
            return new(name, offIndex, onIndex, null, null, CoveragePerfBudgetStatus.Unproved, ["Unresolved or nonfinite aggregate arithmetic."]);
        var status = !limit.HasValue ? CoveragePerfBudgetStatus.Unproved :
            (gpu ? percent!.Value : delta) <= limit.Value ? CoveragePerfBudgetStatus.Pass : CoveragePerfBudgetStatus.Fail;
        return new(name, offIndex, onIndex, delta, percent, status, limit.HasValue ? [] :
            [name.StartsWith("one actor", StringComparison.Ordinal) ? "Observed one-actor delta only. No extrapolation or gate." : "Owner acceptance required. No shared-submission threshold."]);

        double? Value(CoveragePerfFrameSample sample) => gpu ? CoveragePerfStatistics.GpuMilliseconds(sample)
            : CoveragePerfStatistics.CpuMilliseconds(sample, frequency);
    }

    static CoveragePerfGate Unproved(string reason, ImmutableArray<string> invalid) =>
        new(CoveragePerfBudgetStatus.Unproved, invalid.Add(reason), []);
}
