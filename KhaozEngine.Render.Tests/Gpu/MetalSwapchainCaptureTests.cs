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

            Assert.True(api.FramebufferOnly);
            Assert.True(boundary.HasDrawable);
            Assert.False(boundary.CurrentDrawableCopyable);
        }

        [Fact]
        public void A_copy_source_request_clears_framebuffer_only_before_the_next_drawable_is_taken()
        {
            (FakeMetalSwapchainApi api, MetalPresentBoundary boundary) = Build(scriptDrawables: 2);
            api.Log.Clear();

            boundary.Pending.QueueFramebufferOnly(false);
            boundary.Present();

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

            boundary.Pending.QueueFramebufferOnly(false);

            // MID-FRAME: the held drawable was vended framebuffer only, and queueing changes nothing about it.
            Assert.True(boundary.HasDrawable);
            Assert.False(boundary.CurrentDrawableCopyable);
            Assert.True(api.FramebufferOnly);

            boundary.Present();

            Assert.True(boundary.HasDrawable);
            Assert.True(boundary.CurrentDrawableCopyable);
        }

        [Fact]
        public void Releasing_the_request_restores_framebuffer_only()
        {
            (FakeMetalSwapchainApi api, MetalPresentBoundary boundary) = Build(scriptDrawables: 3);

            boundary.Pending.QueueFramebufferOnly(false);
            boundary.Present();
            Assert.True(boundary.CurrentDrawableCopyable);

            boundary.Pending.QueueFramebufferOnly(true);
            boundary.Present();

            Assert.True(api.FramebufferOnly);
            Assert.True(boundary.HasDrawable);
            Assert.False(boundary.CurrentDrawableCopyable);
        }

        [Fact]
        public void A_recycled_drawable_that_still_reports_framebuffer_only_is_not_copyable()
        {
            (FakeMetalSwapchainApi api, MetalPresentBoundary boundary) = Build(scriptDrawables: 1);

            // THE LAYER RECYCLES A POOLED DRAWABLE made before the write, then makes a fresh one.
            boundary.Pending.QueueFramebufferOnly(false);
            api.ScriptDrawable(framebufferOnly: true);
            api.ScriptDrawable();
            boundary.Present();

            Assert.False(api.FramebufferOnly);
            Assert.True(boundary.HasDrawable);
            Assert.False(boundary.CurrentDrawableCopyable);

            boundary.Present();

            Assert.True(boundary.CurrentDrawableCopyable);
        }

        [Fact]
        public void An_orphan_bound_boundary_is_never_copyable()
        {
            (FakeMetalSwapchainApi api, MetalPresentBoundary boundary) = Build(scriptDrawables: 1);

            // A MINIMISED LAYER: the request lands, and the next acquire hands back no drawable.
            boundary.Pending.QueueFramebufferOnly(false);
            api.ScriptNoDrawable();
            boundary.Present();

            Assert.False(api.FramebufferOnly);
            Assert.True(boundary.IsOrphanBound);
            Assert.False(boundary.HasDrawable);
            Assert.False(boundary.CurrentDrawableCopyable);
        }

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
