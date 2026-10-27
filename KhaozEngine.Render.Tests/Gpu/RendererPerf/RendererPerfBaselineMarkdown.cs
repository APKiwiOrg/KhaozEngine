using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfMarkdown;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Published views use verified arithmetic and canonical provenance, without frames or local paths.
internal static class RendererPerfBaselineMarkdown
{
    internal static string Summary(RendererPerfBaselineLeg input, RendererPerfSummary summary)
    {
        var report = summary.Raw;
        var text = new StringBuilder("# Renderer baseline\n\nFormat khaozengine.renderer-baseline/1.\n\n");
        text.Append("Series ").Append(input.Leg).Append(". Unit ").Append(report.Identity.UnitId).Append(".\n\n")
            .Append("Run [").Append(input.RunUrl).Append("](").Append(input.RunUrl).Append("). Attempt ")
            .Append(report.Identity.Attempt.ToString(CultureInfo.InvariantCulture)).Append(".\n\nActual billed minutes ")
            .Append(input.BilledMinutes.ToString(CultureInfo.InvariantCulture)).Append(".\n\n");
        text.Append("## Verified provenance\n\n```json\n").Append(Canonical(new
        {
            reportSha256 = summary.VerifiedArtifact!.ReportSha256,
            report.Manifest,
            report.Identity,
            report.Recipes,
            report.Sidecars,
            controlEvidence = report.Controls.Select(c => new { c.Control.Id, c.StateSha256, c.Metrics }).ToArray(),
            compilerProduction = report.Compiler.Production,
            compilerDiagnostic = report.Compiler.Diagnostic,
            compilerState = report.Compiler.State,
            compilerCounts = report.Compiler.Counts,
            compilerReports = report.Pipelines.Select(p => new
            {
                p.Identity,
                p.Status,
                p.Reason,
                executables = p.Executables.Length,
                statistics = p.Executables.SelectMany(e => e.Statistics).GroupBy(s => s.Metric.Status).OrderBy(g => g.Key)
                    .Select(g => new { status = g.Key, count = g.Count() }).ToArray()
            }).ToArray(),
            report.Publication,
            memory = new { observations = report.Memory.Observations.Length, report.Memory.Peaks, report.Memory.Process, report.Memory.Native },
            references = summary.References
        })).Append("\n```\n\n");
        text.Append("## Total-only scene results\n\nMeasured distributions only. Times are ns. No trimming or fastest-iteration selection.\n");
        Header(text);
        foreach (var row in summary.Combined.Where(r => r.Layout == RendererPerfLayout.TotalOnly).OrderBy(r => r.SceneId, StringComparer.Ordinal))
            foreach (string metric in new[] { "gpu.frame", "cpu.frame" })
                Distribution(text, row.SceneId, metric, row.Metrics[metric]);
        text.Append("\n## Within-job repeatability\n\nThree iterations are within-job only, not independent fleet samples or statistical significance.\n\n")
            .Append("| Scene | Layout | Iteration means |\n| --- | --- | --- |\n");
        foreach (var row in summary.Combined.OrderBy(r => r.SceneId, StringComparer.Ordinal).ThenBy(r => r.Layout))
            text.Append("| ").Append(row.SceneId).Append(" | ").Append(row.Layout).Append(" | ")
                .AppendJoin(", ", row.IterationMeans.Select(Metric)).Append(" |\n");
        text.Append("\n## Per-pass diagnostics and models\n\nPerPass frame totals are diagnostic. They do not replace TotalOnly results.\n");
        Header(text);
        foreach (var row in summary.Combined.Where(r => r.Layout == RendererPerfLayout.PerPass).OrderBy(r => r.SceneId, StringComparer.Ordinal))
            foreach (var metric in row.Metrics.Where(p => p.Key == "gpu.frame" || p.Key.StartsWith("gpu.pass.", StringComparison.Ordinal)
                || p.Key.StartsWith("gpu.exclusive.", StringComparison.Ordinal) || p.Key == "gpu.unattributed"
                || p.Key.StartsWith("gap.", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal))
                Distribution(text, row.SceneId, metric.Key, metric.Value);
        text.Append("\nModel assessments retain support states, dominant resources, uncertainty and missing evidence.\n\n")
            .Append("| Scene | Model | Status | Dominant resources | Range provenance and limitations | Missing evidence |\n| --- | --- | --- | --- | --- | --- |\n");
        var models = summary.Blocks.SelectMany(b => b.GapEvidence.Select(p => new { b.Block.SceneId, Model = p.Key, Evidence = p.Value }))
            .GroupBy(p => (p.SceneId, p.Model)).OrderBy(g => g.Key.SceneId, StringComparer.Ordinal).ThenBy(g => g.Key.Model, StringComparer.Ordinal);
        foreach (var model in models)
        {
            var evidence = model.SelectMany(m => m.Evidence).ToArray();
            var ranges = evidence.Select(e => e.CalibratedRange).ToArray();
            string minimum = ranges.All(r => r?.Minimum is not null) ? ranges.Min(r => r!.Minimum)!.Value.ToString("R", CultureInfo.InvariantCulture) : "unavailable";
            string maximum = ranges.All(r => r?.Maximum is not null) ? ranges.Max(r => r!.Maximum)!.Value.ToString("R", CultureInfo.InvariantCulture) : "unavailable";
            text.Append("| ").Append(model.Key.SceneId).Append(" | ").Append(Cell(model.Key.Model)).Append(" | ")
                .Append(Cell(Join(evidence.Select(e => e.Status.ToString())))).Append(" | ")
                .Append(Cell(Join(evidence.Select(e => e.DominantResource)))).Append(" | ")
                .Append(Cell(Join(evidence.Select(e => e.CalibratedRange is { } r ? r.Provenance + ", " + r.Source + ", " + r.Reason : "unavailable"))))
                .Append(". Observed calibrated range envelope ").Append(minimum).Append(" to ").Append(maximum).Append(" ns")
                .Append(" | ").Append(Cell(Join(evidence.SelectMany(e => e.MissingEvidence)))).Append(" |\n");
        }
        text.Append("\n## Controls\n\nOuter durations remain separate from scene frames. Unmatched or unsupported rates retain their causes.\n");
        Header(text);
        foreach (var control in summary.Controls.OrderBy(c => int.Parse(c.Block.SceneId.AsSpan(8), CultureInfo.InvariantCulture)))
        {
            foreach (var metric in control.Metrics.Where(p => p.Key == "gpu.frame" || p.Key.StartsWith("achieved.", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal))
                Distribution(text, control.Block.SceneId, metric.Key, metric.Value);
            ControlResolution(text, control);
        }
        text.Append("\n## Marker perturbation\n\n| Scene/iteration | Difference | Percent | Threshold | Above threshold |\n| --- | --- | --- | --- | --- |\n");
        foreach (var row in summary.Perturbations.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append("| ").Append(Cell(row.Key)).Append(" | ").Append(Metric(row.Value.DifferenceNs)).Append(" | ")
                .Append(Metric(row.Value.Percent)).Append(" | ").Append(Metric(row.Value.ThresholdNs)).Append(" | ")
                .Append(row.Value.AboveThreshold?.ToString() ?? "unavailable").Append(" |\n");
        Excesses(text, summary);
        text.Append("\n## Limitations\n\n")
            .Append("- Three iterations establish within-job repeatability only. No universal regression budget is assigned.\n")
            .Append("- Linux Vulkan and Windows D3D11 are separate series. No cross-backend deltas are implied.\n")
            .Append("- Missing references remain ModelIncomplete. Unsupported compiler counters remain unavailable. Logical payload is not exact off-chip traffic.\n")
            .Append("- Actual billed minutes are supplied by the operator. Elapsed-time estimates and native accounting remain distinct in provenance.\n")
            .Append("- [#1547](https://github.com/APKiwiOrg/KhaozEngine/issues/1547) remains the partial-write accounting proof limit. Successful publication does not close it.\n")
            .Append("- Optimization, vendor profiling, Windows Vulkan, consumer assets and equipment fit, adoption and release require separate work and approval.\n")
            .Append("- Original registered files in the linked workflow run remain the reproduction authority. Verification ends when its retained handles close.\n");
        return text.ToString();
    }

    static void Excesses(StringBuilder text, RendererPerfSummary summary)
    {
        text.Append("\n## Largest measured excesses\n\n");
        var excesses = summary.Combined.Where(r => r.Layout == RendererPerfLayout.PerPass)
            .SelectMany(r => r.Metrics.Where(p => p.Key.StartsWith("gap.", StringComparison.Ordinal) && p.Key.EndsWith(".excess", StringComparison.Ordinal)
                && p.Value.Mean.Status == RendererPerfMetricStatus.Available && p.Value.Mean.Value is not null)
                .Select(p => new { r.SceneId, Metric = p.Key, Value = p.Value.Mean }))
            .OrderByDescending(p => p.Value.Value).ThenBy(p => p.SceneId, StringComparer.Ordinal).ThenBy(p => p.Metric, StringComparer.Ordinal).Take(10).ToArray();
        if (excesses.Length == 0)
        {
            text.Append("No available measured excess. Collect explicit matched work, control rates, clocks and compiler-retention evidence before ranking investigations.\n");
            return;
        }
        text.Append("Available mean excesses only, observed minus calibrated reference, including negative values. Suggestions are leads, not causes.\n\n")
            .Append("| Scene | Metric | Mean excess | Suggested investigation |\n| --- | --- | --- | --- |\n");
        foreach (var row in excesses)
            text.Append("| ").Append(row.SceneId).Append(" | ").Append(Cell(row.Metric)).Append(" | ").Append(Metric(row.Value))
                .Append(" | Investigate matched work and rate assumptions, retained CPU partitions, synchronization and clock evidence for this pass before a separately approved capture. |\n");
    }

    internal static string Index(IEnumerable<(RendererPerfBaselineLeg Input, RendererPerfSummary Summary)> legs)
    {
        var text = new StringBuilder("# Renderer baseline index\n\nFormat khaozengine.renderer-baseline/1. Separate series, no cross-backend deltas.\n\n")
            .Append("| Leg | Unit | Summary | Actual billed minutes |\n| --- | --- | --- | --- |\n");
        foreach (var leg in legs.OrderBy(l => l.Input.Leg, StringComparer.Ordinal))
            text.Append("| ").Append(leg.Input.Leg).Append(" | ").Append(leg.Summary.Raw.Identity.UnitId).Append(" | [summary](")
                .Append(leg.Input.Leg).Append('-').Append(leg.Summary.Raw.Identity.UnitId).Append(".md) | ")
                .Append(leg.Input.BilledMinutes.ToString(CultureInfo.InvariantCulture)).Append(" |\n");
        return text.ToString();
    }

    static string Join(IEnumerable<string> values) => string.Join(" ", values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    static string Canonical<T>(T value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, JsonSerializer.SerializeToElement(value, RendererPerfManifest.JsonOptions));
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var element in value.EnumerateArray()) Write(writer, element); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
}
