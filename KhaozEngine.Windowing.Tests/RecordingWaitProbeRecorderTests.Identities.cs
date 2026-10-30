using System;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.RecordingWaitProbeTestData;

namespace KhaozEngine.Tests.Windowing;

public sealed partial class RecordingWaitProbeRecorderTests
{
    [Theory]
    [InlineData("identity")]
    [InlineData("clock")]
    [InlineData("depth")]
    [InlineData("count")]
    public void Available_interior_metadata_changes_do_not_erase_roles_or_fake_a_valid_window(string corruption)
    {
        var b0 = Boundary(); var b1 = Boundary(2, 12); var b2 = Boundary(4, 24);
        b1 = b1 with
        {
            Totals = corruption switch
            {
                "identity" => b1.Totals!.Value with { DeviceIdentity = Guid.NewGuid() },
                "clock" => b1.Totals!.Value with { Frequency = 999 },
                "depth" => b1.Totals!.Value with { Depth = 3 },
                _ => b1.Totals!.Value with { Count = -1 },
            }
        };
        var c1 = Callback(Request()); var c2 = Callback(Request(2));
        FrameDiagnosticsSample[] samples = [
            new(0, 5000, 5, 5, Window(), Window(1), default, default,
                new(Request(), RecordingWaitProbeStatus.Valid, c1, b0, b1, RecordingWaitProbeAccounting.Reconcile(RecordingWaitProbeStatus.Valid, c1, b0, b1))),
            new(5000, 10000, 5, 5, Window(1), Window(2), default, default,
                new(Request(2), RecordingWaitProbeStatus.Valid, c2, b1, b2, RecordingWaitProbeAccounting.Reconcile(RecordingWaitProbeStatus.Valid, c2, b1, b2)))];
        var result = FrameDiagnosticsRingWaitSummary.Create(samples, true);
        Assert.Equal(2, result.ValidRoleRows); Assert.True(result.RoleCoverageComplete);
        Assert.Equal(corruption == "count" ? RecordingWaitReconciliationStatus.InvalidTotals : RecordingWaitReconciliationStatus.IdentityMismatch, result.Window.Status);
        Assert.False(result.FullyAttributed); Assert.Null(result.Window.Ticks);
    }

    [Fact]
    public void Aggregate_tick_overflow_preserves_role_coverage_and_the_other_role_total()
    {
        FrameDiagnosticsSample[] samples = new FrameDiagnosticsSample[2];
        var start = Boundary();
        for (int i = 0; i < 2; i++)
        {
            var request = Request(i + 1); var c = Callback(request);
            c = c with
            {
                Prefix = c.Prefix with { Receipt = c.Prefix.Receipt!.Value with { Ticks = 0 } },
                Main = c.Main with { Receipt = c.Main.Receipt!.Value with { Ticks = long.MaxValue } }
            };
            var end = Boundary(2 * (i + 1), long.MaxValue);
            samples[i] = new(i * 5000, (i + 1) * 5000, 5, 5, Window(i), Window(i + 1), default, default,
                new(request, RecordingWaitProbeStatus.Valid, c, start, end,
                    RecordingWaitProbeAccounting.Reconcile(RecordingWaitProbeStatus.Valid, c, start, end)));
            start = end;
        }
        var result = FrameDiagnosticsRingWaitSummary.Create(samples, true);
        Assert.Equal(2, result.ValidRoleRows); Assert.True(result.RoleCoverageComplete);
        Assert.Equal(RecordingWaitReconciliationStatus.InvalidTotals, result.Window.Status);
        Assert.Equal(0, result.Prefix!.Value.Ticks); Assert.Equal(2, result.Prefix.Value.Count);
        Assert.Null(result.Main); Assert.Null(result.MainMs); Assert.False(result.FullyAttributed);
    }

    [Fact]
    public void Report_carries_both_runtime_code_identities_outside_sampling()
    {
        var baseline = new FrameDiagnosticsBoundary(0, Window(), default);
        var end = new FrameDiagnosticsBoundary(10000, Window(1), default);
        FrameDiagnosticsSample[] samples = [new(0, 10000, 10, 10, Window(), Window(1), default)];
        var report = FrameDiagnosticsReport.Create(1000, baseline, end, samples, 1, "duration");
        string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions
        { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(typeof(Frame).Assembly.ManifestModule.ModuleVersionId,
            doc.RootElement.GetProperty("engineCodeIdentity").GetProperty("mvid").GetGuid());
        Assert.NotEqual(Guid.Empty, doc.RootElement.GetProperty("appCodeIdentity").GetProperty("mvid").GetGuid());
    }
}
