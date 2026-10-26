using System;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalRingWaitOverlapTests
{
    [Fact]
    public void Two_held_native_lists_keep_their_ordinal_and_exact_own_contribution()
    {
        using var shared = new HeldEvent();
        using var h = new MetalRingHarness(2, shared);
        MetalRingWaitReceiptTests.PublishOwners(h, 2);
        using var first = h.NewList(new object());
        using var second = h.NewList(new object());
        GpuRecordingWaitReceipt a = default, b = default;
        Exception? aError = null, bError = null;
        var one = new Thread(() => { try { first.Begin(); Assert.True(first.TryGetRecordingWait(out a)); } catch (Exception e) { aError = e; } });
        var two = new Thread(() => { try { second.Begin(); Assert.True(second.TryGetRecordingWait(out b)); } catch (Exception e) { bError = e; } });
        bool secondStarted = false;
        one.Start();
        try
        {
            Assert.True(shared.Entered[1].Wait(TimeSpan.FromSeconds(10)));
            two.Start(); secondStarted = true;
            Assert.True(shared.Entered[0].Wait(TimeSpan.FromSeconds(10)));
            shared.Release[0].Set();
            Assert.True(two.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(bError);
            Assert.Equal(GpuRingWaitSnapshotStatus.Available, h.Rings.TryReadRingWaitTotals(out var middle));
            Assert.Equal(1, middle.Count);
            shared.Release[1].Set();
            Assert.True(one.Join(TimeSpan.FromSeconds(10))); Assert.Null(aError);
            Assert.Equal(1UL, a.RecordingOrdinal); Assert.Equal(2UL, b.RecordingOrdinal);
            Assert.Equal(1, a.Segment); Assert.Equal(0, b.Segment);
            Assert.Equal(1, a.WaitCallCount); Assert.Equal(1, b.WaitCallCount);
            Assert.Equal(GpuRingWaitSnapshotStatus.Available, h.Rings.TryReadRingWaitTotals(out var total));
            Assert.Equal(2, total.Count); Assert.Equal(a.Count + b.Count, total.Count);
            Assert.Equal(a.Ticks + b.Ticks, total.Ticks);
            Assert.Equal(total.DeviceIdentity, a.DeviceIdentity); Assert.Equal(a.DeviceIdentity, b.DeviceIdentity);
        }
        finally
        {
            foreach (var gate in shared.Release) gate.Set();
            Assert.True(one.Join(TimeSpan.FromSeconds(10)));
            if (secondStarted) Assert.True(two.Join(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public void Held_native_buffer_acquire_is_outside_ring_accounting()
    {
        using var h = new MetalRingHarness();
        using var source = new HeldBuffer();
        using var list = h.NewList(new object(), source);
        GpuRecordingWaitReceipt receipt = default;
        Exception? failure = null;
        var worker = new Thread(() => { try { list.Begin(); Assert.True(list.TryGetRecordingWait(out receipt)); } catch (Exception e) { failure = e; } });
        worker.Start();
        try
        {
            Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0UL, h.Rings.RecordingIndex); Assert.Equal(0, h.Backpressure.Totals.Count);
            source.AllowAcquire.Set(); Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(failure); Assert.Equal(0, receipt.Count); Assert.Equal(0, receipt.Ticks);
        }
        finally { source.AllowAcquire.Set(); Assert.True(worker.Join(TimeSpan.FromSeconds(10))); }
    }

    // Each target is independently held. Completion is per-value so releasing the later publication
    // cannot accidentally satisfy the first wait. Shared state uses a lock, never the old unsynchronized fake.
    sealed class HeldEvent : IMetalSharedEvent
    {
        readonly object _gate = new();
        readonly bool[] _completed = new bool[2];
        internal ManualResetEventSlim[] Entered { get; } = [new(), new()];
        internal ManualResetEventSlim[] Release { get; } = [new(), new()];
        public ulong Read() { lock (_gate) return _completed[1] ? 2UL : _completed[0] ? 1UL : 0UL; }
        public bool WaitUntil(ulong value, ulong timeoutMs)
        {
            int i = checked((int)value - 1);
            Entered[i].Set();
            if (!Release[i].Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Held event safety expired.");
            lock (_gate) _completed[i] = true;
            return true;
        }
        public void EncodeSignal(IntPtr commandBuffer, ulong value) { }
        public void Dispose() { foreach (var x in Entered) x.Dispose(); foreach (var x in Release) x.Dispose(); }
    }

    sealed class HeldBuffer : IMetalCommandBufferSource, IDisposable
    {
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim AllowAcquire { get; } = new();
        public IntPtr Acquire()
        {
            Entered.Set();
            if (!AllowAcquire.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Held buffer safety expired.");
            return new IntPtr(123);
        }
        public void Release(IntPtr commandBuffer) { }
        public void Dispose() { Entered.Dispose(); AllowAcquire.Dispose(); }
    }
}
