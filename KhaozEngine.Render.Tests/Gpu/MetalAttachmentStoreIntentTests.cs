using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalAttachmentStoreIntentTests
{
    // #595. Independent colour and combined-format plane intents reach the real deferred schedule.
    [Fact]
    public void OnlyTheAttachmentsDeclaredDeadAreDiscarded()
    {
        using var harness = new MetalRingHarness();
        var render = new FakeMetalRenderCalls();
        using var list = harness.NewList(new(), render: render);
        list.Begin();
        var framebuffer = new MetalBoundFramebuffer(1, 64, 64,
            [new(new IntPtr(1), GpuPixelFormat.R8G8B8A8UNorm), new(new IntPtr(2), GpuPixelFormat.R8G8B8A8UNorm)],
            new(new IntPtr(3), GpuPixelFormat.D32FloatS8UInt), true);
        list.Passes.SetFramebuffer(framebuffer, new(DiscardColourMask: 2, DiscardStencil: true));
        list.Passes.PrepareDraw();
        var pass = Assert.Single(render.Passes);
        Assert.Equal(MetalStoreAction.Store, pass.Colour[0].StoreAction);
        Assert.Equal(MetalStoreAction.DontCare, pass.Colour[1].StoreAction);
        Assert.Equal(MetalStoreAction.Store, pass.Depth.StoreAction);
        Assert.Equal(MetalStoreAction.DontCare, pass.Depth.StencilStoreAction);
        list.Passes.SetFramebuffer(framebuffer with { Id = 2 });
        list.Passes.PrepareDraw();
        Assert.Equal(MetalStoreAction.Store, render.Passes[1].Depth.StoreAction);
        Assert.Equal(MetalStoreAction.Store, render.Passes[1].Depth.StencilStoreAction);
        list.End();
    }
}
