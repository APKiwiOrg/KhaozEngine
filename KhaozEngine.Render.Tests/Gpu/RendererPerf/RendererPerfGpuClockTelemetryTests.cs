using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfGpuClockTelemetryTests
{
    internal const string Query = "pci.device_id,driver_version,clocks.applications.graphics,clocks.current.graphics,pstate,utilization.gpu,temperature.gpu,power.draw,clocks_throttle_reasons.active,clocks.current.memory,clocks.max.graphics";

    [Fact]
    public void Extended_host_rows_keep_the_fixed_cadence_and_deprecated_requested_clock_evidence()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Row(0) + Row(64));
        RendererPerfHostTelemetry.Verify(bytes);
        Assert.ThrowsAny<Exception>(() => RendererPerfHostTelemetry.Verify(Encoding.UTF8.GetBytes(Row(0) + Row(65))));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("clock")]
    [InlineData("power")]
    [InlineData("missing-row")]
    [InlineData("failure")]
    public void Registered_raw_observations_must_match_the_report_and_hold_the_lock(string fault)
    {
        var report = RendererPerfGpuClockAssessmentTests.WithClocks(RendererPerfAnalysisFixture.Report());
        string raw = fault switch
        {
            "clock" => "0x1EB810DE, 590.48.01, N/A, 1080, P0, 100, 60, 65, 0x0, 5001, 1590",
            "power" => "0x1EB810DE, 590.48.01, N/A, 1095, P0, 100, 60, 65, 0x4, 5001, 1590",
            _ => "0x1EB810DE, 590.48.01, N/A, 1095, P0, 100, 60, 65, 0x0, 5001, 1590"
        };
        string rows = string.Concat(Enumerable.Range(0, fault == "missing-row" ? 6 : 7)
            .Select(i => Row(i * 64, raw, fault == "failure" ? "sampler failed" : null)));
        byte[] bytes = Encoding.UTF8.GetBytes(rows);
        var expected = new RendererPerfExpectedSidecar(RendererPerfSidecarKind.NonCompiler,
            RendererPerfArtifactProducers.Schema("host-telemetry.jsonl"), "host-telemetry.jsonl", bytes.Length,
            RendererPerfRecipe.Hash(bytes), RendererPerfSidecarCounts.Empty);
        if (fault == "ok") RendererPerfArtifactProducers.Verify(expected, bytes, report);
        else Assert.ThrowsAny<Exception>(() => RendererPerfArtifactProducers.Verify(expected, bytes, report));
    }

    internal static string Row(int boundary, string? raw = null, string? failure = null) => JsonSerializer.Serialize(new
    {
        boundary,
        atTicks = boundary + 1,
        frequency = 1000,
        query = Query,
        raw = raw ?? "0x1EB810DE, 590.48.01, Requested functionality has been deprecated, 1095, P0, 100, 60, 65, 0x0000000000000002, 5001, 1590",
        failure
    }, RendererPerfManifest.JsonOptions) + "\n";
}
