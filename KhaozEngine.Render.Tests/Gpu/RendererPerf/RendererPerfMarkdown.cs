using System;
using System.Globalization;
using System.Linq;
using System.Text;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Compact views supplement raw artifacts. Invariant round-trip numbers retain exact units.
internal static class RendererPerfMarkdown
{
    internal static string Summary(RendererPerfSummary summary)
    {
        summary = summary.VerifiedArtifact is { } artifact && ReferenceEquals(summary.Raw, artifact.Report)
            ? RendererPerfAnalysis.Analyze(artifact, summary.References)
            : RendererPerfAnalysis.Analyze(summary.Raw, summary.References);
        var report = summary.Raw;
        var text = new StringBuilder("# Renderer performance summary\n\n");
        text.Append("Unit ").Append(Cell(report.Identity.UnitId)).Append(". Publication ").Append(report.Publication.State)
            .Append(". Promotion eligible ").Append(summary.PromotionEligible).Append(".\n\n");
        text.Append("Run ").Append(Cell(report.Identity.RunId)).Append(" attempt ").Append(report.Identity.Attempt)
            .Append(". Backend ").Append(Cell(report.Identity.Backend)).Append(". Adapter ").Append(Cell(report.Identity.Adapter))
            .Append(". Driver ").Append(Cell(report.Identity.Driver)).Append(".\n\n");
        foreach (string reason in summary.Reasons) text.Append("- ").Append(Cell(reason)).Append('\n');
        text.Append("\nMeasured distributions only. p95/p99 use nearest rank ceil(p*N). Standard deviation is population.\n")
            .Append("Combined frames are descriptive. Iteration means remain separate replicates, with no significance claim.\n\n");
        foreach (var block in summary.Blocks.Concat(summary.Controls))
        {
            string row = $"{block.Block.SceneId}/{block.Block.Layout}/{block.Block.Iteration}";
            Header(text);
            foreach (var metric in block.Metrics.Where(p => Display(p.Key, p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal))
                Distribution(text, row, metric.Key, metric.Value);
            if (!block.GcFrames.IsEmpty) text.Append("\nGC-associated frame IDs ").Append(Cell(row)).Append(": ")
                .AppendJoin(", ", block.GcFrames).Append("\n\n");
            foreach (var gap in block.GapEvidence.OrderBy(p => p.Key, StringComparer.Ordinal))
                foreach (var evidence in gap.Value.DistinctBy(e => (e.Status, e.DominantResource, e.CalibratedRange, Missing: string.Join(" ", e.MissingEvidence))))
                    text.Append("\nGap ").Append(Cell(row + "/" + gap.Key)).Append(" ").Append(evidence.Status)
                        .Append(". Peak lower bound ").Append(Metric(evidence.PeakLowerBoundNs))
                        .Append(". Calibrated ").Append(Metric(evidence.CalibratedReferenceNs))
                        .Append(". Dominant resource ").Append(Cell(evidence.DominantResource))
                        .Append(". Range ").Append(evidence.CalibratedRange is { } range
                            ? Cell($"{Number(range.Minimum)} to {Number(range.Maximum)} {range.Unit}, {range.Provenance}, {range.Source}, {range.Reason}") : "unavailable")
                        .Append(". ").Append(Cell(string.Join(" ", evidence.MissingEvidence))).Append('\n');
        }
        text.Append("\nCombined descriptive distributions\n\n");
        foreach (var row in summary.Combined)
        {
            Header(text);
            foreach (var metric in row.Metrics.Where(p => Display(p.Key, p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal))
                Distribution(text, row.SceneId + "/" + row.Layout, metric.Key, metric.Value);
            text.Append("\nIteration means ").Append(Cell(row.SceneId + "/" + row.Layout)).Append(": ")
                .AppendJoin(", ", row.IterationMeans.Select(Metric)).Append("\n\n");
        }
        foreach (var pair in summary.Perturbations.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append("\n").Append(pair.Value.DifferenceNs.Value is { } value && value != 0 ? "MarkerPerturbationObserved " : "Marker perturbation ")
                .Append(Cell(pair.Key)).Append(": ").Append(Metric(pair.Value.DifferenceNs))
                .Append(". Relative difference ").Append(Metric(pair.Value.Percent))
                .Append(". Threshold ").Append(Metric(pair.Value.ThresholdNs))
                .Append(". AboveThreshold ").Append(pair.Value.AboveThreshold?.ToString() ?? "unavailable").Append('\n');
        text.Append("\nMemory observations ").Append(report.Memory.Observations.Length).Append(". Last available snapshots are distinct from peaks.\n");
        foreach (var pair in report.Memory.Peaks.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var observation = report.Memory.Observations[pair.Value.Observation];
            text.Append("\nPeak ").Append(Cell(pair.Key)).Append(": ").Append(Metric(pair.Value.Metric)).Append(" at observation ")
                .Append(pair.Value.Observation).Append(" (").Append(Cell(observation.Operation + "/" + observation.Stage))
                .Append(", first frame ").Append(observation.FirstFrame?.ToString(CultureInfo.InvariantCulture) ?? "startup")
                .Append("). Source ").Append(Cell(pair.Value.Metric.Source)).Append('\n');
        }
        for (int i = 0; i < report.Memory.Observations.Length; i++)
            if (report.Memory.Observations[i].Reason is { } failure) text.Append("\nMemory observation ").Append(i).Append(": ").Append(Cell(failure)).Append('\n');
        foreach (var pair in report.Memory.Process.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append("\nLast process ").Append(Cell(pair.Key)).Append(": ").Append(Metric(pair.Value)).Append(". Source ").Append(Cell(pair.Value.Source)).Append('\n');
        foreach (var memory in report.Memory.Native)
            foreach (var (name, metric) in new[] { ("logical buffers", memory.LogicalBufferBytes), ("logical textures", memory.LogicalTextureBytes),
                ("committed", memory.CommittedBytes), ("resident", memory.ResidentBytes), ("budget", memory.BudgetBytes),
                ("staging", memory.StagingCapacityBytes), ("history", memory.HistoryPayloadBytes), ("scratch", memory.ScratchPayloadBytes),
                ("peak logical", memory.PeakLogicalPayloadBytes), ("live resources", memory.LiveResourceCount) })
                text.Append("\nNative ").Append(name).Append(": ").Append(Native(metric)).Append('\n');
        foreach (var reference in summary.References.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var r = reference.Value;
            text.Append("\nReference ").Append(Cell(reference.Key)).Append(": peak ").Append(Number(r.PeakBandwidthBytesPerSecond))
                .Append(" bytes/s, ").Append(Number(r.PeakFp32OpsPerSecond)).Append(" operations/s, measured ")
                .Append(Number(r.MeasuredBandwidthBytesPerSecond)).Append(" bytes/s, ").Append(Number(r.MatchedOpsPerSecond))
                .Append(" operations/s, ").Append(Number(r.MatchedPixelsPerSecond)).Append(" pixels/s, launch ")
                .Append(Number(r.MatchedLaunchNs)).Append(" ns, ").Append(Cell(r.InstructionClass + ", " + r.Source)).Append('\n');
        }
        return text.ToString();
    }

    internal static void Header(StringBuilder text) => text.Append("\n| Row | Metric | Available/expected | Mean | Median | p95 | p99 | Min | Max | Population SD | First64 | Last64 |\n")
        .Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");

    internal static void Distribution(StringBuilder text, string row, string name, RendererPerfDistribution value)
    {
        text.Append("| ").Append(Cell(row)).Append(" | ").Append(Cell(name)).Append(" | ").Append(value.Count).Append('/').Append(value.ExpectedCount);
        foreach (var metric in new[] { value.Mean, value.Median, value.P95, value.P99, value.Minimum, value.Maximum,
            value.StandardDeviation, value.First64, value.Last64 }) text.Append(" | ").Append(Metric(metric));
        text.Append(" |\n");
    }

    static bool Display(string name, RendererPerfDistribution value) => name is "gpu.frame" or "cpu.frame" or "gpu.unattributed" or "cpu.partitionGap"
        || name.StartsWith("gpu.pass.", StringComparison.Ordinal) || name.StartsWith("cpu.phase.", StringComparison.Ordinal)
        || name.StartsWith("gap.", StringComparison.Ordinal) && (name.EndsWith(".excess", StringComparison.Ordinal) || name.EndsWith(".ratio", StringComparison.Ordinal))
        || name.StartsWith("achieved.", StringComparison.Ordinal)
        || name.StartsWith("counter.logical.total.", StringComparison.Ordinal)
        || name.StartsWith("counter.logical.unattributed.", StringComparison.Ordinal) && value.Maximum.Value != 0;

    internal static string Comparison(RendererPerfComparisonResult result)
    {
        var text = new StringBuilder("# Renderer performance comparison\n\n");
        text.Append("Compatible ").Append(result.Compatible).Append(". Deltas are candidate minus baseline. No regression budget is assigned.\n\n");
        foreach (var difference in result.Differences)
            text.Append("- ").Append(difference.Incompatible ? "Incompatible " : "Informational ").Append(Cell(difference.Path))
                .Append(": ").Append(Cell(difference.Baseline)).Append(" -> ").Append(Cell(difference.Candidate)).Append('\n');
        foreach (string reason in result.Reasons) text.Append("- ").Append(Cell(reason)).Append('\n');
        text.Append("\n| Row | Metric | Mean delta | Median delta | p95 delta | p99 delta | Mean percent |\n")
            .Append("| --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var (kind, rows) in new[] { ("iteration", result.Rows), ("combined descriptive", result.CombinedRows), ("control", result.ControlRows) })
            foreach (var row in rows)
                foreach (var metric in row.Deltas.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    text.Append("| ").Append(Cell($"{kind}/{row.SceneId}/{row.Layout}/{row.Iteration}")).Append(" | ").Append(Cell(metric.Key));
                    foreach (var value in new[] { metric.Value.Mean, metric.Value.Median, metric.Value.P95, metric.Value.P99, metric.Value.Percent })
                        text.Append(" | ").Append(Metric(value));
                    text.Append(" |\n");
                }
        return text.ToString();
    }

    static string Native(GpuDiagnosticMetric metric) => metric.Status == GpuDiagnosticStatus.Available && metric.Value is { } value
        ? Cell((value.Type switch
        {
            GpuDiagnosticValueType.UInt64 => value.UInt64!.Value.ToString(CultureInfo.InvariantCulture),
            GpuDiagnosticValueType.Int64 => value.Int64!.Value.ToString(CultureInfo.InvariantCulture),
            GpuDiagnosticValueType.Float64 => Number(value.Float64),
            _ => value.Boolean!.Value.ToString()
        }) + " " + metric.Unit + ", " + metric.Provenance + ", " + metric.Source)
        : Cell(metric.Status + ": " + metric.Reason + ", " + metric.Source);
    static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "unavailable";
    internal static string Metric(RendererPerfMetric value) => Cell(value.Value is { } number
        ? Number(number) + " " + value.Unit : value.Status + ": " + value.Reason);
    internal static string Cell(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("|", "&#124;", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
