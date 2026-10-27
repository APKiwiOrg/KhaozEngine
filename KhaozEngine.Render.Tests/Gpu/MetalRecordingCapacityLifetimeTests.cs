using System;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalRecordingCapacityLifetimeTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public void Wrap_waits_for_the_maximum_late_owner_before_replaying_patches(int depth)
    {
        using var h = new MetalRingHarness(depth);
        var ring = h.NewRing(257, out byte[] backing);
        using var prefix = h.NewList(new object());
        using var main = h.NewList(new object());
        Assert.Equal(depth, h.Rings.FramesInFlight);
        Assert.Equal(depth, prefix.Arena.Depth);
        Assert.Equal(depth, main.Arena.Depth);
        Assert.Equal(512 * depth, backing.Length);
        prefix.Begin();
        int prefixSegment = prefix.RingSegment;
        ring.Write(prefixSegment, 0, new byte[] { 1, 2, 3, 4 });
        prefix.End();
        main.Begin();
        ring.Write(main.RingSegment, 0, new byte[] { 5, 6, 7, 8 });
        Assert.NotEqual(prefixSegment, main.RingSegment);
        Assert.Equal((ulong)(512 * main.RingSegment), ring.SegmentBaseBytes(main.RingSegment));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ring.ReadSegment(prefixSegment, 0, 4));
        main.End();
        h.Submit(main);
        ulong lateOwner = h.Submit(prefix);
        h.Rings.RecordSegmentOwner(prefixSegment, lateOwner - 1);
        Assert.Equal(lateOwner, h.Rings.SegmentOwner(prefixSegment));
        // Fill the remaining distinct slots. No recording aliases the uncompleted prefix.
        for (int i = 2; i < depth; i++)
        {
            main.Begin(); main.End(); h.Submit(main);
        }
        h.Rings.UpdateBuffer(ring, 0, new byte[] { 9, 10, 11, 12 });
        Assert.True(ring.HasPendingPatchesFor(prefixSegment));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ring.ReadSegment(prefixSegment, 0, 4));
        h.Event.OnWait = () => Assert.Equal(new byte[] { 1, 2, 3, 4 }, ring.ReadSegment(prefixSegment, 0, 4));
        prefix.Begin();
        Assert.Equal(prefixSegment, prefix.RingSegment);
        Assert.Equal(lateOwner, h.Event.LastWaitValue);
        Assert.False(ring.HasPendingPatchesFor(prefixSegment));
        Assert.Equal(new byte[] { 9, 10, 11, 12 }, ring.ReadSegment(prefixSegment, 0, 4));
        prefix.End(); h.Submit(prefix);
        h.Event.OnWait = null;
        for (int i = 0; i < depth; i++)
        {
            main.Begin();
            Assert.Equal(depth, main.Arena.Depth);
            Assert.True(main.TryGetRecordingWait(out var receipt));
            Assert.Equal(depth, receipt.Depth);
            Assert.Equal(main.RingSegment, receipt.Segment);
            main.End(); h.Submit(main);
        }
        Assert.Equal(0, ring.PendingPatchCount);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public void Failed_abandoned_and_disposed_recordings_release_their_buffers(int depth)
    {
        using var h = new MetalRingHarness(depth);
        var buffers = new FakeMetalCommandBufferSource();
        using var list = h.NewList(new object(), buffers);
        buffers.NextAcquireFails = true;
        Assert.Throws<InvalidOperationException>(list.Begin);
        Assert.False(list.TryGetRecordingWait(out _));
        list.Begin(); list.End();
        list.Begin(); // abandon the earlier sealed recording
        Assert.Equal(1, buffers.Outstanding);
        list.End();
        list.Dispose(); list.Dispose();
        Assert.Equal(0, buffers.Outstanding);
        Assert.False(list.TryGetRecordingWait(out _));
        Assert.Throws<ObjectDisposedException>(list.Begin);
    }
}
