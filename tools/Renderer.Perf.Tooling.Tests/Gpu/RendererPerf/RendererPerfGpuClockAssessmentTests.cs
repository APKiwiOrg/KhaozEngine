using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfGpuClockAssessmentTests
{
    [Theory]
    [InlineData("conditioning:S3/TotalOnly/0", 1080, 0)]
    [InlineData("S3/TotalOnly/0", 1110, 0)]
    [InlineData("S3/TotalOnly/0", 1200, 0)]
    [InlineData("control:11/TotalOnly/0", 300, 1)]
    [InlineData("S3/PerPass/0", 1095, 4)]
    [InlineData("S3/PerPass/0", 1095, 8)]
    [InlineData("S3/PerPass/0", 1095, 16)]
    [InlineData("S3/PerPass/0", 1095, 32)]
    [InlineData("S3/PerPass/0", 1095, 64)]
    [InlineData("S3/PerPass/0", 1095, 128)]
    [InlineData("S3/PerPass/0", 1095, 256)]
    [InlineData("S3/PerPass/0", 1095, 512)]
    public void Promotion_refuses_any_unheld_clock_or_external_limiter(string operation, int clock, int flags)
    {
        var report = RendererPerfAnalysisFixture.Report();
        int index = Array.FindIndex(report.Batches.ToArray(), b => b.Operation == operation);
        var batch = report.Batches[index];
        report = report with { Batches = report.Batches.SetItem(index, batch with { Telemetry = Telemetry(clock, flags) }) };
        var error = Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report));
        Assert.Equal("GpuClockNotLocked", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Idle_and_application_setting_flags_are_allowed_only_at_the_exact_clock(int flags)
    {
        var report = RendererPerfAnalysisFixture.Report();
        report = WithClocks(report, 1095, flags);
        byte[] bytes = RendererPerfReportJson.Serialize(report);
        var root = JsonNode.Parse(bytes)!;
        Assert.Equal(1095, root["gpuClock"]!["lockedGraphicsClockMHz"]!.GetValue<int>());
        Assert.Equal("verified", root["gpuClock"]!["state"]!.GetValue<string>());
        Assert.Equal(7, root["gpuClock"]!["samples"]!.GetValue<int>());
        root["gpuClock"]!["samples"] = 8;
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Parse(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Theory]
    [InlineData("clock.observedMHz")]
    [InlineData("clock.throttlingReasons")]
    [InlineData("clock.observedAtTicks")]
    public void Missing_telemetry_never_certifies_a_held_lock(string key)
    {
        var report = RendererPerfAnalysisFixture.Report();
        report = report with { Batches = report.Batches.SetItem(0, report.Batches[0] with { Telemetry = Telemetry(1095, 0).Remove(key) }) };
        Assert.Equal("GpuClockNotLocked", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report)).Code);
    }

    [Fact]
    public void Reusing_an_old_observation_after_the_sampler_boundary_refuses_promotion()
    {
        var report = WithClocks(RendererPerfAnalysisFixture.Report());
        var batch = report.Batches[64];
        report = report with
        {
            Batches = report.Batches.SetItem(64, batch with
            {
                Telemetry = batch.Telemetry.SetItem("clock.observedAtTicks", report.Batches[0].Telemetry["clock.observedAtTicks"])
            })
        };
        Assert.Equal("GpuClockNotLocked", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report)).Code);
    }

    [Fact]
    public void Driver_keeps_clock_refusal_independent_from_drift_and_retains_all_frames()
    {
        var rig = new RendererPerfDriverTests.DriverRig { Drift = true, ObservedClock = 900 };
        var report = rig.Run();
        Assert.Contains("GpuClockNotLocked", report.Assessment.Reasons);
        Assert.Contains(report.Assessment.Reasons, r => r.StartsWith("ClockOrWorkDrift", StringComparison.Ordinal));
        Assert.Equal(1260, rig.Frames.Count);
        var summary = RendererPerfAnalysis.Analyze(report);
        Assert.Contains("GpuClockNotLocked", summary.Reasons);
        Assert.Contains("Locked graphics clock 1095 MHz. Verification Refused", RendererPerfAnalysis.Markdown(summary), StringComparison.Ordinal);
    }

    internal static RendererPerfReport WithClocks(RendererPerfReport report, int clock = 1095, int flags = 0)
    {
        int boundary = 0;
        string? previous = null;
        return report with
        {
            Batches = [.. report.Batches.Select(b =>
        {
            if (previous != b.Operation) { boundary++; previous = b.Operation; }
            int at = boundary++ / 64 * 64 + 1;
            return b with { Telemetry = Telemetry(clock, flags).SetItem("clock.observedAtTicks",
                RendererPerfAnalysisFixture.Value(at, "ticks", "synthetic host sample")) };
        })]
        };
    }

    internal static ImmutableDictionary<string, RendererPerfMetric> Telemetry(int clock, int flags)
        => ImmutableDictionary<string, RendererPerfMetric>.Empty
            .Add("clock.observedMHz", RendererPerfAnalysisFixture.Value(clock, "MHz", "synthetic host sample"))
            .Add("clock.throttlingReasons", RendererPerfAnalysisFixture.Value(flags, "flags", "synthetic host sample"))
            .Add("clock.observedAtTicks", RendererPerfAnalysisFixture.Value(1, "ticks", "synthetic host sample"));
}
