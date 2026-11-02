using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class MetalEarlyFrameSubmitTests
{
    // #1682. The only extra queue commit belongs before drawable acquisition, at the natural pass boundary.
    [Theory]
    [InlineData(false, 72, true, false)]
    [InlineData(true, 71, true, false)]
    [InlineData(true, 72, false, false)]
    [InlineData(true, 72, true, true)]
    [InlineData(true, 144, true, false)]
    public void Split_is_sampling_frame_and_block_gated_and_precedes_acquire(bool sampling, int callback, bool frame, bool expected)
    {
        using var harness = new MetalRingHarness();
        var buffers = new FakeMetalCommandBufferSource();
        var calls = new FakeMetalEncoderCalls();
        var uncommitted = new MetalUncommittedBuffers(harness.FramesInFlight);
        using var list = harness.NewList(new object(), buffers, calls, uncommitted);
        var native = new SubmitApi(list);
        list.EarlySubmit = new(native, harness.SubmitLock);
        var early = new FrameEarlySubmitCapture(200);
        using var capture = new FrameTimingCapture(frame ? list : null, earlySubmit: early);
        early.Boundary(0, 0);
        if (callback >= 72) early.Boundary(72, 72);
        if (callback >= 144) early.Boundary(144, 144);
        capture.CallbackIndex = callback;
        if (sampling) capture.Start();
        list.Begin();
        IntPtr first = list.RecordingCommandBuffer;
        list.Encoders.EnsureRenderEncoder(new(99));
        bool acquiredAfterCommit = false;
        var framebuffer = new MetalSwapchainFramebuffer(GpuPixelFormat.R8G8B8A8UNorm, new(16, 16), () =>
        {
            acquiredAfterCommit = native.Committed.Contains(first);
        });
        list.SetFramebuffer(framebuffer);
        Assert.Equal(expected, acquiredAfterCommit);
        Assert.Equal(expected, first != list.RecordingCommandBuffer);
        Assert.Equal(expected, early.Snapshot(200).AppliedPerCallback[callback]);
        if (expected)
        {
            Assert.Equal(new[] { first }, native.Handlers);
            Assert.Equal(MetalEncoderKind.None, native.OpenAtCommit);
            Assert.Equal(0UL, harness.Timeline.LastSubmitted);
            Assert.Equal(1, uncommitted.Outstanding);
            Assert.Equal(1, uncommitted.Peak);
            Assert.Equal(0UL, list.Arena.SlotValue(list.RingSegment));
            IntPtr final = list.RecordingCommandBuffer;
            // Capture taps append to the current list after the game's drawable pass.
            list.Encoders.EnsureBlitEncoder();
            list.End();
            Assert.Equal(final, list.SealedCommandBuffer);
            ulong value = harness.Submit(list);
            Assert.Equal(value, harness.Rings.SegmentOwner(list.RingSegment));
            Assert.Equal(value, list.Arena.SlotValue(list.RingSegment));
            Assert.False(harness.Rings.EarlyFrames.Pending(list.RingSegment));
            Assert.Equal(0, uncommitted.Outstanding);
        }
    }

    [Theory]
    [InlineData("dispose")]
    [InlineData("discard")]
    [InlineData("replacement")]
    [InlineData("encoder")]
    public void Abandoned_early_work_quarantines_the_slot_and_never_leaks_an_uncommitted_buffer(string exit)
    {
        using var harness = new MetalRingHarness(framesInFlight: 1);
        var buffers = new FakeMetalCommandBufferSource();
        var uncommitted = new MetalUncommittedBuffers(1);
        using var list = harness.NewList(new object(), buffers: buffers, uncommitted: uncommitted);
        list.EarlySubmit = new(new SubmitApi(list), harness.SubmitLock);
        var early = new FrameEarlySubmitCapture(100);
        using var capture = new FrameTimingCapture(list, earlySubmit: early);
        early.Boundary(72, 72);
        capture.Start();
        capture.CallbackIndex = 72;
        list.Begin();
        list.Encoders.EnsureRenderEncoder(new(99));
        int slot = list.RingSegment;
        if (exit == "replacement") buffers.NextAcquireFails = true;
        if (exit == "replacement") Assert.Throws<InvalidOperationException>(list.SplitBeforeDrawableAcquire);
        else
        {
            list.SplitBeforeDrawableAcquire();
            if (exit == "encoder") list.Encoders.EnsureBlitEncoder();
        }
        if (exit == "discard") { list.End(); list.DiscardRecording(); }
        else list.Dispose();
        Assert.Equal(0, uncommitted.Outstanding);
        Assert.True(harness.Rings.EarlyFrames.Pending(slot));
        Assert.Throws<InvalidOperationException>(() => harness.Rings.BeginRecording());
        Assert.Equal(0UL, harness.Timeline.LastSubmitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dead_device_or_handler_failure_does_not_commit_a_prefix(bool handlerFailure)
    {
        using var harness = new MetalRingHarness();
        using var list = harness.NewList(new object());
        var native = new SubmitApi(list) { IsAlive = handlerFailure, HandlerAvailable = !handlerFailure };
        list.EarlySubmit = new(native, harness.SubmitLock);
        using var capture = new FrameTimingCapture(list, earlySubmit: new FrameEarlySubmitCapture(100));
        capture.EarlySubmit!.Boundary(72, 72);
        capture.Start();
        capture.CallbackIndex = 72;
        list.Begin();
        list.Encoders.EnsureRenderEncoder(new(99));
        if (handlerFailure) Assert.Throws<InvalidOperationException>(list.SplitBeforeDrawableAcquire);
        else list.SplitBeforeDrawableAcquire();
        Assert.Empty(native.Committed);
        Assert.False(capture.EarlySubmit.Snapshot(100).AppliedPerCallback[72]);
    }

    [Fact]
    public void Early_uniform_rewrites_and_tail_device_work_are_counted_and_off_timeline_updates_defer_until_the_final_signal()
    {
        using var harness = new MetalRingHarness(framesInFlight: 1);
        var ring = harness.NewRing(16, out _);
        using var list = harness.NewList(new object());
        list.EarlySubmit = new(new SubmitApi(list), harness.SubmitLock);
        using var capture = new FrameTimingCapture(list, earlySubmit: new FrameEarlySubmitCapture(100));
        capture.EarlySubmit!.Boundary(72, 72);
        capture.Start();
        capture.CallbackIndex = 72;
        list.Begin();
        int slot = list.RingSegment;
        ring.Write(slot, 0, new byte[] { 1, 2, 3, 4 });
        ring.RecordRead(slot);
        list.Encoders.EnsureRenderEncoder(new(99));
        var lease = list.Arena.Take(4);
        list.SplitBeforeDrawableAcquire();
        // A real frame's tail must never fail, so a rewrite of a prefix-read uniform is counted against its callback.
        ring.Write(slot, 8, new byte[] { 9 });
        Assert.Equal(1, capture.EarlySubmit.Snapshot(100).PrefixUniformRewritesPerCallback[72]);
        // A resource first referenced in the tail was not read by the prefix and remains writable.
        var tailRing = harness.NewRing(16, out _);
        tailRing.RecordRead(slot);
        tailRing.Write(slot, 0, new byte[] { 7 });
        Assert.Equal(7, tailRing.ReadSegment(slot, 0, 1)[0]);
        // SpriteBatch uploads its quads through the device after the drawable is bound, so tail device work is counted.
        harness.Rings.EarlyFrames.NoteDeviceWork();
        Assert.Equal(1, capture.EarlySubmit.Snapshot(100).TailDeviceWorkPerCallback[72]);
        harness.Rings.UpdateBuffer(ring, 0, new byte[] { 9 });
        Assert.Equal(1, ring.ReadSegment(slot, 0, 1)[0]);
        Assert.True(ring.HasPendingPatchesFor(slot));
        var tailLease = list.Arena.Take(4);
        Assert.True(lease.Buffer != tailLease.Buffer || lease.OffsetBytes != tailLease.OffsetBytes);
        list.End();
        ulong final = harness.Submit(list);
        harness.Rings.EarlyFrames.NoteDeviceWork();
        Assert.Equal(1, capture.EarlySubmit.Snapshot(100).TailDeviceWorkTotal);
        harness.Event.Completed = final;
        list.Begin();
        Assert.Equal(9, ring.ReadSegment(list.RingSegment, 0, 1)[0]);
    }

    [Fact]
    public void A_stopped_capture_cannot_split_even_in_an_on_block()
    {
        using var harness = new MetalRingHarness();
        using var list = harness.NewList(new object());
        var native = new SubmitApi(list);
        list.EarlySubmit = new(native, harness.SubmitLock);
        using var capture = new FrameTimingCapture(list, earlySubmit: new FrameEarlySubmitCapture(100));
        capture.EarlySubmit!.Boundary(72, 72);
        capture.Start();
        capture.CallbackIndex = 72;
        capture.Stop();
        list.Begin();
        list.Encoders.EnsureRenderEncoder(new(99));
        list.SplitBeforeDrawableAcquire();
        Assert.Empty(native.Committed);
    }

    [Theory]
    [InlineData((int)MetalEncoderKind.Blit)]
    [InlineData((int)MetalEncoderKind.Compute)]
    public void A_swapchain_bind_does_not_split_a_nonrender_encoder_that_may_continue_after_the_bind(int kindValue)
    {
        var kind = (MetalEncoderKind)kindValue;
        using var harness = new MetalRingHarness();
        using var list = harness.NewList(new object());
        var native = new SubmitApi(list);
        list.EarlySubmit = new(native, harness.SubmitLock);
        using var capture = new FrameTimingCapture(list, earlySubmit: new FrameEarlySubmitCapture(100));
        capture.EarlySubmit!.Boundary(72, 72);
        capture.Start();
        capture.CallbackIndex = 72;
        list.Begin();
        IntPtr prefix = list.RecordingCommandBuffer;
        if (kind == MetalEncoderKind.Blit) list.Encoders.EnsureBlitEncoder();
        else list.Encoders.EnsureComputeEncoder();
        MetalSwapchainFramebuffer? framebuffer = null;
        framebuffer = new(GpuPixelFormat.R8G8B8A8UNorm, new(16, 16), () =>
            framebuffer!.Adopt(new(new(42), GpuPixelFormat.R8G8B8A8UNorm), new(16, 16)));
        list.SetFramebuffer(framebuffer);
        Assert.Equal(kind, list.Encoders.Open);
        Assert.Equal(prefix, list.RecordingCommandBuffer);
        Assert.Empty(native.Committed);
        Assert.False(capture.EarlySubmit.Snapshot(100).AppliedPerCallback[72]);
    }

    sealed class SubmitApi(MetalCommandList list) : IMetalEarlyFrameSubmitApi
    {
        public bool IsAlive { get; set; } = true;
        internal bool HandlerAvailable = true;
        internal List<IntPtr> Handlers = [];
        internal List<IntPtr> Committed = [];
        internal MetalEncoderKind OpenAtCommit;
        public void FlushSetup() { }
        public bool AttachCompletionHandler(IntPtr buffer)
        {
            Handlers.Add(buffer);
            return HandlerAvailable;
        }
        public void Commit(IntPtr buffer, MetalCommandList commands)
        {
            Assert.Same(list, commands);
            Assert.Contains(buffer, Handlers);
            OpenAtCommit = commands.Encoders.Open;
            Committed.Add(buffer);
        }
    }
}
