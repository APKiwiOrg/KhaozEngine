using System;
using System.Collections.Immutable;
using System.Linq;
using Xunit;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfAnalysisFixture;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
public sealed class RendererPerfComparisonTests
{
    static RendererPerfComparisonResult VerifiedCompare(RendererPerfSummary baseline, RendererPerfSummary candidate)
        => RendererPerfComparison.Compare(baseline, candidate);
    static RendererPerfComparisonResult VerifiedCompare(RendererPerfReport baseline, RendererPerfReport candidate)
        => RendererPerfAnalysisFixture.VerifiedCompare(baseline, candidate);

    [Theory]
    [InlineData("driver")]
    [InlineData("recipe")]
    [InlineData("layout")]
    [InlineData("pacing")]
    [InlineData("runtime")]
    [InlineData("capture")]
    [InlineData("adapter")]
    public void Rejects_driver_recipe_layout_and_pacing_mismatch(string fault)
    {
        var baseline = Report();
        var candidate = fault switch
        {
            "driver" => baseline with { Identity = baseline.Identity with { Driver = "other driver" } },
            "runtime" => baseline with { Identity = baseline.Identity with { Cpu = "other host CPU" } },
            "capture" => baseline with { Identity = baseline.Identity with { CaptureFlags = "vendor capture" } },
            "adapter" => baseline with { Identity = baseline.Identity with { DeviceId = 0x1234 }, Assessment = baseline.Assessment with { ValidForPromotion = false } },
            "recipe" => baseline with { Recipes = baseline.Recipes.SetItem(0, baseline.Recipes[0] with { SequenceSha256 = new string('f', 64) }) },
            "layout" => Map(baseline, (b, f) => b.Layout == RendererPerfLayout.TotalOnly ? f : f with
            { Passes = [new("new-layout", 0, null, null, 1, 2, 10, Value(10), false)] }),
            // Frozen manifest rejection is also a compatibility refusal, never relaxed for analysis.
            _ => baseline with { Manifest = baseline.Manifest with { BatchSize = 2 } }
        };
        var result = VerifiedCompare(baseline, candidate);
        Assert.False(result.Compatible);
        Assert.NotEmpty(result.Reasons);
        Assert.All(result.Rows, row => Assert.Null(row.Deltas["gpu.frame"].Mean.Value));
    }

    [Fact]
    public void Allows_candidate_source_hash_change()
    {
        var baseline = Report();
        var recipe = baseline.Recipes[0].Identity with { ShaderSha256 = new string('f', 64) };
        var candidate = baseline with
        {
            Identity = baseline.Identity with { WorkloadSha = new string('d', 40), HarnessSha256 = new string('e', 64), RunId = "456" },
            Manifest = baseline.Manifest with { WorkloadSha = new string('d', 40), Recipes = [recipe] },
            Recipes = [baseline.Recipes[0] with { Identity = recipe }]
        };
        var result = VerifiedCompare(baseline, candidate);
        Assert.True(result.Compatible);
        Assert.Contains(result.Differences, d => d.Path == "identity.workloadSha" && !d.Incompatible);
        Assert.Contains(result.Differences, d => d.Path.EndsWith("shaderSha256", StringComparison.Ordinal) && !d.Incompatible);
        Assert.Equal(0, result.Rows[0].Deltas["gpu.frame"].Mean.Value);
    }

    [Fact]
    public void Nested_scopes_do_not_replace_outer_total()
    {
        var report = Map(Report(), (b, f) => b.Layout == RendererPerfLayout.TotalOnly ? f : f with
        {
            Passes = [new("parent", 0, null, null, 1, 6, 80, Value(80), false),
                new("child", 0, "parent", 0, 2, 3, 30, Value(30), false),
                new("device.upload", 0, null, null, 0, 0, null,
                    new(null, "ns", "CPU device owner", RendererPerfMetricStatus.Unsupported, "CPU only"), true)]
        });
        var summary = VerifiedAnalyze(report);
        var row = summary.Blocks.Single(b => b.Block.Layout == RendererPerfLayout.PerPass);
        Assert.Equal(100, row.Metrics["gpu.frame"].Mean.Value);
        Assert.Equal(20, row.Metrics["gpu.unattributed"].Mean.Value);
        Assert.Equal(50, row.Metrics["gpu.exclusive.parent.0"].Mean.Value);
        Assert.Null(row.Metrics["gpu.pass.device.upload.0"].Mean.Value);
        Assert.True(summary.PromotionEligible);
    }

    [Fact]
    public void Keeps_correlated_iteration_means_separate()
    {
        var report = Map(Report(full: true), (b, f) => Duration(f, b.Iteration switch { 0 => 100, 1 => 102, _ => 104 }));
        var summary = VerifiedAnalyze(report);
        var combined = summary.Combined.Single(s => s.SceneId == "S0" && s.Layout == RendererPerfLayout.TotalOnly);
        Assert.Equal(new double?[] { 100, 102, 104 }, combined.IterationMeans.Select(m => m.Value));
        Assert.Equal(768, combined.Metrics["gpu.frame"].Count);
        Assert.Equal(102, combined.Metrics["gpu.frame"].Mean.Value);
        var result = VerifiedCompare(report, report);
        Assert.Equal(3, result.Rows.Count(r => r.SceneId == "S0" && r.Layout == RendererPerfLayout.TotalOnly));
    }

    [Fact]
    public void Missing_metric_cannot_win_a_comparison()
    {
        var baseline = Map(Report(), (_, f) => f with { Metrics = f.Metrics.Add("optional", Value(200, "bytes", "logical payload")) });
        var candidate = Map(Report(), (_, f) => f with
        {
            Metrics = f.Metrics.Add("optional", new(null, "bytes", "logical payload", RendererPerfMetricStatus.Unsupported, "not exposed"))
        });
        var delta = VerifiedCompare(baseline, candidate).Rows[0].Deltas["counter.optional"];
        Assert.Null(delta.Mean.Value);
        Assert.Contains("not exposed", delta.Mean.Reason);
        Assert.Null(delta.Percent.Value);
    }

    [Fact]
    public void Incomplete_publication_and_drift_refuse_all_numeric_deltas()
    {
        var baseline = Report();
        var partial = VerifiedCompare(baseline, Failed(baseline));
        Assert.False(partial.Compatible);
        Assert.Null(partial.Rows[0].Deltas["gpu.frame"].Mean.Value);
        var drifting = Map(baseline, (_, f) => Duration(f, f.SequenceIndex < 64 ? 100 : 120));
        var summary = VerifiedAnalyze(drifting);
        Assert.Contains(summary.Reasons, r => r.Contains("ClockOrWorkDrift", StringComparison.Ordinal));
        Assert.Equal(100, summary.Blocks[0].Metrics["gpu.frame"].First64.Value);
        Assert.Equal(120, summary.Blocks[0].Metrics["gpu.frame"].Last64.Value);
        Assert.False(VerifiedCompare(baseline, drifting).Compatible);
        var previouslyValid = VerifiedAnalyze(baseline);
        var changedPublication = previouslyValid with { Raw = Failed(baseline) };
        Assert.False(VerifiedCompare(previouslyValid, changedPublication).Compatible);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("recipe")]
    public void Rejected_nonfinite_typed_input_returns_original_protocol_refusal(string fault)
    {
        var baseline = Report();
        var candidate = fault == "identity"
            ? baseline with { Identity = baseline.Identity with { TimestampPeriodNs = double.NaN } }
            : baseline with
            {
                Recipes = baseline.Recipes.SetItem(0, baseline.Recipes[0] with
                { Facts = baseline.Recipes[0].Facts.Add("nonfinite", Value(double.PositiveInfinity)) })
            };
        var original = Assert.Throws<RendererPerfProtocolException>(() => VerifiedAnalyze(candidate));
        var result = VerifiedCompare(baseline, candidate);
        Assert.False(result.Compatible);
        Assert.Contains($"candidate protocol {original.Code}: {original.Message}", result.Reasons);
        Assert.Empty(result.Differences);
        Assert.Empty(result.Rows);
        Assert.Empty(result.CombinedRows);
        Assert.Empty(result.ControlRows);
    }

    [Fact]
    public void Compatible_deltas_are_candidate_minus_baseline_and_zero_percent_is_undefined()
    {
        var baseline = Map(Report(), (_, f) => f with { Metrics = f.Metrics.Add("zero", Value(0, "bytes")) });
        var candidate = Map(baseline, (_, f) => Duration(f, 110));
        var result = VerifiedCompare(baseline, candidate);
        Assert.True(result.Compatible);
        Assert.Equal(10, result.Rows[0].Deltas["gpu.frame"].Mean.Value);
        Assert.Equal(10, result.Rows[0].Deltas["gpu.frame"].Percent.Value);
        Assert.Null(result.Rows[0].Deltas["counter.zero"].Percent.Value);
        Assert.Equal(10, result.CombinedRows[0].Deltas["gpu.frame"].P99.Value);
        Assert.Contains("candidate minus baseline", RendererPerfComparison.Markdown(result));
    }

    [Fact]
    public void Missing_block_is_separate_instead_of_being_paired_with_another_layout()
    {
        var baseline = Report();
        var candidate = Failed(baseline with { Blocks = [baseline.Blocks[0]], Controls = [] });
        candidate = RendererPerfExecutionEvidenceTests.WithTrace(candidate);
        var result = VerifiedCompare(baseline, candidate);
        Assert.False(result.Compatible);
        Assert.Equal(2, result.Rows.Length);
        Assert.Contains(result.Rows.Single(r => r.Layout == RendererPerfLayout.PerPass).Reasons,
            r => r.Contains("candidate block missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_reference_cannot_produce_a_favorable_gap_delta()
    {
        var report = Map(Report(), (b, f) => b.Layout == RendererPerfLayout.TotalOnly ? f : Duration(f, 300) with
        { Passes = [new("copy", 0, null, null, 1, 2, 200, Value(200), false)] });
        report = report with { Assessment = report.Assessment with { WorkModels = [RendererPerfFloorModelTests.Model(50, 50, 0) with { PassId = "copy" }] } };
        var rates = ImmutableDictionary<string, RendererPerfReferenceRates>.Empty.Add("S3/copy/0",
            new(1e9, null, 1e9, null, null, 40, "fp32-fma", "synthetic matched transfer evidence"));
        var baseline = VerifiedAnalyze(report, rates);
        var candidate = VerifiedAnalyze(report);
        var result = VerifiedCompare(baseline, candidate);
        Assert.Null(result.Rows[1].Deltas["gap.copy.0.excess"].Mean.Value);
        Assert.Contains("Missing", result.Rows[1].Deltas["gap.copy.0.excess"].Mean.Reason);
        // A partial component can retain the same source label without validating the full model.
        var incomplete = VerifiedAnalyze(report, rates.SetItem("S3/copy/0", rates["S3/copy/0"] with
        { PeakBandwidthBytesPerSecond = null, MeasuredBandwidthBytesPerSecond = null }));
        var partial = VerifiedCompare(baseline, incomplete);
        Assert.Null(partial.Rows[1].Deltas["gap.copy.0.peakLowerBound"].Mean.Value);
        Assert.Null(partial.Rows[1].Deltas["gap.copy.0.calibratedReference"].Mean.Value);
    }

    [Theory]
    [InlineData("reference-source")]
    [InlineData("instruction-class")]
    [InlineData("model-source")]
    [InlineData("model-provenance")]
    [InlineData("dependency")]
    [InlineData("matched")]
    public void Gap_deltas_require_matched_evidence_identity_not_equal_calibration_values(string change)
    {
        var report = Map(Report(), (b, f) => b.Layout == RendererPerfLayout.TotalOnly ? f : Duration(f, 300) with
        { Passes = [new("copy", 0, null, null, 1, 2, 200, Value(200), false)] });
        var model = RendererPerfFloorModelTests.Model(50, 50, 0) with { PassId = "copy" };
        report = report with { Assessment = report.Assessment with { WorkModels = [model] } };
        var referenceA = new RendererPerfReferenceRates(1e9, null, 1e9, null, null, 40, "fp32-fma", "valid copy reference A");
        var referenceB = referenceA with
        {
            MeasuredBandwidthBytesPerSecond = .5e9,
            Source = change == "reference-source" ? "valid copy reference B" : referenceA.Source,
            InstructionClass = change == "instruction-class" ? "copy-only" : referenceA.InstructionClass
        };
        var component = model.Components["readBytes"];
        var candidateModel = change switch
        {
            "model-source" => model with { Components = model.Components.SetItem("readBytes", component with { Source = "other work evidence" }) },
            "model-provenance" => model with { Components = model.Components.SetItem("readBytes", component with { Provenance = "estimated" }) },
            "dependency" => model with { SerialDependencies = ["other dependency policy"] },
            _ => model
        };
        var references = ImmutableDictionary<string, RendererPerfReferenceRates>.Empty.Add("S3/copy/0", referenceA);
        var baseline = VerifiedAnalyze(report, references);
        var candidate = VerifiedAnalyze(report with { Assessment = report.Assessment with { WorkModels = [candidateModel] } },
            references.SetItem("S3/copy/0", referenceB));
        Assert.True(baseline.PromotionEligible);
        Assert.True(candidate.PromotionEligible);
        Assert.Equal(RendererPerfMetricStatus.Available, baseline.Blocks[1].GapEvidence["copy.0"][0].Status);
        Assert.Equal(RendererPerfMetricStatus.Available, candidate.Blocks[1].GapEvidence["copy.0"][0].Status);
        Assert.Equal(100, baseline.Blocks[1].Metrics["gap.copy.0.excess"].Mean.Value);
        Assert.Equal(0, candidate.Blocks[1].Metrics["gap.copy.0.excess"].Mean.Value);
        var result = VerifiedCompare(baseline, candidate);
        foreach (var row in result.Rows.Concat(result.CombinedRows).Where(r => r.Layout == RendererPerfLayout.PerPass))
        {
            Assert.Equal(0, row.Deltas["gpu.frame"].Mean.Value);
            if (change == "matched")
            {
                Assert.Equal(-100, row.Deltas["gap.copy.0.excess"].Mean.Value);
                Assert.Equal(-1, row.Deltas["gap.copy.0.ratio"].Mean.Value);
                Assert.Equal(100, row.Deltas["gap.copy.0.calibratedReference"].Mean.Value);
                Assert.True(row.Compatible);
            }
            else
            {
                Assert.False(row.Compatible);
                Assert.Contains(row.Reasons, r => r.Contains("provenance", StringComparison.OrdinalIgnoreCase));
                foreach (string name in new[] { "excess", "ratio", "calibratedReference", "peakLowerBound" })
                {
                    var delta = row.Deltas["gap.copy.0." + name];
                    Assert.Null(delta.Mean.Value);
                    Assert.Null(delta.Median.Value);
                    Assert.Null(delta.P95.Value);
                    Assert.Null(delta.P99.Value);
                    Assert.Null(delta.Percent.Value);
                    Assert.Contains("provenance", delta.Mean.Reason);
                }
            }
        }
        Assert.Equal(change == "matched", result.Compatible);
        if (change == "reference-source")
        {
            Assert.Contains(result.Reasons, r => r.Contains("valid copy reference A", StringComparison.Ordinal)
                && r.Contains("valid copy reference B", StringComparison.Ordinal));
        }
    }
}
