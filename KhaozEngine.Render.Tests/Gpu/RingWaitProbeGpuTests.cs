using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class RingWaitProbeGpuTests(PreparePhaseSplitFixture fixture, ITestOutputHelper log)
    : IClassFixture<PreparePhaseSplitFixture>
{
    [GpuTheory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, true, false)]
    public void Observation_preserves_prepared_output_and_same_frame_capture_bytes(bool split, bool transparent, bool temporal, bool hdr)
    {
        byte[][] off = fixture.Render(split, transparent, temporal, true, hdr, probeMode: ShadowStageProbeMode.Counts);
        var order = fixture.ProbeDrawOrder;
        byte[][] on = fixture.Render(split, transparent, temporal, true, hdr, probeMode: ShadowStageProbeMode.Counts, ringWaitProbe: true);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(off[i], on[i]); Assert.Equal(order[i], fixture.ProbeDrawOrder[i]);
            var callback = fixture.WaitCallbacks[i];
            Assert.Equal(i + 1, callback.Request.Sequence); Assert.True(callback.Request.Enabled);
            Assert.Equal(ShadowStageProbeMode.Counts, callback.Request.Mode);
            Assert.Equal(1, callback.AcknowledgementCount);
            Assert.Equal(RecordingWaitProbeStatus.Valid, RecordingWaitProbeAccounting.Validate(callback.Request, callback, false));
            Assert.Equal(split ? RecordingWaitObservationStatus.Available : RecordingWaitObservationStatus.NotApplicable, callback.Prefix.Status);
            Assert.True(callback.Main.Receipt.HasValue);
            var reconciliation = RecordingWaitProbeAccounting.Reconcile(RecordingWaitProbeStatus.Valid, callback,
                fixture.WaitBoundaries[i], fixture.WaitBoundaries[i + 1]);
            Assert.Equal(RecordingWaitReconciliationStatus.Available, reconciliation.Status);
            Assert.True(reconciliation.Count >= 0); Assert.True(reconciliation.Ticks >= 0);
            log.WriteLine($"callback={i + 1}, prefix={callback.Prefix.Receipt?.Count}, main={callback.Main.Receipt.Value.Count}, remainder={reconciliation.Count}, ticks={reconciliation.Ticks}");
        }
        log.WriteLine(fixture.ValidationProof);
        log.WriteLine("Eight pipelined output buffers and same-frame capture RGBA bytes agree with observation off/on. Zero native stalls are permitted.");
    }

    [GpuFact]
    public void A_disposed_real_device_exposes_no_coherent_snapshot()
    {
        using var context = GpuDeviceContext.CreateHeadless();
        var native = Assert.IsType<MetalGpuDevice>(context.GpuDevice);
        Assert.Equal(GpuRingWaitSnapshotStatus.Available, native.TryReadRingWaitTotals(out _));
        context.Dispose();
        Assert.Equal(GpuRingWaitSnapshotStatus.Unavailable, native.TryReadRingWaitTotals(out var totals));
        Assert.Equal(default, totals);
    }
}
