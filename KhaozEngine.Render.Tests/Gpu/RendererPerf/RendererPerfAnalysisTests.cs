using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfAnalysisFixture;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfAnalysisTests
{
    [Fact]
    public void Exact_nearest_rank_and_population_deviation_are_reproducible()
    {
        var distribution = RendererPerfAnalysis.Distribution([Value(1), Value(2), Value(3), Value(10)], 4);
        Assert.Equal(4, distribution.Count);
        Assert.Equal(4, distribution.Mean.Value);
        Assert.Equal(2.5, distribution.Median.Value);
        Assert.Equal(10, distribution.P95.Value);
        Assert.Equal(10, distribution.P99.Value);
        Assert.Equal(1, distribution.Minimum.Value);
        Assert.Equal(10, distribution.Maximum.Value);
        Assert.Equal(Math.Sqrt(12.5), distribution.StandardDeviation.Value);
        Assert.Equal(19, RendererPerfAnalysis.Distribution([.. Enumerable.Range(1, 20).Select(i => Value(i))], 20).P95.Value);
        Assert.Equal(20, RendererPerfAnalysis.Distribution([.. Enumerable.Range(1, 20).Select(i => Value(i))], 20).P99.Value);
    }

    [Fact]
    public void Missing_or_mixed_metrics_preserve_provenance_without_subset_means()
    {
        var unsupported = new RendererPerfMetric(null, "ns", "synthetic outer pair", RendererPerfMetricStatus.Unsupported, "no timestamp counter");
        var distribution = RendererPerfAnalysis.Distribution([Value(4), unsupported], 2);
        Assert.Equal(1, distribution.Count);
        Assert.Null(distribution.Mean.Value);
        Assert.Contains(unsupported, distribution.Unavailable);
        Assert.Null(RendererPerfAnalysis.Distribution([Value(1), Value(1, "ms")], 2).Mean.Value);
        Assert.Null(RendererPerfAnalysis.Distribution([Value(4)], 2).P95.Value);
    }

    [Theory]
    [InlineData("missing-parent")]
    [InlineData("overlap")]
    [InlineData("negative-gap")]
    public void Invalid_partitions_remain_diagnostic_and_refuse_promotion(string fault)
    {
        var report = Map(Report(), (b, f) => b.Layout == RendererPerfLayout.TotalOnly ? f : f with
        {
            Passes = fault == "missing-parent"
                ? [new("child", 0, "absent", 0, 2, 3, 30, Value(30), false)]
                : [new("first", 0, null, null, 1, 4, 80, Value(80), false),
                    new("second", 0, null, null, fault == "overlap" ? 3u : 5u, 6, fault == "overlap" ? 10u : 40u, Value(fault == "overlap" ? 10 : 40), false)]
        });
        var summary = RendererPerfAnalysis.Analyze(report);
        Assert.False(summary.PromotionEligible);
        Assert.Contains(summary.Reasons, r => r.Contains("partition", StringComparison.OrdinalIgnoreCase));
        Assert.Null(summary.Blocks[1].Metrics["gpu.unattributed"].Mean.Value);
    }

    [Fact]
    public void Cpu_intervals_counters_and_gc_frames_use_retained_evidence()
    {
        var report = Map(Report(), (_, f) => f with
        {
            CpuPhases = [new("prepare", null, 0, 20), new("child", "prepare", 20, 30), new("prepare", null, 30, 60)],
            Metrics = f.Metrics.Add("gc.gen0.collections", Value(f.SequenceIndex == 7 ? 1 : 0, "count"))
                .Add("logical.unattributed.Draws", Value(2, "count"))
        });
        var row = RendererPerfAnalysis.Analyze(report).Blocks[0];
        Assert.Equal(100000000, row.Metrics["cpu.frame"].Mean.Value);
        Assert.Equal(50000000, row.Metrics["cpu.phase.prepare"].Mean.Value);
        Assert.Equal(40000000, row.Metrics["cpu.partitionGap"].Mean.Value);
        Assert.Equal(new[] { report.Blocks[0].Measured[7].FrameId }, row.GcFrames);
        Assert.Equal(2, row.Metrics["counter.logical.unattributed.Draws"].Mean.Value);
    }

    [Fact]
    public void Partial_publication_preserves_failure_and_cannot_be_promoted()
    {
        var report = Failed(Report());
        var summary = RendererPerfAnalysis.Analyze(report);
        Assert.False(summary.PromotionEligible);
        Assert.Contains("partial write accounting gap #1547", summary.Reasons);
        Assert.Equal(report.Blocks.Length, summary.Raw.Blocks.Length);
        Assert.Contains("Failed", RendererPerfAnalysis.Markdown(summary));
    }

    [Fact]
    public void Gap_values_retain_explicit_reference_inputs_and_invalid_model_status()
    {
        var report = Map(Report(), (b, f) => b.Layout == RendererPerfLayout.TotalOnly ? f : Duration(f, 300) with
        { Passes = [new("copy", 0, null, null, 1, 2, 200, Value(200), false)] });
        report = report with { Assessment = report.Assessment with { WorkModels = [RendererPerfFloorModelTests.Model(50, 50, 0) with { PassId = "copy" }] } };
        var rates = ImmutableDictionary<string, RendererPerfReferenceRates>.Empty.Add("S3/copy/0",
            new(1e9, null, 1e9, null, null, 40, "fp32-fma", "synthetic matched transfer evidence"));
        var row = RendererPerfAnalysis.Analyze(report, rates).Blocks[1];
        Assert.Equal(100, row.Metrics["gap.copy.0.peakLowerBound"].Mean.Value);
        Assert.Equal(100, row.Metrics["gap.copy.0.calibratedReference"].Mean.Value);
        Assert.Equal(100, row.Metrics["gap.copy.0.excess"].Mean.Value);
        Assert.Equal(2, row.Metrics["gap.copy.0.ratio"].Mean.Value);
        Assert.Null(RendererPerfAnalysis.Analyze(report).Blocks[1].Metrics["gap.copy.0.excess"].Mean.Value);
        var invalid = Map(report, (b, f) => b.Layout == RendererPerfLayout.TotalOnly ? f : f with
        { Passes = [f.Passes[0] with { ElapsedTicks = 99, Duration = Value(99) }] });
        var metric = RendererPerfAnalysis.Analyze(invalid, rates).Blocks[1].Metrics["gap.copy.0.excess"].Mean;
        Assert.Null(metric.Value);
        Assert.Equal(RendererPerfMetricStatus.Invalid, metric.Status);
        Assert.Contains("below the claimed lower bound", metric.Reason);
    }

    [Fact]
    public void Marker_perturbation_is_separate_from_total_only_and_does_not_set_a_budget()
    {
        var report = Map(Report(), (b, f) => Duration(f, b.Layout == RendererPerfLayout.TotalOnly ? 100 : 130));
        var summary = RendererPerfAnalysis.Analyze(report);
        Assert.Equal(30, summary.Perturbations["S3/0"].Value);
        Assert.Equal(100, summary.Blocks[0].Metrics["gpu.frame"].Mean.Value);
        Assert.Contains("MarkerPerturbationObserved", RendererPerfAnalysis.Markdown(summary));
        Assert.True(summary.PromotionEligible);
    }

    [Fact]
    public void Invalid_reference_inputs_remain_invalid_gap_evidence()
    {
        var report = Map(Report(), (b, f) => b.Layout == RendererPerfLayout.TotalOnly ? f : f with
        { Passes = [new("copy", 0, null, null, 1, 2, 80, Value(80), false)] });
        var rates = ImmutableDictionary<string, RendererPerfReferenceRates>.Empty.Add("S3/copy/0",
            new(1e9, null, -1, null, null, 40, "fp32-fma", "invalid synthetic calibration"));
        var summary = RendererPerfAnalysis.Analyze(report, rates);
        var metric = summary.Blocks[1].Metrics["gap.copy.0.excess"].Mean;
        Assert.Equal(RendererPerfMetricStatus.Invalid, metric.Status);
        Assert.Null(metric.Value);
        Assert.Contains("invalid synthetic calibration", metric.Source);
        Assert.NotEmpty(summary.Blocks[1].GapEvidence["copy.0"][0].MissingEvidence);
    }

    [Fact]
    public void Memory_peaks_keep_first_observation_source_and_failed_reads()
    {
        var report = Report();
        var evidence = new RendererPerfMemoryEvidence();
        foreach (var entry in new[] { ("S3/TotalOnly/0", 900d), ("S3/PerPass/0", 900d), ("control:1/TotalOnly/0", 100d) })
            evidence.Observe(entry.Item1, "startup", null, 0, 1000,
                new([], ImmutableDictionary<string, RendererPerfMetric>.Empty.Add("managed.live.bytes", Value(entry.Item2, "bytes", "managed heap snapshot"))));
        evidence.Observe("control:4/TotalOnly/0", "startup", null, 0, 1000, null, "read failed");
        var summary = RendererPerfAnalysis.Analyze(report with { Memory = evidence.Report() });
        Assert.Equal(0, summary.Raw.Memory.Peaks["managed.live.bytes"].Observation);
        string markdown = RendererPerfAnalysis.Markdown(summary);
        Assert.Contains("900 bytes", markdown);
        Assert.Contains("observation 0", markdown);
        Assert.Contains("managed heap snapshot", markdown);
        Assert.Contains("read failed", markdown);
        Assert.Equal(100, summary.Raw.Memory.Process["managed.live.bytes"].Value);
    }

    [Fact]
    public void Strict_decoder_handles_signed_zero_columns_and_publication_whitespace()
    {
        var report = Map(Report(), (_, f) => f with { Metrics = f.Metrics.Add("signed", Value(-0d)) });
        string json = Encoding.UTF8.GetString(RendererPerfReportJson.Serialize(report));
        var node = JsonNode.Parse(json)!;
        double elapsed = node["publication"]!["elapsedMilliseconds"]!["value"]!.GetValue<double>();
        string token = elapsed.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        json = json.Replace("\"value\":" + token + "}", "\"value\":" + token + "    }", StringComparison.Ordinal);
        var summary = RendererPerfAnalysis.Read(Encoding.UTF8.GetBytes(json));
        Assert.True(double.IsNegative(summary.Raw.Blocks[0].Measured[0].Metrics["signed"].Value!.Value));
        Assert.Equal(100, summary.Blocks[0].Metrics["gpu.frame"].Mean.Value);
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfAnalysis.Read(Encoding.UTF8.GetBytes(json.Replace("metric-vectors/2", "metric-vectors/1", StringComparison.Ordinal))));
    }
}
