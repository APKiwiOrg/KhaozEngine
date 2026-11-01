using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfMarkdown;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Profile toggles share one unit's native environment. Backends remain separate series.
internal static class RendererPerfProfileMarkdown
{
    internal static string Profiles(IEnumerable<(RendererPerfBaselineLeg Input, RendererPerfSummary Summary)> legs)
    {
        var text = new StringBuilder("# Renderer profile deltas\n\nFormat khaozengine.renderer-profiles/1. Deltas are candidate minus baseline within one unit. Separate series, no cross-backend deltas.\n\n")
            .Append("Times are ns. Percent is the relative mean delta. PerPass totals are diagnostic. Three iterations are within-job repeats, not independent fleet samples or statistical significance. Combined rows are descriptive. No regression budget is assigned.\n\n")
            .Append("The M5 colour copy runs outside any named pass, so its cost appears only in `gpu.frame`. Ownership is tracked in [#1636](https://github.com/APKiwiOrg/KhaozEngine/issues/1636).\n");
        foreach (var leg in legs.OrderBy(l => l.Input.Leg, StringComparer.Ordinal))
        {
            string unit = leg.Summary.Raw.Identity.UnitId;
            text.Append("\n## ").Append(leg.Input.Leg).Append("\n\nUnit ").Append(unit)
                .Append(". [Run](").Append(leg.Input.RunUrl).Append("). [Summary](").Append(leg.Input.Leg).Append('-').Append(unit).Append(".md).\n\n")
                .Append("| Profile pair | Baseline scene | Candidate scene | Layout | Iteration | Metric | Mean delta | Median delta | p95 delta | p99 delta | Mean percent |\n")
                .Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
            foreach (var row in RendererPerfProfileComparison.Compare(leg.Summary))
                foreach (var metric in row.Deltas.Where(p => p.Key == "gpu.frame" || p.Key.StartsWith("gpu.pass.", StringComparison.Ordinal))
                    .OrderBy(p => p.Key == "gpu.frame" ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal))
                {
                    text.Append("| ").Append(Cell(row.Label)).Append(" | ").Append(row.BaselineSceneId).Append(" | ").Append(row.CandidateSceneId)
                        .Append(" | ").Append(row.Layout).Append(" | ").Append(row.Iteration?.ToString(CultureInfo.InvariantCulture) ?? "combined descriptive")
                        .Append(" | ").Append(Cell(metric.Key));
                    foreach (var value in new[] { metric.Value.Mean, metric.Value.Median, metric.Value.P95, metric.Value.P99, metric.Value.Percent })
                        text.Append(" | ").Append(Metric(value));
                    text.Append(" |\n");
                }
        }
        return text.ToString();
    }

    internal static string Index(IEnumerable<(RendererPerfBaselineLeg Input, RendererPerfSummary Summary)> legs)
    {
        var ordered = legs.OrderBy(l => l.Input.Leg, StringComparer.Ordinal).ToArray();
        var text = new StringBuilder("# Renderer profile index\n\nFormat khaozengine.renderer-profiles/1. Separate series, no cross-backend deltas.\n\n")
            .Append("| Leg | Unit | Summary | Actual billed minutes |\n| --- | --- | --- | --- |\n");
        foreach (var leg in ordered)
            text.Append("| ").Append(leg.Input.Leg).Append(" | ").Append(leg.Summary.Raw.Identity.UnitId).Append(" | [summary](")
                .Append(leg.Input.Leg).Append('-').Append(leg.Summary.Raw.Identity.UnitId).Append(".md) | ")
                .Append(leg.Input.BilledMinutes.ToString(CultureInfo.InvariantCulture)).Append(" |\n");
        text.Append("\n[Profile deltas](profiles-").Append(ordered[0].Summary.Raw.Identity.UnitId).Append('-')
            .Append(ordered[1].Summary.Raw.Identity.UnitId).Append(".md).\n");
        return text.ToString();
    }
}
