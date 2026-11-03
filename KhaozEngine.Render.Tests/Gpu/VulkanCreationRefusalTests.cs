using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Vulkan;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class VulkanCreationRefusalTests
{
    [Fact]
    public void ForeignSettingsAndUnknownSurfacesRefuseBeforeNativeCreation()
    {
        var settings = new D3D11BackendProvider(environment: _ => null).ResolveHeadlessSettings(new(false, false));
        var refusal = Assert.Throws<VulkanBackendCreationArgumentException>(() => new VulkanBackendProvider().CreateHeadless(settings));
        Assert.Equal(VulkanBackendCreationRefusal.ForeignDiagnosticSettings, refusal.Reason);
        Assert.Equal(settings.GetType(), refusal.ActualType);
        Assert.Equal("settings", refusal.ParamName);
        Assert.IsAssignableFrom<ArgumentException>(refusal);
        var surface = Assert.Throws<VulkanBackendCreationUnsupportedException>(() => VulkanInstanceLayout.SurfaceExtensionFor((GpuWindowKind)999));
        Assert.Equal(VulkanBackendCreationRefusal.WindowKindUnsupported, surface.Reason);
        Assert.Equal((GpuWindowKind)999, surface.WindowKind);
        Assert.NotEmpty(VulkanInstanceLayout.SurfaceExtensionFor(GpuWindowKind.X11));
    }
    [Fact]
    public void LoaderFailureRetainsItsNativeCauseAndProbe()
    {
        var cause = new DllNotFoundException("loader missing");
        var refusal = Assert.Throws<VulkanBackendCreationUnsupportedException>(() => VulkanInstance.RefuseLoaderFailureForTests(cause));
        Assert.Equal(VulkanBackendCreationRefusal.LoaderUnavailable, refusal.Reason);
        Assert.Equal(typeof(DllNotFoundException), refusal.LoaderExceptionType);
        Assert.Equal("GpuBackendSelector.IsBackendSupported", refusal.Probe);
        Assert.Same(cause, refusal.InnerException);
    }
}
