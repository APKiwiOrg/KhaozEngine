using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The Direct3D 11 half of <see cref="IGpuSwapchainCapture"/>, device free over
    /// <see cref="FakeD3D11SwapchainSurface"/>. A blit-model back buffer is already a <c>CopyResource</c> source,
    /// so copyability follows the attachments alone, and the copy names the CURRENT colour view as its source.
    /// The native <c>GetResource</c> and <c>CopyResource</c> behind that source run only on a Windows device.
    /// </summary>
    public sealed class D3D11SwapchainCaptureTests
    {
        [Fact]
        public void The_back_buffer_is_copyable_without_any_request()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            int callsBefore = surface.Calls.Count;

            Assert.False(swapchain.CopySourceRequested);
            Assert.NotNull(swapchain.CurrentColourView);

            swapchain.CopySourceRequested = true;
            Assert.NotNull(swapchain.CurrentColourView);
            swapchain.CopySourceRequested = false;
            Assert.NotNull(swapchain.CurrentColourView);

            swapchain.Present();
            Assert.Same(surface.LastRenderTargetView, swapchain.CurrentColourView);
            Assert.Equal(new[] { "Present 1" }, surface.Trace[callsBefore..]);
        }

        [Fact]
        public void A_swapchain_with_its_attachments_released_is_not_copyable()
        {
            var fake = new FakeD3D11SwapchainSurface(1280, 720);
            var surface = new ObservedSurface(fake);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            surface.DuringResizeBuffers = () => surface.SeenDuringResize = swapchain.CurrentColourView;
            surface.SeenDuringResize = new object();

            swapchain.QueueResize(1600, 900);
            swapchain.Present();

            Assert.Null(surface.SeenDuringResize);
            Assert.NotNull(swapchain.CurrentColourView);

            swapchain.Dispose();
            Assert.Null(swapchain.CurrentColourView);
        }

        [Fact]
        public void The_copy_source_is_the_current_colour_attachment_after_a_resize()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            object released = surface.LastRenderTargetView!;
            swapchain.QueueResize(1600, 900);
            swapchain.Present();
            var destination = new FakeTexture(1600, 900, 1, 1, GpuPixelFormat.B8G8R8A8UNorm);
            var commands = new RecordingGpuCommandList(new NullGpuCommandList());

            D3D11BackBufferCopySource.RecordCopy(swapchain, commands, boundThisRecording: true, destination);

            RecordingGpuCommandList.TextureCopy copy = Assert.Single(commands.TextureCopies);
            Assert.True(copy.WholeResource);
            Assert.Same(destination, copy.Destination);
            var source = Assert.IsType<D3D11BackBufferCopySource>(copy.Source);
            Assert.Same(surface.LastRenderTargetView, source.ColourView);
            Assert.NotSame(released, source.ColourView);
            Assert.Equal((1600u, 900u, 1u, 1u, GpuPixelFormat.B8G8R8A8UNorm),
                (source.Width, source.Height, source.MipLevels, source.SampleCount, source.Format));
        }

        [Fact]
        public void A_copy_of_an_uncopyable_swapchain_throws_InvalidOperationException()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            swapchain.Dispose();
            var destination = new FakeTexture(1280, 720, 1, 1, GpuPixelFormat.B8G8R8A8UNorm);
            var commands = new RecordingGpuCommandList(new NullGpuCommandList());

            Assert.Throws<InvalidOperationException>(
                () => D3D11BackBufferCopySource.RecordCopy(swapchain, commands, destination));
            Assert.Throws<InvalidOperationException>(
                () => D3D11BackBufferCopySource.RecordCopy(null, commands, destination));
            Assert.Empty(commands.TextureCopies);
        }

        /// <summary>
        /// A LIST THAT NEVER BOUND THE SWAPCHAIN IS REFUSED, with the message every backend gives, over a real
        /// recorder and the native trace emitter. Binding an ordinary framebuffer does not count, a foreign list
        /// never counts, and binding the swapchain framebuffer does until the next <c>Begin</c>.
        /// </summary>
        [Fact]
        public void A_swapchain_copy_from_a_list_that_never_bound_the_swapchain_is_refused()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            var destination = new FakeTexture(1280, 720, 1, 1, GpuPixelFormat.B8G8R8A8UNorm);
            var offscreen = new FakeFramebuffer(new GpuOutputDescription(null, GpuPixelFormat.B8G8R8A8UNorm), 64, 64);
            var log = new D3D11NativeCallLog();
            using var recorder = new D3D11CommandRecorder<D3D11NativeTraceEmitter>(
                new D3D11NativeTraceEmitter(new D3D11DeviceState(), log));

            recorder.Begin();
            recorder.SetFramebuffer(offscreen);
            log.Reset();

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                () => D3D11BackBufferCopySource.RecordCopy(swapchain, recorder, destination));
            Assert.Equal(SwapchainCopyCheck.NotBoundMessage, refused.Message);
            Assert.Empty(log.Trace);

            var foreign = new RecordingGpuCommandList(new NullGpuCommandList());
            Assert.Throws<InvalidOperationException>(
                () => D3D11BackBufferCopySource.RecordCopy(swapchain, foreign, destination));
            Assert.Empty(foreign.TextureCopies);

            recorder.SetFramebuffer(swapchain.Framebuffer);
            Assert.True(recorder.BoundSwapchainFramebuffer);
            log.Reset();
            D3D11BackBufferCopySource.RecordCopy(swapchain, recorder, destination);
            Assert.Single(log.Trace);
            recorder.End();

            recorder.Begin();
            Assert.False(recorder.BoundSwapchainFramebuffer);
            Assert.Throws<InvalidOperationException>(
                () => D3D11BackBufferCopySource.RecordCopy(swapchain, recorder, destination));
            recorder.End();
        }

        /// <summary>Forwards to the fake and runs a callback inside <c>ResizeBuffers</c>, which is the one moment
        /// the swapchain has released its attachments and not yet created the next ones.</summary>
        sealed class ObservedSurface : ID3D11SwapchainSurface
        {
            readonly FakeD3D11SwapchainSurface _inner;

            internal ObservedSurface(FakeD3D11SwapchainSurface inner) => _inner = inner;

            internal Action? DuringResizeBuffers { get; set; }

            internal object? SeenDuringResize { get; set; }

            public GpuPixelFormat ColourFormat => _inner.ColourFormat;

            public GpuPixelFormat? DepthFormat => _inner.DepthFormat;

            public void ReleaseAttachments() => _inner.ReleaseAttachments();

            public void ResizeBuffers(uint width, uint height)
            {
                DuringResizeBuffers?.Invoke();
                _inner.ResizeBuffers(width, height);
            }

            public D3D11SwapchainAttachments CreateAttachments(uint width, uint height)
                => _inner.CreateAttachments(width, height);

            public int Present(int syncInterval) => _inner.Present(syncInterval);

            public void Dispose() => _inner.Dispose();
        }
    }
}
