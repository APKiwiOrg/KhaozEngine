using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.GpuCore;

[Collection("CoreBackend")]
public sealed class CoreCapabilityParityTests
{
    [NativeGpuFact(RequireManagedVulkan = true)]
    public void Capabilities_match_the_managed_vulkan_backend()
    {
        GpuCapabilities expected;
        KhaozEngineVulkan.Register();
        using (var managed = GpuDeviceContext.CreateHeadless(GpuBackendKind.VulkanNative))
            expected = managed.Capabilities;

        CoreBackendRegistration.SeatAsVulkanNative();
        using var core = GpuDeviceContext.CreateHeadless(GpuBackendKind.VulkanNative);
        var actual = core.Capabilities;
        Assert.Equal(expected.ClipSpaceYInverted, actual.ClipSpaceYInverted);
        Assert.Equal(expected.DepthRangeZeroToOne, actual.DepthRangeZeroToOne);
        Assert.Equal(expected.DeviceName, actual.DeviceName);
        Assert.Equal(expected.SamplerAnisotropy, actual.SamplerAnisotropy);
        Assert.Equal(expected.SamplerLodBias, actual.SamplerLodBias);
        Assert.Equal(expected.MaxMsaaSampleCount, actual.MaxMsaaSampleCount);
        Assert.Equal(expected.SupportedMsaaSampleCounts, actual.SupportedMsaaSampleCounts);
        Assert.Equal(expected.SupportsShadowMaps, actual.SupportsShadowMaps);
        Assert.Equal(expected.SupportsCompute, actual.SupportsCompute);
        Assert.Equal(expected.MaxStorageBufferRangeBytes, actual.MaxStorageBufferRangeBytes);
        Assert.Equal(expected.MaxRenderTexture2DDimension, actual.MaxRenderTexture2DDimension);
        Assert.Equal(expected.SupportsTimestamps, actual.SupportsTimestamps);
        Assert.Equal(expected.SupportsShaderFloatControls, actual.SupportsShaderFloatControls);
        Assert.Equal(expected.MaxColorAttachments, actual.MaxColorAttachments);
        Assert.Equal(expected.SupportsCompletionFences, actual.SupportsCompletionFences);
        Assert.Equal(expected.MaxFragmentStorageBuffers, actual.MaxFragmentStorageBuffers);
    }
}
