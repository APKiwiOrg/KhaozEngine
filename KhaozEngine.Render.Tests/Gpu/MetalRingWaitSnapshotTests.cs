using System;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalRingWaitSnapshotTests
{
    [Fact]
    public void Snapshot_copies_the_pair_without_polling_the_gpu()
    {
        using var h = new MetalRingHarness();
        h.Event.OnRead = () => throw new InvalidOperationException("GPU poll forbidden");
        h.Event.OnWait = () => throw new InvalidOperationException("GPU wait forbidden");
        Assert.Equal(GpuRingWaitSnapshotStatus.Available, h.Rings.TryReadRingWaitTotals(out var totals));
        Assert.Equal(0, totals.Count); Assert.Equal(0, totals.Ticks);
        Assert.Equal(h.FramesInFlight, totals.Depth); Assert.Equal(h.Rings.RingWaitDeviceIdentity, totals.DeviceIdentity);
    }

    [Fact]
    public void A_held_publisher_returns_contended_without_entering_or_retrying()
    {
        using var h = new MetalRingHarness();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? failure = null;
        var publisher = new Thread(() =>
        {
            try { lock (h.SubmitLock) { held.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } }
            catch (Exception e) { failure = e; }
        });
        publisher.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(GpuRingWaitSnapshotStatus.Contended, h.Rings.TryReadRingWaitTotals(out var totals));
            Assert.Equal(default, totals);
        }
        finally { release.Set(); Assert.True(publisher.Join(TimeSpan.FromSeconds(10))); }
        Assert.Null(failure);
    }
}
