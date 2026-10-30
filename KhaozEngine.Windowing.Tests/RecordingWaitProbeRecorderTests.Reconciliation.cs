using System;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.RecordingWaitProbeTestData;

namespace KhaozEngine.Tests.Windowing;

public sealed partial class RecordingWaitProbeRecorderTests
{
    [Theory]
    [InlineData(2, 12, 0, 0)]
    [InlineData(3, 21, 1, 9)]
    [InlineData(3, 12, 1, 0)]
    public void Exact_integer_subtraction_retains_zero_and_legitimate_remainder(long count, long ticks, long remainderCount, long remainderTicks)
    {
        var value = RecordingWaitProbeAccounting.Reconcile(RecordingWaitProbeStatus.Valid, Callback(Request()), Boundary(), Boundary(count, ticks));
        Assert.Equal(RecordingWaitReconciliationStatus.Available, value.Status);
        Assert.Equal(remainderCount, value.Count); Assert.Equal(remainderTicks, value.Ticks);
    }

    [Theory]
    [InlineData("negative-count")]
    [InlineData("negative-ticks")]
    [InlineData("device")]
    [InlineData("frequency")]
    [InlineData("depth")]
    [InlineData("overflow")]
    public void Inconsistent_totals_and_metadata_never_clamp_or_reconcile(string corruption)
    {
        var end = Boundary(2, 12); var totals = end.Totals!.Value;
        totals = corruption switch
        {
            "negative-count" => totals with { Count = 1 },
            "negative-ticks" => totals with { Ticks = 11 },
            "device" => totals with { DeviceIdentity = Guid.NewGuid() },
            "frequency" => totals with { Frequency = 1 },
            "depth" => totals with { Depth = 3 },
            "overflow" => totals with { Count = long.MinValue },
            _ => totals,
        };
        var value = RecordingWaitProbeAccounting.Reconcile(RecordingWaitProbeStatus.Valid, Callback(Request()), Boundary(), end with { Totals = totals });
        Assert.Equal(corruption.StartsWith("negative", StringComparison.Ordinal) ? RecordingWaitReconciliationStatus.NegativeRemainder
            : corruption == "overflow" ? RecordingWaitReconciliationStatus.InvalidTotals : RecordingWaitReconciliationStatus.IdentityMismatch, value.Status);
        Assert.Null(value.Count); Assert.Null(value.Ticks);
    }

    [Theory]
    [InlineData(-1, 3, 2, true)]
    [InlineData(0, 2, 1, false)]
    [InlineData(1, 2, 0, true)]
    [InlineData(2, 2, 1, false)]
    public void Interior_gaps_preserve_roles_and_independent_endpoint_reconciliation(int gap, int boundaries, int intervals, bool windowValid)
    {
        RecordingWaitBoundary[] b = [Boundary(), Boundary(2, 12), Boundary(4, 24)];
        if (gap >= 0) b[gap] = new(RecordingWaitBoundaryStatus.Contended, null);
        FrameDiagnosticsSample[] samples = new FrameDiagnosticsSample[2];
        for (int i = 0; i < 2; i++)
        {
            var request = Request(i + 1); var c = Callback(request);
            var row = new RecordingWaitProbeRow(request, RecordingWaitProbeStatus.Valid, c, b[i], b[i + 1],
                RecordingWaitProbeAccounting.Reconcile(RecordingWaitProbeStatus.Valid, c, b[i], b[i + 1]));
            samples[i] = new(1000 + i * 5000, 6000 + i * 5000, 5, 5, Window(i), Window(i + 1), default, default, row);
        }
        var summary = FrameDiagnosticsRingWaitSummary.Create(samples, true);
        Assert.Equal(2, summary.ValidRoleRows); Assert.Equal(boundaries, summary.AvailableBoundaries);
        Assert.Equal(intervals, summary.ReconciledIntervals);
        Assert.Equal(gap == -1, summary.FullyAttributed);
        Assert.Equal(windowValid ? RecordingWaitReconciliationStatus.Available : RecordingWaitReconciliationStatus.MissingBoundary, summary.Window.Status);
        Assert.Equal(10, summary.Prefix!.Value.Ticks); Assert.Equal(14, summary.Main!.Value.Ticks);
    }

    [Fact]
    public void Counted_zero_tick_wait_is_legal_and_long_values_remain_exact()
    {
        var c = Callback(Request());
        c = c with
        {
            Prefix = c.Prefix with { Receipt = c.Prefix.Receipt!.Value with { Ticks = 0 } },
            Main = c.Main with { Receipt = c.Main.Receipt!.Value with { Ticks = 0 } }
        };
        Assert.Equal(RecordingWaitProbeStatus.Valid, RecordingWaitProbeAccounting.Validate(Request(), c, false));
        var value = RecordingWaitProbeAccounting.Reconcile(RecordingWaitProbeStatus.Valid, c,
            Boundary(9007199254740993, 9007199254740993), Boundary(9007199254740995, 9007199254740993));
        Assert.Equal(0, value.Count); Assert.Equal(0, value.Ticks);
    }
}
