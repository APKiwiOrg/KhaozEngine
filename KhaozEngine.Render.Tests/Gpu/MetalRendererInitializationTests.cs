using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Windowing;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalRendererInitializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Offscreen_work_precedes_the_opted_in_acquire_and_capture_sees_the_final_binding(bool rendererOwned)
    {
        using var h = new MetalDeferredDrawableTests.Harness();
        using var ring = new MetalRingHarness();
        var passes = new FakeMetalRenderCalls();
        using MetalCommandList commands = ring.NewList(new object(), render: passes);
        using var device = new MetalDeferredDrawableTests.FrameDevice(h.Boundary);
        using var taps = new FrameCaptureTaps(device, null);
        var frame = new Frame { Commands = commands, Width = 64, Height = 32 };
        var offscreen = new Offscreen();
        var hostClear = new Color(0.9f, 0.1f, 0.6f, 1f);
        bool captured = false, after = false;
        var destination = new FakeTexture(64, 32, 1, 1, GpuPixelFormat.B8G8R8A8UNorm);
        taps.Add(new Tap(c =>
        {
            Assert.Equal(1, h.Api.AcquireCount);
            Assert.Same(commands, c.Commands);
            Assert.Equal(h.Api.Handed[0].Texture, h.Boundary.Framebuffer.Attachment.Texture);
            // Reaching the destination ownership check proves this recording bound the swapchain.
            Assert.Throws<ArgumentException>(() => commands.CopyFromDrawable(
                h.Boundary.Framebuffer.Attachment.Texture, destination));
            captured = true;
        }, _ =>
        {
            Assert.True(captured);
            Assert.Equal(1, device.Submits);
            Assert.Equal(0, device.Presents);
            after = true;
        }));
        try
        {
            RendererFramePolicy.Run(frame, true, device, commands, hostClear,
                _ => Assert.Equal(0, h.Api.AcquireCount), _ =>
                {
                    Assert.Equal(rendererOwned ? 0 : 1, h.Api.AcquireCount);
                    commands.SetFramebuffer(offscreen);
                    commands.ClearColorTarget(0, Color.Black);
                    Assert.Equal(rendererOwned ? 0 : 1, h.Api.AcquireCount);
                    if (rendererOwned)
                        Assert.Equal(SwapchainCopyCheck.NotBoundMessage, Assert.Throws<InvalidOperationException>(
                            () => commands.CopyFromDrawable(IntPtr.Zero, destination)).Message);
                    commands.SetFramebuffer(h.Boundary.Framebuffer);
                    commands.ClearColorTarget(0, Color.Transparent);
                }, taps, rendererOwned);

            Assert.True(after);
            Assert.Equal(1, device.Submits);
            Assert.Equal(1, device.Presents);
            Assert.Equal(rendererOwned ? 2 : 3, passes.Passes.Count);
            if (!rendererOwned) Assert.Equal(hostClear, passes.Passes[0].Colour[0].ClearValue);
            RecordedRenderPass final = passes.Passes[^1];
            Assert.Equal(h.Api.Handed[0].Texture, final.Colour[0].Texture);
            Assert.Equal(MetalLoadAction.Clear, final.Colour[0].LoadAction);
            Assert.Equal(Color.Transparent, final.Colour[0].ClearValue);
        }
        finally { frame.DisposeSubmission(); }
    }

    [Fact]
    public void An_opted_in_suppressed_frame_never_opens_acquires_submits_or_presents()
    {
        using var h = new MetalDeferredDrawableTests.Harness();
        using var ring = new MetalRingHarness();
        var calls = new FakeMetalEncoderCalls();
        using MetalCommandList commands = ring.NewList(new object(), calls: calls);
        using var device = new MetalDeferredDrawableTests.FrameDevice(h.Boundary);
        using var taps = new FrameCaptureTaps(device, null);
        taps.Add(new Tap(_ => throw new InvalidOperationException("unexpected capture")));
        var frame = new Frame { Commands = commands, RenderSuppressed = true };
        int callbacks = 0;
        try
        {
            RendererFramePolicy.Run(frame, false, device, commands, Color.Black,
                _ => callbacks++, _ => callbacks++, taps, true);
            Assert.Equal(2, callbacks);
            Assert.Empty(calls.Log);
            Assert.Equal(0, h.Api.AcquireCount);
            Assert.Equal(0, device.Submits);
            Assert.Equal(0, device.Presents);
        }
        finally { frame.DisposeSubmission(); }
    }

    sealed class Offscreen : IGpuFramebuffer, IMetalBoundFramebufferSource
    {
        public uint Width => 64;
        public uint Height => 32;
        public GpuOutputDescription Outputs => new(null, GpuPixelFormat.B8G8R8A8UNorm);
        public bool IsSwapchain => false;
        public MetalBoundFramebuffer AsBound => new(ulong.MaxValue, Width, Height,
            [new MetalAttachment(new IntPtr(0x700), GpuPixelFormat.B8G8R8A8UNorm)], default, false);
        public void Dispose() { }
    }

    sealed class Tap(Action<FrameCaptureContext> record, Action<FrameCaptureContext>? after = null) : IFrameCaptureTap
    {
        public void Record(FrameCaptureContext context) => record(context);
        public void AfterSubmit(FrameCaptureContext context) => after?.Invoke(context);
        public void Detach(Action<IDisposable> retire) { }
    }
}
