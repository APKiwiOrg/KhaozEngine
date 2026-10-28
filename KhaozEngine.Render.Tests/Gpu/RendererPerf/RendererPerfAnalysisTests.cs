using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfAnalysisFixture;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
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
        var summary = VerifiedAnalyze(report);
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
        var row = VerifiedAnalyze(report).Blocks[0];
        Assert.Equal(100000000, row.Metrics["cpu.frame"].Mean.Value);
        Assert.Equal(50000000, row.Metrics["cpu.phase.prepare"].Mean.Value);
        Assert.Equal(40000000, row.Metrics["cpu.partitionGap"].Mean.Value);
        Assert.Equal(new[] { report.Blocks[0].Measured[7].FrameId }, row.GcFrames);
        Assert.Equal(2, row.Metrics["counter.logical.unattributed.Draws"].Mean.Value);
    }

    // Phases copied from paid T4 frames that tile the CPU interval exactly. Per-segment nanosecond rounding once made
    // these read as a negative gap and refused an otherwise sound baseline.
    [Theory]
    [InlineData(1_000_000_000L, new[] { 1042, 191, 1012, 310, 291, 70, 210, 7585, 5571, 420, 91, 330, 51, 340, 50, 131, 69, 211, 60, 201, 290, 471, 30429, 4689, 371, 8316, 360, 551, 1213 })]
    [InlineData(10_000_000L, new[] { 2, 2, 8, 2, 2, 1, 2, 12, 47, 2, 1, 3, 3, 1, 1, 2, 2, 1, 4, 39, 2, 2, 481, 5, 3, 14 })]
    public void Exactly_tiled_cpu_frames_have_a_zero_partition_gap(long frequency, int[] durations)
    {
        long start = 3285012111926;
        var phases = ImmutableArray.CreateBuilder<RendererPerfCpuSpan>(durations.Length);
        long cursor = start;
        for (int i = 0; i < durations.Length; i++)
        {
            phases.Add(new("phase" + i, null, cursor, cursor + durations[i]));
            cursor += durations[i];
        }
        var report = Map(Report(), (_, f) => f with
        {
            CpuStartTicks = start,
            CpuEndTicks = cursor,
            CpuFrequency = frequency,
            CpuPhases = phases.ToImmutable()
        });
        var summary = VerifiedAnalyze(report);
        Assert.All(summary.Blocks, row => Assert.Equal(0, row.Metrics["cpu.partitionGap"].Mean.Value));
        Assert.DoesNotContain(summary.Reasons, reason => reason.Contains("Invalid CPU partition", StringComparison.Ordinal));
    }

    [Fact]
    public void Partial_publication_preserves_failure_and_cannot_be_promoted()
    {
        var report = Failed(Report());
        var summary = VerifiedAnalyze(report);
        Assert.False(summary.PromotionEligible);
        Assert.Contains("partial write accounting gap #1547", summary.Reasons);
        Assert.Equal(report.Blocks.Length, summary.Raw.Blocks.Length);
        Assert.Contains("Failed", RendererPerfAnalysis.Markdown(summary));
    }

    [Fact]
    public void Markdown_recomputes_failed_raw_publication_instead_of_publishing_stale_success()
    {
        var baseline = Report();
        var previouslyValid = VerifiedAnalyze(baseline);
        Assert.Contains("Promotion eligible True", RendererPerfAnalysis.Markdown(previouslyValid));
        var changedPublication = previouslyValid with
        {
            Raw = Failed(Map(baseline, (_, f) => Duration(f, 110))),
            Reasons = ["stale cached reason"]
        };
        string markdown = RendererPerfAnalysis.Markdown(changedPublication);
        Assert.Contains("Publication Failed. Promotion eligible False", markdown);
        Assert.Contains("partial write accounting gap #1547", markdown);
        Assert.DoesNotContain("Promotion eligible True", markdown);
        Assert.DoesNotContain("stale cached reason", markdown);
        Assert.Contains("| 110 ns |", markdown.Split('\n').Single(line => line.Contains("| S3/TotalOnly/0 | gpu.frame |", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Control_cpu_partition_validity_reaches_promotion_and_comparison(bool overlap)
    {
        var baseline = Report();
        var control = baseline.Controls[0];
        var candidate = baseline with
        {
            Controls = baseline.Controls.SetItem(0, control with
            {
                Measured = [.. control.Measured.Select(f => f with
            { CpuPhases = [new("first", null, 0, 60), new("second", null, overlap ? 50 : 60, 100)] })]
            })
        };
        var summary = VerifiedAnalyze(candidate);
        var row = summary.Controls[0];
        Assert.Equal(60000000, row.Metrics["cpu.phase.first"].Mean.Value);
        Assert.Equal(overlap ? 50000000 : 40000000, row.Metrics["cpu.phase.second"].Mean.Value);
        Assert.Equal(100, row.Metrics["gpu.frame"].Mean.Value);
        Assert.Equal(2, summary.Raw.Controls[0].Measured[0].CpuPhases.Length);
        Assert.Equal(!overlap, summary.PromotionEligible);
        var comparison = VerifiedCompare(baseline, candidate);
        Assert.Equal(!overlap, comparison.Compatible);
        if (overlap)
        {
            Assert.Equal(RendererPerfMetricStatus.Invalid, row.Metrics["cpu.partitionGap"].Mean.Status);
            Assert.Contains(summary.Reasons, reason => reason.Contains("Invalid CPU partition", StringComparison.Ordinal)
                && reason.Contains("control:" + control.Control.Id, StringComparison.Ordinal));
            Assert.All(comparison.Rows.Concat(comparison.CombinedRows).Concat(comparison.ControlRows), r =>
                Assert.All(r.Deltas.Values, delta => Assert.Null(delta.Mean.Value)));
        }
        else
        {
            Assert.Equal(0, row.Metrics["cpu.partitionGap"].Mean.Value);
            Assert.Equal(0, comparison.ControlRows[0].Deltas["gpu.frame"].Mean.Value);
        }
    }

    [Fact]
    public void Gap_values_retain_explicit_reference_inputs_and_invalid_model_status()
    {
        var report = Map(Report(), (b, f) => b.Layout == RendererPerfLayout.TotalOnly ? f : Duration(f, 300) with
        { Passes = [new("copy", 0, null, null, 1, 2, 200, Value(200), false)] });
        report = report with { Assessment = report.Assessment with { WorkModels = [RendererPerfFloorModelTests.Model(50, 50, 0) with { PassId = "copy" }] } };
        var rates = ImmutableDictionary<string, RendererPerfReferenceRates>.Empty.Add("S3/copy/0",
            new(1e9, null, 1e9, null, null, 40, "fp32-fma", "synthetic matched transfer evidence"));
        var row = VerifiedAnalyze(report, rates).Blocks[1];
        Assert.Equal(100, row.Metrics["gap.copy.0.peakLowerBound"].Mean.Value);
        Assert.Equal(100, row.Metrics["gap.copy.0.calibratedReference"].Mean.Value);
        Assert.Equal(100, row.Metrics["gap.copy.0.excess"].Mean.Value);
        Assert.Equal(2, row.Metrics["gap.copy.0.ratio"].Mean.Value);
        Assert.Null(VerifiedAnalyze(report).Blocks[1].Metrics["gap.copy.0.excess"].Mean.Value);
        var invalid = Map(report, (b, f) => b.Layout == RendererPerfLayout.TotalOnly ? f : f with
        { Passes = [f.Passes[0] with { ElapsedTicks = 99, Duration = Value(99) }] });
        var metric = VerifiedAnalyze(invalid, rates).Blocks[1].Metrics["gap.copy.0.excess"].Mean;
        Assert.Null(metric.Value);
        Assert.Equal(RendererPerfMetricStatus.Invalid, metric.Status);
        Assert.Contains("below the claimed lower bound", metric.Reason);
    }

    [Fact]
    public void Marker_perturbation_is_separate_from_total_only_and_does_not_set_a_budget()
    {
        var report = Map(Report(), (b, f) => Duration(f, b.Layout == RendererPerfLayout.TotalOnly ? 100 : 130));
        var summary = VerifiedAnalyze(report);
        Assert.Equal(30, summary.Perturbations["S3/0"].DifferenceNs.Value);
        Assert.Equal(100, summary.Blocks[0].Metrics["gpu.frame"].Mean.Value);
        Assert.Contains("MarkerPerturbationObserved", RendererPerfAnalysis.Markdown(summary));
        Assert.True(summary.PromotionEligible);
    }

    [Theory]
    [InlineData(10000000, 10010000, 10000, "0.1", 500000, false)]
    [InlineData(10000000, 10500000, 500000, "5", 500000, false)]
    [InlineData(10000000, 10500001, 500001, "5.00001", 500000, true)]
    [InlineData(10000000, 12000000, 2000000, "20", 500000, true)]
    [InlineData(1000000, 1099999, 99999, "9.9999", 100000, false)]
    [InlineData(1000000, 1100000, 100000, "10", 100000, false)]
    [InlineData(1000000, 1100001, 100001, "10.0001", 100000, true)]
    [InlineData(10000000, 9499999, -500001, "-5.00001", 500000, true)]
    public void Perturbation_retains_relative_difference_and_flags_only_above_original_threshold(
        int total, int detailed, int difference, string percent, int threshold, bool flagged)
    {
        var report = Map(Report(), (b, f) => Duration(f, b.Layout == RendererPerfLayout.TotalOnly ? total : detailed));
        var summary = VerifiedAnalyze(report);
        var perturbation = summary.Perturbations["S3/0"];
        Assert.Equal(difference, perturbation.DifferenceNs.Value);
        Assert.Equal(double.Parse(percent, System.Globalization.CultureInfo.InvariantCulture), perturbation.Percent.Value!.Value, 10);
        Assert.Equal(threshold, perturbation.ThresholdNs.Value);
        Assert.Equal(flagged, perturbation.AboveThreshold);
        string markdown = RendererPerfAnalysis.Markdown(summary);
        Assert.Contains("Relative difference ", markdown);
        Assert.Contains(" percent. Threshold ", markdown);
        Assert.Contains("Threshold " + threshold + " ns", markdown);
        Assert.Contains("AboveThreshold " + flagged, markdown);
        Assert.Contains(difference + " ns", markdown);
        Assert.Equal(total, summary.Blocks[0].Metrics["gpu.frame"].Mean.Value);
        Assert.Equal(detailed, summary.Blocks[1].Metrics["gpu.frame"].Mean.Value);
        Assert.True(summary.PromotionEligible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Perturbation_zero_or_invalid_baseline_never_invents_relative_success(bool invalid)
    {
        var report = Failed(Map(Report(), (b, f) => b.Layout != RendererPerfLayout.TotalOnly ? Duration(f, 120000) : invalid
            ? f with { GpuFrame = new(null, "ns", "synthetic outer pair", RendererPerfMetricStatus.Invalid, "disjoint baseline"), GpuElapsedTicks = null }
            : Duration(f, 0)));
        var summary = VerifiedAnalyze(report);
        var perturbation = summary.Perturbations["S3/0"];
        Assert.Null(perturbation.Percent.Value);
        Assert.Equal(invalid ? RendererPerfMetricStatus.Invalid : RendererPerfMetricStatus.ModelIncomplete, perturbation.Percent.Status);
        if (invalid)
        {
            Assert.Null(perturbation.DifferenceNs.Value);
            Assert.Null(perturbation.ThresholdNs.Value);
            Assert.Null(perturbation.AboveThreshold);
        }
        else
        {
            Assert.Equal(120000, perturbation.DifferenceNs.Value);
            Assert.Equal(100000, perturbation.ThresholdNs.Value);
            Assert.True(perturbation.AboveThreshold);
        }
        string markdown = RendererPerfAnalysis.Markdown(summary);
        Assert.Contains(invalid ? "disjoint baseline" : "baseline mean is zero", markdown);
        Assert.Contains(invalid ? "AboveThreshold unavailable" : "AboveThreshold True", markdown);
    }

    [Fact]
    public void Invalid_reference_inputs_remain_invalid_gap_evidence()
    {
        var report = Map(Report(), (b, f) => b.Layout == RendererPerfLayout.TotalOnly ? f : f with
        { Passes = [new("copy", 0, null, null, 1, 2, 80, Value(80), false)] });
        var rates = ImmutableDictionary<string, RendererPerfReferenceRates>.Empty.Add("S3/copy/0",
            new(1e9, null, -1, null, null, 40, "fp32-fma", "invalid synthetic calibration"));
        var summary = VerifiedAnalyze(report, rates);
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
        var summary = VerifiedAnalyze(report with { Memory = evidence.Report() });
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
