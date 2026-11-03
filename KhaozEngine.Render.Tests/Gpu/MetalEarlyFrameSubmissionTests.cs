using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalEarlyFrameSubmissionTests
{
    static MetalSwapchainFramebuffer Drawable()
    {
        MetalSwapchainFramebuffer? fb = null;
        fb = new(GpuPixelFormat.R8G8B8A8UNorm, new(16, 16),
            () => fb!.Adopt(new(new(0x777), GpuPixelFormat.R8G8B8A8UNorm), new(16, 16)));
        return fb;
    }

    [Fact]
    public void AFinalTimestampBatchContinuesAcrossThePrefixAndStaysAfterTheTail()
    {
        using var rig = new CoveragePerf.MetalTimestampEndFixture();
        rig.List.EarlySubmission = new(new FakeMetalEarlyFrameApi(), rig.Harness.SubmitLock);
        rig.Start();
        rig.List.SetFramebuffer(Drawable());
        rig.PendingClear();
        rig.Arm();
        rig.List.End();
        Assert.True(rig.List.IsSealed);
        Assert.Equal(0, rig.Native.OpenBlits);
        Assert.Empty(rig.Harness.Event.Encoded);
        ulong final = rig.Harness.Submit(rig.List);
        using var fence = new MetalGpuFence(rig.Harness.Timeline);
        fence.Arm(final);
        rig.Harness.Event.Completed = final;
        var ticks = new ulong[2];
        Assert.Equal(GpuTimestampStatus.Ready, rig.Pool.TryResolve(fence, ticks, out double period));
        Assert.True(period > 0);
        Assert.True(ticks[1] > ticks[0]);
    }

    [Fact]
    public void DeviceLossDuringSetupLeavesTheFrameUnsplitAndDiscardable()
    {
        using var h = new MetalRingHarness(framesInFlight: 1);
        var buffers = new FakeMetalCommandBufferSource();
        var count = new MetalUncommittedBuffers(1, new RecordingLogger());
        using var list = h.NewList(new(), buffers, uncommitted: count);
        var api = new FakeMetalEarlyFrameApi { OnCommit = () => throw new InvalidOperationException("A lost device must never commit.") };
        api.OnFlush = () => api.IsAlive = false;
        list.EarlySubmission = new(api, h.SubmitLock);
        list.Begin();
        list.SetFramebuffer(Drawable());
        list.End();
        list.DiscardRecording();
        Assert.Single(buffers.Acquired);
        Assert.Equal(0, count.Outstanding);
        Assert.Empty(h.Event.Encoded);
        h.Rings.BeginRecording();
    }

    // #1682. The prefix is deliberately not a timeline submission. Only final completion permits reuse.
    [Fact]
    public void BothBuffersOwnOneRingAndArenaSlotUntilTheFinalSubmissionCompletes()
    {
        using var h = new MetalRingHarness(framesInFlight: 1);
        var buffers = new FakeMetalCommandBufferSource();
        var uncommitted = new MetalUncommittedBuffers(1, new RecordingLogger());
        using var list = h.NewList(new(), buffers, uncommitted: uncommitted);
        var api = new FakeMetalEarlyFrameApi();
        list.EarlySubmission = new(api, h.SubmitLock);
        list.Begin();
        int segment = list.RingSegment;
        var firstLease = list.Arena.Take(16);
        firstLease.Span.Fill(7);
        list.SetFramebuffer(Drawable());
        Assert.Empty(h.Event.Encoded);
        Assert.Equal(0ul, h.Rings.SegmentOwner(segment));
        Assert.Equal(0ul, list.Arena.SlotValue(segment));
        Assert.Equal(segment, list.RingSegment);
        Assert.Equal(segment, list.Arena.Slot);
        Assert.Equal(1, uncommitted.Outstanding);
        var tailLease = list.Arena.Take(16);
        Assert.NotEqual(firstLease.OffsetBytes, tailLease.OffsetBytes);
        Assert.Equal(7, firstLease.Span[0]);
        list.ClearColorTarget(0, Color.Black);
        list.End();
        IntPtr final = list.SealedCommandBuffer;
        ulong value = h.Submit(list);
        buffers.Release(final);
        Assert.Equal(value, h.Rings.SegmentOwner(segment));
        Assert.Equal(value, list.Arena.SlotValue(segment));
        Assert.Single(h.Event.Encoded);
        Assert.Equal(0, uncommitted.Outstanding);
        // The held completion makes backpressure observable without a clock or a sleep.
        h.Event.OnWait = () => h.Event.Completed = value;
        list.Begin();
        Assert.Equal(value, h.Event.Completed);
        Assert.True(h.Rings.StallCount > 0);
        list.End();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandonedOrFailedTailQuarantinesTheSlotWithoutInventingASignal(bool acquisitionFails)
    {
        using var h = new MetalRingHarness(framesInFlight: 1);
        var buffers = new FakeMetalCommandBufferSource();
        var count = new MetalUncommittedBuffers(1, new RecordingLogger());
        var api = new FakeMetalEarlyFrameApi { OnCommit = () => buffers.NextAcquireFails = acquisitionFails };
        var list = h.NewList(new(), buffers, uncommitted: count);
        list.EarlySubmission = new(api, h.SubmitLock);
        list.Begin();
        if (acquisitionFails) Assert.Throws<InvalidOperationException>(() => list.SetFramebuffer(Drawable()));
        else list.SetFramebuffer(Drawable());
        list.Dispose();
        Assert.Equal(0, count.Outstanding);
        Assert.Equal(0, buffers.Outstanding);
        Assert.Empty(h.Event.Encoded);
        Assert.Equal(0ul, h.Rings.SegmentOwner(0));
        Assert.Equal(MetalFrameAccessFailure.AbandonedFrameSlot,
            Assert.Throws<MetalFrameAccessException>(() => h.Rings.BeginRecording()).Reason);
    }

    [Fact]
    public void TailUniformWritesAreRangeCheckedEvenThroughTheDeviceWideWriter()
    {
        using var h = new MetalRingHarness(framesInFlight: 1);
        MetalBuffer source = h.NewBuffer(128, GpuBufferUsage.UniformBuffer);
        MetalBuffer destination = h.NewBuffer(128, GpuBufferUsage.Staging);
        using var list = h.NewList(new());
        list.EarlySubmission = new(new FakeMetalEarlyFrameApi(), h.SubmitLock);
        list.Begin();
        list.UpdateBuffer(source, 0, 7u);
        list.CopyBuffer(source, 0, destination, 0, 16);
        list.SetFramebuffer(Drawable());
        // The blit remains open until the clear-only rendering boundary, so it is not split into two encoders.
        list.ClearColorTarget(0, Color.Black);
        list.ResolveDeferredDrawable();
        Assert.Equal(MetalFrameAccessFailure.OverlappingWrite,
            Assert.Throws<MetalFrameAccessException>(() => list.UpdateBuffer(source, 0, 9u)).Reason);
        Assert.Throws<MetalFrameAccessException>(() => h.Rings.UpdateBuffer(source.Ring!, 0, new byte[] { 11 }));
        // Refusal must leave even earlier immediate-copy targets and pending patches unchanged.
        Assert.Equal(7, source.Ring!.ReadSegment(0, 0, 1)[0]);
        Assert.False(source.Ring.HasPendingPatches);
        h.Rings.UpdateBuffer(source.Ring!, 64, new byte[] { 9 });
        Assert.Equal(9, source.Ring.ReadSegment(list.RingSegment, 64, 1)[0]);
        list.End();
        h.Submit(list);
        h.Event.Completed = h.Timeline.LastSubmitted;
        h.Rings.UpdateBuffer(source.Ring!, 0, new byte[] { 9 });
        Assert.Equal(9, source.Ring.ReadSegment(0, 0, 1)[0]);
        h.DisposeWithoutRelease(source);
        h.DisposeWithoutRelease(destination);
    }

    // #1682. Submitted previous-frame storage is deferred, or waited for at depth one, rather than overwritten.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ADeviceUniformUpdatePreservesTheSplitPreviousFramesBytes(int depth)
    {
        using var h = new MetalRingHarness(framesInFlight: depth);
        MetalBuffer source = h.NewBuffer(128, GpuBufferUsage.UniformBuffer);
        MetalBuffer destination = h.NewBuffer(128, GpuBufferUsage.Staging);
        using var list = h.NewList(new());
        list.EarlySubmission = new(new FakeMetalEarlyFrameApi(), h.SubmitLock);
        // Buffer copies address physical bytes. Align this recording with physical segment zero.
        if (depth > 1) { h.Rings.BeginRecording(); h.Rings.BeginRecording(); }
        list.Begin();
        int previous = list.RingSegment;
        list.UpdateBuffer(source, 0, 7u);
        list.CopyBuffer(source, 0, destination, 0, 16);
        list.SetFramebuffer(Drawable());
        list.ClearColorTarget(0, Color.Black);
        list.End();
        ulong final = h.Submit(list);
        Assert.Equal(0ul, h.Event.Completed);
        if (depth > 1) h.Rings.BeginRecording();
        h.Event.OnWait = () => Assert.Equal(7, source.Ring!.ReadSegment(previous, 0, 1)[0]);

        h.Rings.UpdateBuffer(source.Ring!, 0, new byte[] { 9 });

        if (depth == 1) Assert.Equal(final, h.Event.Completed);
        else
        {
            Assert.Equal(0ul, h.Event.Completed);
            Assert.Equal(7, source.Ring!.ReadSegment(previous, 0, 1)[0]);
            Assert.True(source.Ring.HasPendingPatchesFor(previous));
            Assert.Equal(9, source.Ring.ReadSegment(h.Rings.CurrentSegment, 0, 1)[0]);
            h.Event.Completed = final;
            h.Rings.BeginRecording();
            h.Rings.BeginRecording();
        }
        Assert.Equal(9, source.Ring!.ReadSegment(previous, 0, 1)[0]);
        Assert.False(source.Ring.HasPendingPatchesFor(previous));
        h.DisposeWithoutRelease(source);
        h.DisposeWithoutRelease(destination);
    }

    [Fact]
    public void OffscreenOnlyRecordingKeepsOneBuffer()
    {
        using var h = new MetalRingHarness();
        var buffers = new FakeMetalCommandBufferSource();
        using var list = h.NewList(new(), buffers);
        list.EarlySubmission = new(new FakeMetalEarlyFrameApi(), h.SubmitLock);
        list.Begin();
        list.End();
        Assert.Single(buffers.Acquired);
        Assert.Empty(h.Event.Encoded);
    }
}
