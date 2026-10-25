using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The Metal half of <see cref="IGpuSwapchainCapture"/>, device free over <see cref="FakeMetalSwapchainApi"/>
    /// and <see cref="FakeMetalOrphanTarget"/>. A <c>framebufferOnly</c> drawable cannot be a blit source, so a
    /// capture request turns the layer's <c>framebufferOnly</c> off at the next present boundary, and a drawable
    /// is copyable only when its own texture reports framebufferOnly false, so a pooled one the layer recycles
    /// from before the write is not. The blit itself needs a real drawable and runs only on a
    /// device.
    /// </summary>
    public sealed class MetalSwapchainCaptureTests
    {
        const int FramesInFlight = 3;

        [Fact]
        public void The_layer_is_configured_framebuffer_only_by_default()
        {
            (FakeMetalSwapchainApi api, MetalPresentBoundary boundary) = Build(scriptDrawables: 1);
            _ = Bind(boundary);

            Assert.True(api.FramebufferOnly);
            Assert.True(boundary.HasDrawable);
            Assert.False(boundary.CurrentDrawableCopyable);
        }

        [Fact]
        public void A_copy_source_request_clears_framebuffer_only_before_the_next_drawable_is_taken()
        {
            (FakeMetalSwapchainApi api, MetalPresentBoundary boundary) = Build(scriptDrawables: 2);
            _ = Bind(boundary);
            api.Log.Clear();

            boundary.Pending.QueueFramebufferOnly(false);
            boundary.Present();
            _ = Bind(boundary);

            int write = api.Log.IndexOf("SetFramebufferOnly(False)");
            int acquire = api.Log.IndexOf("acquire");
            Assert.True(write >= 0, "the boundary never wrote framebufferOnly: " + string.Join(", ", api.Log));
            Assert.True(write < acquire, "framebufferOnly was written after the next drawable was taken: "
                + string.Join(", ", api.Log));
            Assert.False(api.FramebufferOnly);
        }

        [Fact]
        public void The_drawable_taken_before_the_request_is_not_copyable_and_the_next_one_is()
        {
            (FakeMetalSwapchainApi api, MetalPresentBoundary boundary) = Build(scriptDrawables: 2);
            _ = Bind(boundary);

            boundary.Pending.QueueFramebufferOnly(false);

            // MID-FRAME: the held drawable was vended framebuffer only, and queueing changes nothing about it.
            Assert.True(boundary.HasDrawable);
            Assert.False(boundary.CurrentDrawableCopyable);
            Assert.True(api.FramebufferOnly);

            boundary.Present();
            _ = Bind(boundary);

            Assert.True(boundary.HasDrawable);
            Assert.True(boundary.CurrentDrawableCopyable);
        }

        [Fact]
        public void Releasing_the_request_restores_framebuffer_only()
        {
            (FakeMetalSwapchainApi api, MetalPresentBoundary boundary) = Build(scriptDrawables: 1);
            _ = Bind(boundary);

            boundary.Pending.QueueFramebufferOnly(false);
            api.ScriptDrawable();
            boundary.Present();
            _ = Bind(boundary);
            Assert.True(boundary.CurrentDrawableCopyable);

            boundary.Pending.QueueFramebufferOnly(true);
            api.ScriptDrawable(framebufferOnly: false);
            api.ScriptDrawable();
            boundary.Present();
            _ = Bind(boundary);

            // Recycled textures retain their actual usage even after the layer request changes back.
            Assert.True(api.FramebufferOnly);
            Assert.True(boundary.HasDrawable);
            Assert.True(boundary.CurrentDrawableCopyable);
            boundary.Present();
            _ = Bind(boundary);
            Assert.False(boundary.CurrentDrawableCopyable);
        }

        [Fact]
        public void A_recycled_drawable_that_still_reports_framebuffer_only_is_not_copyable()
        {
            (FakeMetalSwapchainApi api, MetalPresentBoundary boundary) = Build(scriptDrawables: 1);
            _ = Bind(boundary);

            // THE LAYER RECYCLES A POOLED DRAWABLE made before the write, then makes a fresh one.
            boundary.Pending.QueueFramebufferOnly(false);
            api.ScriptDrawable(framebufferOnly: true);
            api.ScriptDrawable();
            boundary.Present();
            _ = Bind(boundary);

            Assert.False(api.FramebufferOnly);
            Assert.True(boundary.HasDrawable);
            Assert.False(boundary.CurrentDrawableCopyable);

            boundary.Present();
            _ = Bind(boundary);

            Assert.True(boundary.CurrentDrawableCopyable);
        }

        [Fact]
        public void An_orphan_bound_boundary_is_never_copyable()
        {
            (FakeMetalSwapchainApi api, MetalPresentBoundary boundary) = Build(scriptDrawables: 1);
            _ = Bind(boundary);

            // A MINIMISED LAYER: the request lands, and the next acquire hands back no drawable.
            boundary.Pending.QueueFramebufferOnly(false);
            api.ScriptNoDrawable();
            boundary.Present();
            _ = Bind(boundary);

            Assert.False(api.FramebufferOnly);
            Assert.True(boundary.IsOrphanBound);
            Assert.False(boundary.HasDrawable);
            Assert.False(boundary.CurrentDrawableCopyable);
        }

        /// <summary>
        /// A LIST THAT NEVER BOUND THE SWAPCHAIN IS REFUSED before anything is encoded, with the message every
        /// backend gives. Binding an ordinary framebuffer does not count. Binding the swapchain framebuffer does,
        /// which shows here as the copy reaching the destination check instead, because a device-free list has no
        /// <see cref="MetalTexture"/> to copy into. The next <c>Begin</c> clears it.
        /// </summary>
        [Fact]
        public void A_swapchain_copy_from_a_list_that_never_bound_the_swapchain_is_refused()
        {
            using var harness = new MetalRingHarness();
            var calls = new FakeMetalEncoderCalls();
            using MetalCommandList list = harness.NewList(new object(), calls: calls);
            var swapchain = new MetalSwapchainFramebuffer(GpuPixelFormat.B8G8R8A8UNorm,
                new MetalDrawableSize(64, 64), () => { });
            swapchain.Adopt(new MetalAttachment(new IntPtr(0x5C1), GpuPixelFormat.B8G8R8A8UNorm),
                new MetalDrawableSize(64, 64));
            var destination = new FakeTexture(64, 64, 1, 1, GpuPixelFormat.B8G8R8A8UNorm);

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer());

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                () => list.CopyFromDrawable(new IntPtr(0x5C1), destination));
            Assert.Equal(SwapchainCopyCheck.NotBoundMessage, refused.Message);
            Assert.Empty(calls.Log);

            list.SetFramebuffer(swapchain);
            Assert.Throws<ArgumentException>(() => list.CopyFromDrawable(new IntPtr(0x5C1), destination));
            list.End();

            list.Begin();
            Assert.Throws<InvalidOperationException>(() => list.CopyFromDrawable(new IntPtr(0x5C1), destination));
            list.End();
        }

        [Fact]
        public void Copyability_checks_do_not_acquire_an_unresolved_frame()
        {
            (FakeMetalSwapchainApi api, MetalPresentBoundary boundary) = Build(scriptDrawables: 2);
            Assert.False(boundary.CurrentDrawableCopyable);
            Assert.Equal(0, api.AcquireCount);
            _ = Bind(boundary);
            boundary.Present();
            Assert.False(boundary.CurrentDrawableCopyable);
            Assert.Equal(1, api.AcquireCount);
        }

        static MetalBoundFramebuffer Bind(MetalPresentBoundary boundary)
            => ((IMetalBoundFramebufferSource)boundary.Framebuffer).AsBound;

        static (FakeMetalSwapchainApi Api, MetalPresentBoundary Boundary) Build(int scriptDrawables)
        {
            var api = new FakeMetalSwapchainApi();
            var orphan = new FakeMetalOrphanTarget(api.Log);
            for (int i = 0; i < scriptDrawables; i++) api.ScriptDrawable();

            var boundary = new MetalPresentBoundary(api, orphan,
                new MetalUncommittedBuffers(FramesInFlight, new RecordingLogger()), new WaitAccumulator(),
                new object(), () => api.Note("drain"), () => api.Note("capture"),
                new MetalDrawableSize(1280u, 720u), MetalSwapchainPolicy.ColourSrgbRequested,
                syncToVerticalBlank: true, FramesInFlight, new RecordingLogger());

            return (api, boundary);
        }
    }
}
