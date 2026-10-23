using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class MetalDeferredDrawableTests
    {
        [Fact]
        public void Construction_publishes_metadata_without_acquiring_a_drawable()
        {
            using var h = new Harness();
            Assert.Equal(new[] { "configure" }, h.Api.Log);
            Assert.Equal(64u, h.Boundary.Framebuffer.Width);
            Assert.Equal(32u, h.Boundary.Framebuffer.Height);
            Assert.Equal(GpuPixelFormat.B8G8R8A8UNorm, h.Boundary.Framebuffer.Outputs.Colour[0]);
            Assert.Null(h.Boundary.Framebuffer.Outputs.Depth);
            Assert.Equal(1, h.Boundary.Framebuffer.Outputs.SampleCount);
            Assert.Equal(0uL, h.Boundary.Framebuffer.Generation);
            Assert.Equal(IntPtr.Zero, h.Boundary.Framebuffer.Attachment.Texture);
            Assert.Equal(0L, h.Boundary.AcquireTotals.Count);
        }

        [Fact]
        public void A_cached_wrapper_resolves_once_per_frame_and_publishes_fresh_bindings()
        {
            using var h = new Harness();
            IMetalBoundFramebufferSource cached = h.Boundary.Framebuffer;
            Assert.Equal(0, h.Api.AcquireCount);
            MetalBoundFramebuffer first = cached.AsBound;
            Assert.Equal(1, h.Api.AcquireCount);
            Assert.Equal(first.Id, cached.AsBound.Id);
            Assert.Equal(1uL, h.Boundary.Framebuffer.Generation);

            h.Boundary.Present();
            Assert.Equal(1, h.Api.AcquireCount);
            Assert.Equal(IntPtr.Zero, h.Boundary.Framebuffer.Attachment.Texture);
            Assert.False(h.Boundary.CurrentDrawableCopyable);

            MetalBoundFramebuffer next = cached.AsBound;
            Assert.Equal(2, h.Api.AcquireCount);
            Assert.NotEqual(first.Id, next.Id);
            Assert.Equal(2uL, h.Boundary.Framebuffer.Generation);
            Assert.Equal(next.Id, cached.AsBound.Id);
        }

        [Fact]
        public void Present_without_a_bind_acquires_only_the_frame_it_presents()
        {
            using var h = new Harness();
            h.Api.Log.Clear();
            h.Boundary.Present();
            h.Boundary.Present();
            Assert.Equal(new[] { "acquire", "acquirePresentBuffer", "present", "releaseDrawable", "capture",
                "acquire", "acquirePresentBuffer", "present", "releaseDrawable", "capture" }, h.Api.Log);
            Assert.Equal(2L, h.Boundary.FramesBegun);
            Assert.Equal(2L, h.Boundary.AcquireTotals.Count);
            Assert.False(h.Boundary.HasDrawable);
            Assert.False(h.Api.AcquireSawLockHeld);
            Assert.False(h.Api.PresentBufferSawLockHeld);
            Assert.True(h.Api.PresentSawLockHeld);
        }

        [Fact]
        public void Nil_is_one_attempt_per_frame_and_recovery_replaces_the_orphan_before_release()
        {
            using var h = new Harness(drawables: 0);
            h.Api.ScriptNoDrawable();
            h.Api.ScriptDrawable();
            MetalBoundFramebuffer orphan = h.Source.AsBound;
            Assert.True(h.Boundary.IsOrphanBound);
            Assert.Equal(orphan.Id, h.Source.AsBound.Id);
            Assert.Equal(1, h.Api.AcquireCount);
            h.Boundary.Present();
            Assert.Equal(1, h.Api.AcquireCount);
            Assert.Equal(1L, h.Boundary.SkippedPresents);
            Assert.True(h.Orphan.IsLive);

            h.Orphan.OnRelease = () => Assert.Equal(h.Api.Handed[1].Texture,
                h.Boundary.Framebuffer.Attachment.Texture);
            MetalBoundFramebuffer replacement = h.Source.AsBound;
            Assert.Equal(2, h.Api.AcquireCount);
            Assert.NotEqual(orphan.Id, replacement.Id);
            Assert.False(h.Orphan.IsLive);
            h.Orphan.OnRelease = null;
        }

        [Fact]
        public void Resize_publishes_dimensions_at_present_without_acquiring_the_next_frame()
        {
            using var h = new Harness();
            _ = h.Source.AsBound;
            h.Boundary.QueueResize(0, 80);
            h.Boundary.Present();
            Assert.Equal(1, h.Api.AcquireCount);
            Assert.Equal(1u, h.Boundary.Framebuffer.Width);
            Assert.Equal(80u, h.Boundary.Framebuffer.Height);
            Assert.Equal(IntPtr.Zero, h.Boundary.Framebuffer.Attachment.Texture);
            Assert.Equal(1uL, h.Boundary.Framebuffer.Generation);
            MetalBoundFramebuffer bound = h.Source.AsBound;
            Assert.Equal(1u, bound.Width);
            Assert.Equal(80u, bound.Height);
        }

        [Fact]
        public void Owner_disposal_refuses_cached_binds_without_acquiring()
        {
            using var h = new Harness();
            IMetalBoundFramebufferSource cached = h.Source;
            h.Boundary.Dispose();
            Assert.Throws<ObjectDisposedException>(() => cached.AsBound);
            Assert.Equal(0, h.Api.AcquireCount);
        }

        [Fact]
        public void External_framebuffer_disposal_keeps_deferred_binding_working()
        {
            using var h = new Harness();
            h.Boundary.Framebuffer.Dispose();
            Assert.Equal(0, h.Api.AcquireCount);
            _ = h.Source.AsBound;
            h.Boundary.Present();
            Assert.True(h.Source.AsBound.IsBound);
            Assert.Equal(2, h.Api.AcquireCount);
        }

        [Fact]
        public void Binding_outside_a_recording_does_not_acquire()
        {
            using var h = new Harness();
            using var ring = new MetalRingHarness();
            using MetalCommandList commands = ring.NewList(new object());
            Assert.Throws<InvalidOperationException>(() => commands.SetFramebuffer(h.Boundary.Framebuffer));
            Assert.Equal(0, h.Api.AcquireCount);
        }

        [Fact]
        public void Frame_preparation_precedes_acquisition_and_clear_only_frames_keep_the_same_target()
        {
            using var h = new Harness();
            using var ring = new MetalRingHarness();
            var render = new FakeMetalRenderCalls();
            using MetalCommandList commands = ring.NewList(new object(), render: render);
            using var device = new FrameDevice(h.Boundary);
            var frame = new Frame { Commands = commands, Width = 64, Height = 32 };

            for (int i = 0; i < 2; i++)
            {
                int index = i;
                FramePhases.Run(frame, true, device, commands, Color.Black,
                    onPrepare: _ => Assert.Equal(index, h.Api.AcquireCount),
                    onFrame: _ =>
                    {
                        Assert.Equal(index + 1, h.Api.AcquireCount);
                        commands.SetFramebuffer(h.Boundary.Framebuffer);
                    });
            }

            Assert.Equal(2, render.Passes.Count);
            Assert.Equal(h.Api.Handed[0].Texture, render.Passes[0].Colour[0].Texture);
            Assert.Equal(h.Api.Handed[1].Texture, render.Passes[1].Colour[0].Texture);
            Assert.Equal(2, h.Api.AcquireCount);
        }

        internal sealed class Harness : IDisposable
        {
            internal FakeMetalSwapchainApi Api { get; } = new();
            internal FakeMetalOrphanTarget Orphan { get; }
            internal MetalPresentBoundary Boundary { get; }
            internal IMetalBoundFramebufferSource Source => Boundary.Framebuffer;

            internal Harness(int drawables = 3)
            {
                for (int i = 0; i < drawables; i++) Api.ScriptDrawable();
                Orphan = new FakeMetalOrphanTarget(Api.Log);
                var submitLock = new object();
                Api.LockToWatch = submitLock;
                Boundary = new MetalPresentBoundary(Api, Orphan,
                    new MetalUncommittedBuffers(3, new RecordingLogger()), new WaitAccumulator(), submitLock,
                    () => Api.Note("drain"), () =>
                    {
                        Assert.False(System.Threading.Monitor.IsEntered(submitLock));
                        Api.Note("capture");
                    }, new MetalDrawableSize(64, 32), false,
                    true, 3, new RecordingLogger());
            }

            public void Dispose() => Boundary.Dispose();
        }

        internal sealed class FrameDevice(MetalPresentBoundary boundary) : IGpuDevice
        {
            readonly FakeGpuDevice _inner = new();
            internal int Submits, Presents;
            public GpuBackendKind Backend => _inner.Backend;
            public GpuCapabilities Capabilities => _inner.Capabilities;
            public IGpuResourceFactory Factory => _inner.Factory;
            public IGpuFramebuffer? SwapchainFramebuffer => boundary.Framebuffer;
            public IGpuSampler PointSampler => _inner.PointSampler;
            public IGpuSampler LinearSampler => _inner.LinearSampler;
            public bool SyncToVerticalBlank { get => boundary.SyncToVerticalBlank; set => boundary.SyncToVerticalBlank = value; }
            public void Present() { Presents++; boundary.Present(); }
            public void ResizeSwapchain(uint w, uint h) => boundary.QueueResize(w, h);
            public void Submit(IGpuCommandList cl) { Submits++; }
            public void Submit(IGpuCommandList cl, IGpuFence fence) { Submits++; _inner.Submit(cl, fence); }
            public void Submit(IGpuCommandList cl, ReadOnlySpan<IGpuFence> fences) { Submits++; _inner.Submit(cl, fences); }
            public void WaitForIdle() { }
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged => throw new NotSupportedException();
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged => throw new NotSupportedException();
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged => throw new NotSupportedException();
            public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h) => throw new NotSupportedException();
            public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h, uint mip, uint layer) => throw new NotSupportedException();
            public MappedData Map(IGpuTexture t, GpuMapMode mode) => throw new NotSupportedException();
            public void Unmap(IGpuTexture t) => throw new NotSupportedException();
            public MappedData Map(IGpuBuffer b, GpuMapMode mode) => throw new NotSupportedException();
            public void Unmap(IGpuBuffer b) => throw new NotSupportedException();
            public void Dispose() => _inner.Dispose();
        }
    }
}
