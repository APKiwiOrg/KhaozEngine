using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfDistribution(int Count, int ExpectedCount, RendererPerfMetric Mean,
    RendererPerfMetric Median, RendererPerfMetric P95, RendererPerfMetric P99, RendererPerfMetric Minimum,
    RendererPerfMetric Maximum, RendererPerfMetric StandardDeviation, RendererPerfMetric First64,
    RendererPerfMetric Last64, ImmutableArray<RendererPerfMetric> Unavailable);
internal sealed record RendererPerfBlockSummary(RendererPerfBlock Block,
    ImmutableDictionary<string, RendererPerfDistribution> Metrics, ImmutableArray<int> GcFrames)
{
    internal ImmutableDictionary<string, ImmutableArray<RendererPerfFloorAssessment>> GapEvidence { get; init; }
        = ImmutableDictionary<string, ImmutableArray<RendererPerfFloorAssessment>>.Empty;
    internal RendererPerfControlResolution? TimingResolution { get; init; }
}
internal sealed record RendererPerfCombinedSummary(string SceneId, RendererPerfLayout Layout,
    ImmutableDictionary<string, RendererPerfDistribution> Metrics, ImmutableArray<RendererPerfMetric> IterationMeans);
internal sealed record RendererPerfSummary(RendererPerfReport Raw, bool PromotionEligible,
    ImmutableArray<string> Reasons, ImmutableArray<RendererPerfBlockSummary> Blocks,
    ImmutableArray<RendererPerfCombinedSummary> Combined, ImmutableArray<RendererPerfBlockSummary> Controls,
    ImmutableDictionary<string, RendererPerfPerturbation> Perturbations,
    ImmutableDictionary<string, RendererPerfReferenceRates> References)
{
    internal RendererPerfVerifiedArtifact? VerifiedArtifact { get; init; }
}

// Cold, offline arithmetic only. The strict codec remains the sole raw-data admission path.
internal static class RendererPerfAnalysis
{
    static readonly RendererPerfReferenceRates NoReference = new(null, null, null, null, null, null,
        "unknown", "Missing explicit matched reference evidence");

    internal static RendererPerfSummary Read(ReadOnlySpan<byte> utf8,
        ImmutableDictionary<string, RendererPerfReferenceRates>? references = null)
        => AnalyzeDecoded(RendererPerfReportJson.Parse(utf8), references);

    internal static RendererPerfSummary Analyze(RendererPerfReport report,
        ImmutableDictionary<string, RendererPerfReferenceRates>? references = null)
        => Read(RendererPerfReportJson.Serialize(report), references);

    internal static RendererPerfSummary ReadDirectory(string directory, string reportName,
        ImmutableDictionary<string, RendererPerfReferenceRates>? referenceRates = null)
        => Analyze(RendererPerfArtifactVerification.Verify(directory, reportName), referenceRates);

    internal static RendererPerfSummary Analyze(RendererPerfVerifiedArtifact artifact,
        ImmutableDictionary<string, RendererPerfReferenceRates>? references = null)
        => AnalyzeDecoded(artifact.Report, references, artifact);

    internal static RendererPerfDistribution Distribution(ImmutableArray<RendererPerfMetric> metrics, int expected)
    {
        if (expected < 0) throw new ArgumentOutOfRangeException(nameof(expected));
        foreach (var metric in metrics) RendererPerfReportJson.Metric(metric);
        int count = metrics.Count(m => m.Status == RendererPerfMetricStatus.Available);
        var unavailable = metrics.Where(m => m.Status != RendererPerfMetricStatus.Available).ToImmutableArray();
        string unit = metrics.IsEmpty ? "unknown" : metrics[0].Unit;
        string source = string.Join(" | ", metrics.Select(m => m.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var causes = new List<string>();
        if (metrics.Length != expected || expected == 0) causes.Add($"Expected {expected} samples, retained {metrics.Length}.");
        causes.AddRange(unavailable.Select(m => m.Reason!));
        if (metrics.Any(m => m.Unit != unit)) causes.Add("Mixed metric units.");
        if (metrics.Any(m => m.Source != metrics[0].Source)) causes.Add("Mixed metric provenance.");
        RendererPerfMetricStatus state = unavailable.Any(m => m.Status == RendererPerfMetricStatus.Invalid)
            ? RendererPerfMetricStatus.Invalid : count == 0 && unavailable.Length != 0
                && unavailable.All(m => m.Status == unavailable[0].Status) ? unavailable[0].Status : RendererPerfMetricStatus.ModelIncomplete;
        RendererPerfDistribution Missing(string reason, RendererPerfMetricStatus status)
        {
            var missing = Unavailable(unit, "derived distribution from " + source, reason, status);
            return new(count, expected, missing, missing, missing, missing, missing, missing, missing, missing, missing, unavailable);
        }
        if (causes.Count != 0) return Missing(string.Join(" ", causes.Distinct(StringComparer.Ordinal)), state);
        double[] ordered = metrics.Select(m => m.Value!.Value).ToArray();
        double mean = Mean(ordered);
        double variance = ordered.Sum(v => (v - mean) * (v - mean)) / ordered.Length;
        double deviation = Math.Sqrt(variance);
        if (!double.IsFinite(deviation))
        {
            double scale = ordered.Max(v => Math.Abs(v - mean));
            deviation = scale * Math.Sqrt(ordered.Sum(v => Math.Pow((v - mean) / scale, 2)) / ordered.Length);
        }
        Array.Sort(ordered);
        // Integer ranks avoid floating-point rounding at exact percentile boundaries.
        double Rank(int percent) => ordered[(int)((percent * (long)ordered.Length + 99) / 100) - 1];
        int middle = ordered.Length / 2;
        double median = ordered.Length % 2 == 0 ? ordered[middle - 1] / 2 + ordered[middle] / 2 : ordered[middle];
        if (!double.IsFinite(mean) || !double.IsFinite(deviation) || !double.IsFinite(median))
            return Missing("Derived arithmetic is nonfinite.", RendererPerfMetricStatus.Invalid);
        RendererPerfMetric Metric(double value, string statistic) => Available(value, unit, statistic + " from " + source);
        RendererPerfMetric Window(bool first) => metrics.Length < 64
            ? Unavailable(unit, "untrimmed 64-frame mean from " + source, "Fewer than 64 measured samples.")
            : Metric(Mean((first ? metrics.Take(64) : metrics.TakeLast(64)).Select(m => m.Value!.Value).ToArray()),
                first ? "first64 mean" : "last64 mean");
        return new(count, expected, Metric(mean, "arithmetic mean"), Metric(median, "median"),
            Metric(Rank(95), "nearest-rank p95"), Metric(Rank(99), "nearest-rank p99"),
            Metric(ordered[0], "minimum"), Metric(ordered[^1], "maximum"), Metric(deviation, "population standard deviation"),
            Window(true), Window(false), unavailable);
    }

    static double Mean(double[] values)
    {
        double mean = values.Average();
        return double.IsFinite(mean) ? mean : values.Sum(v => v / values.Length);
    }
    internal static RendererPerfMetric Available(double value, string unit, string source)
        => double.IsFinite(value) ? new(value, unit, source, RendererPerfMetricStatus.Available, null)
            : Unavailable(unit, source, "Derived arithmetic is nonfinite.", RendererPerfMetricStatus.Invalid);
    internal static RendererPerfMetric Unavailable(string unit, string source, string reason,
        RendererPerfMetricStatus status = RendererPerfMetricStatus.ModelIncomplete) => new(null, unit, source, status, reason);

    static RendererPerfSummary AnalyzeDecoded(RendererPerfReport report, ImmutableDictionary<string, RendererPerfReferenceRates>? inputs, RendererPerfVerifiedArtifact? artifact = null)
    {
        var references = inputs ?? ImmutableDictionary<string, RendererPerfReferenceRates>.Empty;
        var reasons = report.Assessment.Reasons.ToList();
        if (artifact is null) reasons.Add("Unverified artifacts. Directory verification is required for promotion.");
        else if (!ReferenceEquals(artifact.Report, report)) throw new InvalidOperationException("Verified artifact association differs from the analyzed report.");
        if (!report.Assessment.ValidForPromotion) reasons.Add("The original assessment is not valid for promotion.");
        if (report.Publication.State != RendererPerfPublicationState.Completed || !report.Publication.FinalUnitPublished || !report.Publication.ReturnedByRun)
            reasons.Add(report.Publication.OriginalFailure ?? "Final completed publication is unavailable.");
        reasons.AddRange(report.Publication.Errors);
        RendererPerfDriverAssessment.Drift(report.Blocks, reasons);
        var blocks = new List<RendererPerfBlockSummary>();
        var values = new Dictionary<RendererPerfBlock, ImmutableArray<ImmutableDictionary<string, RendererPerfMetric>>>();
        foreach (var block in report.Blocks)
        {
            reasons.AddRange(block.Errors);
            foreach (var frame in block.Warmup.Concat(block.Measured)) reasons.AddRange(frame.Errors);
            var gaps = new Dictionary<string, List<RendererPerfFloorAssessment>>(StringComparer.Ordinal);
            var rows = block.Measured.Select(frame => Frame(report, block.Block, frame, references, gaps, reasons)).ToImmutableArray();
            if (!block.Measured.IsEmpty && block.Measured.Any(f => !Shape(f).SequenceEqual(Shape(block.Measured[0]))))
                reasons.Add($"GPU partition shape changed in {block.Block.SceneId}/{block.Block.Layout}/{block.Block.Iteration}.");
            values.Add(block.Block, rows);
            blocks.Add(new(block.Block, Series(rows, block.Block.MeasuredFrames),
                [.. block.Measured.Where(f => f.Metrics.Any(p => p.Key.StartsWith("gc.gen", StringComparison.Ordinal)
                    && p.Key.EndsWith(".collections", StringComparison.Ordinal) && p.Value.Status == RendererPerfMetricStatus.Available
                    && p.Value.Value > 0)).Select(f => f.FrameId)])
            { GapEvidence = gaps.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutableArray(), StringComparer.Ordinal) });
        }
        var combined = blocks.GroupBy(b => (b.Block.SceneId, b.Block.Layout)).Select(group =>
        {
            var ordered = group.OrderBy(b => b.Block.Iteration).ToArray();
            return new RendererPerfCombinedSummary(group.Key.SceneId, group.Key.Layout,
                Series([.. ordered.SelectMany(b => values[b.Block])], ordered.Sum(b => b.Block.MeasuredFrames)),
                [.. ordered.Select(b => b.Metrics["gpu.frame"].Mean)]);
        }).ToImmutableArray();
        var controls = report.Controls.Select(control =>
        {
            var block = new RendererPerfBlock("control:" + control.Control.Id, RendererPerfLayout.TotalOnly, 0,
                control.Control.WarmupFrames, control.Control.MeasuredFrames);
            var rows = control.Measured.Select(frame => Basic(frame, block, reasons)).ToImmutableArray();
            var metrics = Series(rows, control.Control.MeasuredFrames).ToBuilder();
            try
            {
                foreach (var rate in RendererPerfControls.AchievedRates(control))
                    metrics.Add("achieved." + rate.Key, Distribution([rate.Value], 1));
            }
            catch (InvalidOperationException error)
            {
                foreach (var (name, unit) in new[] { ("frame.meanNs", "ns"), ("bandwidth.bytesPerSecond", "bytes/s"),
                    ("operations.perSecond", "operations/s"), ("fill.pixelsPerSecond", "pixels/s") })
                    metrics.Add("achieved." + name, Distribution([Unavailable(unit, "checked control work / outer GPU duration", error.Message)], 1));
            }
            return new RendererPerfBlockSummary(block, metrics.ToImmutable(), []) { TimingResolution = RendererPerfControlTiming.Summarize(control) };
        }).ToImmutableArray();
        var perturbations = ImmutableDictionary.CreateBuilder<string, RendererPerfPerturbation>(StringComparer.Ordinal);
        foreach (var group in blocks.GroupBy(b => (b.Block.SceneId, b.Block.Iteration)))
        {
            var total = group.SingleOrDefault(b => b.Block.Layout == RendererPerfLayout.TotalOnly)?.Metrics["gpu.frame"].Mean;
            var detailed = group.SingleOrDefault(b => b.Block.Layout == RendererPerfLayout.PerPass)?.Metrics["gpu.frame"].Mean;
            perturbations.Add($"{group.Key.SceneId}/{group.Key.Iteration}", RendererPerfPerturbation.Evaluate(total, detailed));
        }
        var distinct = reasons.Distinct(StringComparer.Ordinal).ToImmutableArray();
        return new(report, report.Assessment.ValidForPromotion && distinct.IsEmpty, distinct, [.. blocks], combined,
            controls, perturbations.ToImmutable(), references)
        { VerifiedArtifact = artifact };
    }

    internal static IEnumerable<(string, int, string?, int?, uint, uint, bool)> Shape(RendererPerfFrameSample frame)
        => frame.Passes.Select(p => (p.Id, p.Occurrence, p.ParentId, p.ParentOccurrence, p.BeginMarker, p.EndMarker, p.DeviceOnly));

    static ImmutableDictionary<string, RendererPerfDistribution> Series(ImmutableArray<ImmutableDictionary<string, RendererPerfMetric>> rows, int expected)
    {
        var result = ImmutableDictionary.CreateBuilder<string, RendererPerfDistribution>(StringComparer.Ordinal);
        foreach (string key in rows.SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var descriptor = rows.First(r => r.ContainsKey(key))[key];
            result.Add(key, Distribution([.. rows.Select(row => row.TryGetValue(key, out var metric) ? metric
                : Unavailable(descriptor.Unit, descriptor.Source, "Metric missing from a retained frame: " + key))], expected));
        }
        // A partial artifact can contain a block with no measured frames.
        if (!result.ContainsKey("gpu.frame")) result.Add("gpu.frame", Distribution([], expected));
        return result.ToImmutable();
    }

    static ImmutableDictionary<string, RendererPerfMetric> Basic(RendererPerfFrameSample frame, RendererPerfBlock block, List<string> reasons)
    {
        var metrics = frame.Metrics.ToImmutableDictionary(p => "counter." + p.Key, p => p.Value, StringComparer.Ordinal).ToBuilder();
        metrics.Add("gpu.frame", frame.GpuFrame);
        metrics.Add("cpu.frame", Available(Nanoseconds(frame.CpuStartTicks, frame.CpuEndTicks, frame.CpuFrequency), "ns", "monotonic CPU frame interval"));
        foreach (var phase in frame.CpuPhases.GroupBy(p => p.Id, StringComparer.Ordinal))
            metrics.Add("cpu.phase." + phase.Key, Available(phase.Sum(p => Nanoseconds(p.StartTicks, p.EndTicks, frame.CpuFrequency)),
                "ns", "sum of exclusive CPU segments for " + phase.Key));
        var ordered = frame.CpuPhases.OrderBy(p => p.StartTicks).ThenBy(p => p.EndTicks).ToArray();
        bool overlap = ordered.Zip(ordered.Skip(1)).Any(p => p.First.EndTicks > p.Second.StartTicks);
        double gap = metrics["cpu.frame"].Value!.Value - frame.CpuPhases.Sum(p => Nanoseconds(p.StartTicks, p.EndTicks, frame.CpuFrequency));
        metrics.Add("cpu.partitionGap", overlap || gap < 0
            ? Unavailable("ns", "CPU partition", "Invalid CPU partition with overlapping segments or negative gap.", RendererPerfMetricStatus.Invalid)
            : Available(gap, "ns", "CPU wall minus exclusive retained segments"));
        if (metrics["cpu.partitionGap"].Status == RendererPerfMetricStatus.Invalid)
            reasons.Add($"Invalid CPU partition in {block.SceneId}/{block.Layout}/{block.Iteration}, frame {frame.FrameId}.");
        return metrics.ToImmutable();
    }

    // Unsigned difference preserves the entire Int64 tick range before floating-point conversion.
    static double Nanoseconds(long start, long end, long frequency) => unchecked((ulong)end - (ulong)start) / (double)frequency * 1e9;

    static ImmutableDictionary<string, RendererPerfMetric> Frame(RendererPerfReport report, RendererPerfBlock block,
        RendererPerfFrameSample frame, ImmutableDictionary<string, RendererPerfReferenceRates> references,
        Dictionary<string, List<RendererPerfFloorAssessment>> gaps, List<string> reasons)
    {
        var metrics = Basic(frame, block, reasons).ToBuilder();
        string context = $"{block.SceneId}/{block.Layout}/{block.Iteration}";
        if (block.Layout == RendererPerfLayout.TotalOnly) return metrics.ToImmutable();
        string? partitionError = PartitionError(frame.Passes);
        var roots = frame.Passes.Where(p => !p.DeviceOnly && p.ParentId is null).ToArray();
        metrics.Add("gpu.unattributed", Gap(frame.GpuFrame, roots, partitionError));
        var models = RendererPerfWorkModels.ForFrame(frame, context);
        foreach (var pass in frame.Passes)
        {
            string key = $"{pass.Id}.{pass.Occurrence}";
            metrics.Add("gpu.pass." + key, pass.Duration);
            metrics.Add("gpu.exclusive." + key, pass.DeviceOnly ? pass.Duration : Gap(pass.Duration,
                frame.Passes.Where(p => !p.DeviceOnly && p.ParentId == pass.Id && p.ParentOccurrence == pass.Occurrence).ToArray(), partitionError));
            var model = report.Assessment.WorkModels.FirstOrDefault(m => m.PassId == pass.Id && m.Occurrence == pass.Occurrence)
                ?? models.Single(m => m.PassId == pass.Id && m.Occurrence == pass.Occurrence);
            var rates = references.GetValueOrDefault($"{block.SceneId}/{pass.Id}/{pass.Occurrence}") ?? NoReference;
            var floor = Assess(model, rates, pass.Duration);
            // Match evidence identities, not calibration values or modeled work magnitudes.
            string provenance = JsonSerializer.Serialize(new
            {
                rates.Source,
                rates.InstructionClass,
                Launch = RendererPerfLaunchRelation.MayOverlap.ToString(),
                model.PassId,
                model.Occurrence,
                Components = model.Components.Where(p => p.Key is "readBytes" or "writeBytes" or "fp32-fma" or "pixels"
                    || p.Key.StartsWith("operations.", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new { p.Key, p.Value.Unit, p.Value.Source, p.Value.Provenance }),
                model.SerialDependencies
            });
            RendererPerfMetric Sourced(RendererPerfMetric metric) => metric with { Source = metric.Source + ", evidence " + provenance };
            floor = floor with
            {
                PeakLowerBoundNs = Sourced(floor.PeakLowerBoundNs),
                CalibratedReferenceNs = Sourced(floor.CalibratedReferenceNs),
                AbsoluteExcessNs = Sourced(floor.AbsoluteExcessNs),
                Ratio = Sourced(floor.Ratio)
            };
            if (!gaps.TryGetValue(key, out var evidence)) gaps.Add(key, evidence = []);
            evidence.Add(floor);
            metrics.Add("gap." + key + ".peakLowerBound", floor.PeakLowerBoundNs);
            metrics.Add("gap." + key + ".calibratedReference", floor.CalibratedReferenceNs);
            metrics.Add("gap." + key + ".excess", floor.AbsoluteExcessNs);
            metrics.Add("gap." + key + ".ratio", floor.Ratio);
        }
        if (metrics.Any(p => (p.Key == "gpu.unattributed" || p.Key.StartsWith("gpu.exclusive.", StringComparison.Ordinal))
            && p.Value.Status == RendererPerfMetricStatus.Invalid)) reasons.Add("Invalid GPU partition in " + context + ".");
        return metrics.ToImmutable();
    }

    static RendererPerfFloorAssessment Assess(RendererPassWorkModel model, RendererPerfReferenceRates rates, RendererPerfMetric duration)
    {
        if (duration.Value is not { } observed)
            return new(duration.Status, duration, duration, duration, duration with { Unit = "ratio" }, "unavailable", [duration.Reason!]);
        try { return RendererPerfFloorModel.Evaluate(model, rates, observed); }
        catch (ArgumentException error)
        {
            var invalid = Unavailable("ns", "invalid reference/model from " + rates.Source, error.Message, RendererPerfMetricStatus.Invalid);
            return new(RendererPerfMetricStatus.Invalid, invalid, invalid, invalid, invalid with { Unit = "ratio" }, "unknown", [error.Message]);
        }
    }

    static RendererPerfMetric Gap(RendererPerfMetric parent, RendererPerfPassSample[] children, string? error)
    {
        const string source = "GPU partition, parent minus immediate executed children";
        if (error is not null) return Unavailable("ns", source, error, RendererPerfMetricStatus.Invalid);
        if (parent.Status != RendererPerfMetricStatus.Available) return parent;
        var missing = children.FirstOrDefault(p => p.Duration.Status is not (RendererPerfMetricStatus.Available or RendererPerfMetricStatus.NotExecuted));
        if (missing is not null) return Unavailable("ns", source, missing.Duration.Reason!, missing.Duration.Status);
        double value = parent.Value!.Value - children.Where(p => p.Duration.Status == RendererPerfMetricStatus.Available).Sum(p => p.Duration.Value!.Value);
        return value < 0 ? Unavailable("ns", source, "Invalid GPU partition has a negative gap.", RendererPerfMetricStatus.Invalid)
            : Available(value, "ns", source);
    }

    static string? PartitionError(ImmutableArray<RendererPerfPassSample> passes)
    {
        foreach (var pass in passes.Where(p => !p.DeviceOnly && p.ParentId is not null))
        {
            var parent = passes.FirstOrDefault(p => p.Id == pass.ParentId && p.Occurrence == pass.ParentOccurrence);
            if (parent is null || parent.DeviceOnly || parent.BeginMarker >= pass.BeginMarker || parent.EndMarker <= pass.EndMarker)
                return "Invalid GPU partition has a missing parent or crossing marker range.";
        }
        foreach (var siblings in passes.Where(p => !p.DeviceOnly).GroupBy(p => (p.ParentId, p.ParentOccurrence)))
        {
            var ordered = siblings.OrderBy(p => p.BeginMarker).ToArray();
            if (ordered.Zip(ordered.Skip(1)).Any(p => p.First.EndMarker >= p.Second.BeginMarker))
                return "Invalid GPU partition has overlapping sibling marker ranges.";
        }
        return null;
    }

    internal static string Markdown(RendererPerfSummary summary) => RendererPerfMarkdown.Summary(summary);
}
