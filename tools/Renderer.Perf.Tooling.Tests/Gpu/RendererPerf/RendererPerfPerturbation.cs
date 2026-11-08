using System;
using System.Linq;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfAnalysis;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Instrumentation sensitivity between matched layouts, never a causal marker tax or regression budget.
internal sealed record RendererPerfPerturbation(RendererPerfMetric DifferenceNs, RendererPerfMetric Percent,
    RendererPerfMetric ThresholdNs, bool? AboveThreshold)
{
    internal static RendererPerfPerturbation Evaluate(RendererPerfMetric? total, RendererPerfMetric? detailed)
    {
        const string source = "matched total-only and per-pass outer means";
        if (total?.Status != RendererPerfMetricStatus.Available || detailed?.Status != RendererPerfMetricStatus.Available)
        {
            string reason = string.Join(" ", new[] { "Matching valid total-only and per-pass means are required.", total?.Reason, detailed?.Reason }
                .Where(r => r is not null));
            var status = total?.Status == RendererPerfMetricStatus.Invalid || detailed?.Status == RendererPerfMetricStatus.Invalid
                ? RendererPerfMetricStatus.Invalid : RendererPerfMetricStatus.ModelIncomplete;
            var missing = Unavailable("ns", source, reason, status);
            return new(missing, missing with { Unit = "percent" }, missing, null);
        }
        double t = total.Value!.Value, d = detailed.Value!.Value;
        var difference = Available(d - t, "ns", "per-pass outer mean minus total-only outer mean");
        var percent = t == 0 ? Unavailable("percent", source, "The baseline mean is zero, so percentage is undefined.")
            : Available((d - t) / t * 100, "percent", "layout difference / total-only mean * 100");
        var threshold = Available(Math.Max(t * .05, 100000), "ns", "larger of 5 percent total-only mean or 0.10 ms");
        return new(difference, percent, threshold, difference.Value is { } delta && threshold.Value is { } limit ? Math.Abs(delta) > limit : null);
    }
}
