using System;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalRingWaitReceiptTests
{
    [Fact]
    public void Observed_zero_is_available_only_during_a_full_successful_recording()
    {
        using var h = new MetalRingHarness();
        using var list = h.NewList(new object());
        Assert.True(list.TryGetRecordingAttempt(out ulong before));
        Assert.False(list.TryGetRecordingWait(out _));
        list.Begin();
        Assert.True(list.TryGetRecordingWait(out var receipt));
        Assert.Equal(unchecked(before + 1), receipt.Attempt);
        Assert.NotEqual(Guid.Empty, receipt.DeviceIdentity);
        Assert.Equal(list.RecordingWaitListIdentity, receipt.ListIdentity);
        Assert.Equal(1UL, receipt.RecordingOrdinal);
        Assert.Equal(0, receipt.Count); Assert.Equal(0, receipt.Ticks); Assert.Equal(0, receipt.WaitCallCount);
        list.End();
        Assert.False(list.TryGetRecordingWait(out _));
        list.Dispose();
        Assert.False(list.TryGetRecordingWait(out _)); Assert.False(list.TryGetRecordingAttempt(out _));
    }

    [Fact]
    public void Multiple_revalidations_publish_one_count_with_exact_accumulator_ticks()
    {
        using var h = new MetalRingHarness(1);
        PublishOwners(h, 1);
        int calls = 0;
        h.Event.OnWait = () =>
        {
            if (++calls != 1) return;
            lock (h.SubmitLock)
            {
                ulong next = h.Timeline.EncodeSignalForSubmit(IntPtr.Zero);
                h.Timeline.RegisterSubmitted(next);
                h.Rings.RecordSegmentOwner(0, next);
            }
        };
        h.Rings.BeginRecordingWithWaitReceipt(out var receipt);
        Assert.Equal(2, receipt.WaitCallCount); Assert.Equal(1, receipt.Count);
        Assert.Equal(h.Backpressure.Totals.Count, receipt.Count);
        Assert.Equal(h.Backpressure.Totals.Ticks, receipt.Ticks);
        Assert.Equal(System.Diagnostics.Stopwatch.Frequency, receipt.Frequency);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    public void Device_writes_remain_outside_recording_receipts(int depth, long remainder)
    {
        using var h = new MetalRingHarness(depth);
        var ring = h.NewRing(4, out _);
        h.Rings.BeginRecordingWithWaitReceipt(out var receipt);
        PublishOwners(h, depth);
        h.Rings.UpdateBuffer(ring, 0, new byte[] { 1, 2, 3, 4 });
        Assert.Equal(GpuRingWaitSnapshotStatus.Available, h.Rings.TryReadRingWaitTotals(out var totals));
        Assert.Equal(remainder, totals.Count - receipt.Count);
        Assert.Equal(h.Backpressure.Totals.Ticks, totals.Ticks);
    }

    [Theory]
    [InlineData("nil")]
    [InlineData("wait")]
    [InlineData("post-ring")]
    public void Failed_begin_cannot_expose_an_old_or_provisional_receipt(string failure)
    {
        using var h = new MetalRingHarness(1);
        var buffers = new FakeMetalCommandBufferSource();
        using var list = h.NewList(new object(), buffers);
        list.Begin(); Assert.True(list.TryGetRecordingWait(out _)); list.End();
        var original = new InvalidOperationException(failure);
        if (failure == "nil") buffers.NextAcquireFails = true;
        else
        {
            PublishOwners(h, 1);
            if (failure == "wait") h.Event.OnWait = () => throw original;
            else h.Event.OnRead = () => { if (h.Backpressure.Totals.Count == 1) throw original; };
        }
        var error = Assert.Throws<InvalidOperationException>(list.Begin);
        if (failure != "nil") Assert.Same(original, error);
        Assert.False(list.TryGetRecordingWait(out _)); Assert.False(list.IsRecording);
        Assert.Equal(failure == "post-ring" ? 1 : 0, h.Backpressure.Totals.Count);
        list.Dispose(); Assert.Equal(0, buffers.Outstanding);
    }

    [Fact]
    public void Attempt_and_device_ordinal_wrap_to_observed_zero()
    {
        using var h = new MetalRingHarness();
        using var list = h.NewList(new object());
        typeof(MetalCommandList).GetField("_timestampRecordingId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(list, ulong.MaxValue);
        typeof(MetalRingAllocator).GetField("_recordingIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(h.Rings, ulong.MaxValue);
        list.Begin();
        Assert.True(list.TryGetRecordingAttempt(out var attempt)); Assert.Equal(0UL, attempt);
        Assert.True(list.TryGetRecordingWait(out var receipt)); Assert.Equal(0UL, receipt.Attempt);
        Assert.Equal(0UL, receipt.RecordingOrdinal);
    }

    internal static void PublishOwners(MetalRingHarness h, int depth)
    {
        lock (h.SubmitLock)
            for (int i = 0; i < depth; i++)
            {
                ulong value = h.Timeline.EncodeSignalForSubmit(IntPtr.Zero);
                h.Timeline.RegisterSubmitted(value);
                h.Rings.RecordSegmentOwner(i, value);
            }
    }
}
