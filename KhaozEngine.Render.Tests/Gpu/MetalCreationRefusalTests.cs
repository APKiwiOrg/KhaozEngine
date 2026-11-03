using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalCreationRefusalTests
{
    [Theory]
    [InlineData(GpuWindowKind.Win32, false, MetalBackendCreationRefusal.WindowKindUnsupported)]
    [InlineData(GpuWindowKind.Cocoa, true, MetalBackendCreationRefusal.WindowHandleMissing)]
    public void WindowAdmissionDistinguishesKindAndHandle(GpuWindowKind kind, bool nil, MetalBackendCreationRefusal reason)
    {
        var refusal = Assert.Throws<MetalBackendCreationArgumentException>(() =>
            MetalLayerHost.RequireWindowForTests(kind, nil ? IntPtr.Zero : new IntPtr(1)));
        Assert.Equal(reason, refusal.Reason);
        Assert.IsAssignableFrom<ArgumentException>(refusal);
        if (reason == MetalBackendCreationRefusal.WindowKindUnsupported)
        {
            Assert.Equal(kind, refusal.ActualWindowKind);
            Assert.Equal(GpuWindowKind.Cocoa, refusal.RequiredWindowKind);
            Assert.Equal("kind", refusal.ParamName);
        }
        else
        {
            Assert.Equal(GpuWindowKind.Cocoa, refusal.WindowKind);
            Assert.Equal("window", refusal.ParamName);
        }
        MetalLayerHost.RequireWindowForTests(GpuWindowKind.Cocoa, new IntPtr(1));
    }

    [Fact]
    public void NativeSurfaceFaultsCarryTheirContext()
    {
        var view = Assert.Throws<MetalBackendCreationArgumentException>(() => MetalLayerHost.RequireContentViewForTests(false));
        Assert.Equal(MetalBackendCreationRefusal.ContentViewMissing, view.Reason);
        Assert.Equal(GpuWindowKind.Cocoa, view.WindowKind);
        Assert.Equal("window", view.ParamName);
        MetalLayerHost.RequireContentViewForTests(true);
        var layer = Assert.Throws<MetalBackendCreationStateException>(() => MetalLayerHost.RequireNativeLayerForTests(false, "class absent"));
        Assert.Equal(MetalBackendCreationRefusal.NativeLayerUnavailable, layer.Reason);
        Assert.Equal("class absent", layer.Detail);
        Assert.Equal("CAMetalLayer", layer.NativeClass);
        Assert.IsAssignableFrom<InvalidOperationException>(layer);
        MetalLayerHost.RequireNativeLayerForTests(true, "");
    }

    [Fact]
    public void ProviderFaultsCarrySettingsMachineAndPlatform()
    {
        var foreign = new D3D11BackendProvider(environment: _ => null).ResolveHeadlessSettings(new(false, false));
        var settings = Assert.Throws<MetalBackendCreationArgumentException>(() => new MetalBackendProvider().CreateHeadless(foreign));
        Assert.Equal(MetalBackendCreationRefusal.ForeignDiagnosticSettings, settings.Reason);
        Assert.Equal(foreign.GetType(), settings.ActualType);
        Assert.Equal("settings", settings.ParamName);
        var machine = Assert.Throws<MetalBackendCreationUnsupportedException>(() => MetalBackendProvider.RequireMachineSupportForTests("family below floor"));
        MetalBackendProvider.RequireMachineSupportForTests(null);
        Assert.Equal(MetalBackendCreationRefusal.MachineUnsupported, machine.Reason);
        Assert.Equal("family below floor", machine.MissingRequirement);
        Assert.Equal(GpuBackendKind.MetalNative, machine.Backend);
        Assert.IsAssignableFrom<NotSupportedException>(machine);
        var platform = MetalBackendProvider.PlatformRefusalForTests("headless");
        Assert.Equal(MetalBackendCreationRefusal.OperatingSystemUnsupported, platform.Reason);
        Assert.Equal("headless", platform.CreationPath);
        Assert.Equal("macOS", platform.RequiredOperatingSystem);
        Assert.IsAssignableFrom<PlatformNotSupportedException>(platform);
        var resource = MetalResourceFactory.PlatformRefusalForTests("A GPU buffer");
        Assert.Equal(MetalBackendCreationRefusal.NativeResourceUnavailableOnPlatform, resource.Reason);
        Assert.Equal("A GPU buffer", resource.ResourceKind);
        Assert.Equal("macOS", resource.RequiredOperatingSystem);
    }
}
