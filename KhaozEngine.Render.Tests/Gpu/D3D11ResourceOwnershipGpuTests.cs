using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class D3D11ResourceOwnershipGpuTests
{
    [GpuTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void FramebufferFactoryRefusesForeignOrMissingAttachments(bool depth, bool missing)
    {
        if (!OperatingSystem.IsWindows()) return;
        using GpuDeviceContext context = GpuDeviceContext.CreateHeadless(GpuBackendKind.Direct3D11Native);
        IGpuResourceFactory factory = context.GpuDevice.Factory;
        GpuPixelFormat format = depth ? GpuPixelFormat.D32FloatS8UInt : GpuPixelFormat.R8G8B8A8UNorm;
        GpuTextureUsage usage = depth ? GpuTextureUsage.DepthStencil : GpuTextureUsage.RenderTarget;
        using var foreign = new FakeTexture(4, 4, 1, 1, format, usage);
        using IGpuTexture own = factory.CreateTexture(new GpuTextureDescription(4, 4, format, usage));

        var refusal = Assert.Throws<GpuResourceOwnershipArgumentException>(() =>
            factory.CreateFramebuffer(depth ? foreign : null, depth ? [] : [missing ? null! : foreign]));
        Assert.Equal(missing ? GpuResourceOwnershipRefusal.MissingResource : GpuResourceOwnershipRefusal.ForeignBackend,
            refusal.Reason);
        Assert.Equal("texture", refusal.ParamName);
        Assert.Equal(GpuBackendKind.Direct3D11Native, refusal.ExpectedBackend);
        Assert.Equal(missing ? null : typeof(FakeTexture), refusal.ActualType);
        Assert.Equal("texture", refusal.ResourceKind);
        Assert.Equal(depth ? "depth attachment" : "colour attachment", refusal.Context);
        using IGpuFramebuffer valid = factory.CreateFramebuffer(depth ? own : null, depth ? [] : [own]);
        Assert.Equal(4u, valid.Width);
        Assert.Equal(4u, valid.Height);
    }
}
