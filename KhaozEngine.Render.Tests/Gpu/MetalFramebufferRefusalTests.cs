using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalFramebufferRefusalTests
{
    [Fact]
    public void AnAttachmentWithoutItsNativeHandleUsesTheSharedMissingViewReason()
    {
        using var harness = new MetalRingHarness();
        var description = new GpuTextureDescription(4, 4, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget);
        using var texture = harness.NewTexture(description);
        using (var valid = new MetalFramebuffer(null, [texture])) Assert.Equal(4u, valid.Width);
        texture.Dispose();

        var refusal = Assert.Throws<GpuFramebufferException>(() => new MetalFramebuffer(null, [texture]));
        Assert.Equal(GpuFramebufferRefusal.AttachmentViewMissing, refusal.Reason);
        Assert.Null(refusal.ParamName);
        Assert.Equal("colour attachment 0", refusal.Attachment);
        Assert.Equal(GpuTextureUsage.RenderTarget, refusal.ActualUsage);
        Assert.Equal(GpuTextureUsage.RenderTarget, refusal.RequiredUsage);
        Assert.False(refusal.IsStaging);
        Assert.Equal("MTLTexture", refusal.NativeHandleKind);
    }
}
