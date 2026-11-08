using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class D3D11FramebufferAttachmentTests
{
    [Theory]
    [InlineData(GpuTextureUsage.Sampled, GpuFramebufferRefusal.AttachmentViewMissing)]
    [InlineData(GpuTextureUsage.Staging, GpuFramebufferRefusal.AttachmentNotRenderable)]
    public void MissingNativeViewCarriesTheAttachmentRule(GpuTextureUsage usage, GpuFramebufferRefusal reason)
    {
        var refusal = Assert.Throws<GpuFramebufferException>(() =>
            D3D11FramebufferAttachmentValidation.RequireView<object>(null, "colour attachment 1",
                GpuTextureUsage.RenderTarget, usage));
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal("colour attachment 1", refusal.Attachment);
        Assert.Equal(usage, refusal.ActualUsage);
        Assert.Equal(GpuTextureUsage.RenderTarget, refusal.RequiredUsage);
        Assert.Equal(usage == GpuTextureUsage.Staging, refusal.IsStaging);
        Assert.Equal("ID3D11RenderTargetView", refusal.NativeHandleKind);
        var view = new object();
        Assert.Same(view, D3D11FramebufferAttachmentValidation.RequireView(view, "colour attachment 1",
            GpuTextureUsage.RenderTarget, GpuTextureUsage.RenderTarget));
    }
}
