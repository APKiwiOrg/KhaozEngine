using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfAnalysis;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Shared arithmetic preserves availability, provenance and gap-model refusals for both comparison views.
internal static class RendererPerfDeltaArithmetic
{
    internal static ImmutableDictionary<string, RendererPerfDelta> Compare(
        ImmutableDictionary<string, RendererPerfDistribution>? baseline,
        ImmutableDictionary<string, RendererPerfDistribution>? candidate, IEnumerable<string> names,
        string? globalRefusal, out ImmutableArray<string> modelReasons)
    {
        var deltas = ImmutableDictionary.CreateBuilder<string, RendererPerfDelta>(StringComparer.Ordinal);
        var gapReasons = new List<string>();
        foreach (string name in names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var left = baseline?.GetValueOrDefault(name);
            var right = candidate?.GetValueOrDefault(name);
            string? refusal = globalRefusal;
            var refusalState = RendererPerfMetricStatus.ModelIncomplete;
            if (refusal is null && name.StartsWith("gap.", StringComparison.Ordinal))
            {
                string prefix = name[..name.LastIndexOf('.')];
                var modelA = baseline?.GetValueOrDefault(prefix + ".excess")?.Mean;
                var modelB = candidate?.GetValueOrDefault(prefix + ".excess")?.Mean;
                if (modelA?.Status != RendererPerfMetricStatus.Available || modelB?.Status != RendererPerfMetricStatus.Available)
                {
                    refusal = string.Join(" ", new[] { "Incomplete or invalid references cannot supply gap deltas.", modelA?.Reason, modelB?.Reason }
                        .Where(r => r is not null));
                    if (modelA?.Status == RendererPerfMetricStatus.Invalid || modelB?.Status == RendererPerfMetricStatus.Invalid)
                        refusalState = RendererPerfMetricStatus.Invalid;
                }
                else if (modelA.Source != modelB.Source)
                {
                    refusal = $"Unmatched reference/model provenance for {prefix}. Baseline {modelA.Source}. Candidate {modelB.Source}.";
                    gapReasons.Add(refusal);
                }
            }
            RendererPerfMetric Delta(RendererPerfMetric? x, RendererPerfMetric? y)
                => Difference(x, y, refusal, refusalState);
            var mean = Delta(left?.Mean, right?.Mean);
            RendererPerfMetric percentage = mean.Value is { } delta && left!.Mean.Value != 0
                ? Available(100 * (delta / left.Mean.Value!.Value), "percent", "candidate minus baseline mean / baseline mean * 100")
                : Unavailable("percent", "relative mean delta", mean.Reason ?? "The baseline mean is zero, so percentage is undefined.", mean.Status == RendererPerfMetricStatus.Available
                    ? RendererPerfMetricStatus.ModelIncomplete : mean.Status);
            deltas.Add(name, new(mean, Delta(left?.Median, right?.Median), Delta(left?.P95, right?.P95), Delta(left?.P99, right?.P99), percentage));
        }
        modelReasons = [.. gapReasons];
        return deltas.ToImmutable();
    }

    static RendererPerfMetric Difference(RendererPerfMetric? baseline, RendererPerfMetric? candidate, string? refusal,
        RendererPerfMetricStatus refusalState)
    {
        string unit = baseline?.Unit ?? candidate?.Unit ?? "unknown";
        const string source = "candidate minus baseline";
        if (refusal is not null) return Unavailable(unit, source, refusal, refusalState);
        if (baseline is null || candidate is null) return Unavailable(unit, source, "The metric is missing from baseline or candidate.");
        if (baseline.Status != RendererPerfMetricStatus.Available || candidate.Status != RendererPerfMetricStatus.Available)
            return Unavailable(unit, source, string.Join(" ", new[] { baseline.Reason, candidate.Reason }.Where(r => r is not null)),
                baseline.Status == RendererPerfMetricStatus.Invalid || candidate.Status == RendererPerfMetricStatus.Invalid
                    ? RendererPerfMetricStatus.Invalid : RendererPerfMetricStatus.ModelIncomplete);
        if (baseline.Unit != candidate.Unit || baseline.Source != candidate.Source)
            return Unavailable(unit, source, "Metric unit or source provenance differs.");
        return Available(candidate.Value!.Value - baseline.Value!.Value, unit, source);
    }

}
