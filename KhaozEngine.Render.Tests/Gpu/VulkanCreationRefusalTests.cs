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
        using var device = new FakeGpuDevice(GpuBackendKind.VulkanNative);
        var provider = new VulkanBackendProvider(environment: _ => null, headless: _ => new(device, null, null));
        var refusal = Assert.Throws<GpuBackendCreationArgumentException>(() => provider.CreateHeadless(settings));
        Assert.Equal(GpuBackendCreationRefusal.ForeignDiagnosticSettings, refusal.Reason);
        Assert.Equal(settings.GetType(), refusal.ActualType);
        Assert.Equal("settings", refusal.ParamName);
        Assert.Equal(GpuBackendKind.VulkanNative, refusal.ExpectedBackend);
        provider.CreateHeadless(provider.ResolveHeadlessSettings(new(false, false)));
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MachineAdmissionIsDeviceFreeAndCarriesTheDriverRemedy(bool noDriver)
    {
        string missing = noDriver ? VulkanSupportProbe.NoDriverInstalled : VulkanSupportProbe.NoLoaderResolved;
        var refusal = Assert.Throws<VulkanBackendCreationUnsupportedException>(() => VulkanBackendProvider.RequireMachineSupportForTests(missing));
        Assert.Equal(VulkanBackendCreationRefusal.MachineUnsupported, refusal.Reason);
        Assert.Equal(missing, refusal.MissingRequirement);
        Assert.Equal("GpuBackendSelector.IsBackendSupported", refusal.Probe);
        Assert.Equal(noDriver ? "mesa-vulkan-drivers" : null, refusal.DriverPackage);
        Assert.IsAssignableFrom<NotSupportedException>(refusal);
        VulkanBackendProvider.RequireMachineSupportForTests(null);
    }
}
