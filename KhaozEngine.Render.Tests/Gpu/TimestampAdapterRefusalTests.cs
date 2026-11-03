using System;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class TimestampAdapterRefusalTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnsupportedSamplingBoundaryIdentifiesTheRequestedBoundary(bool stage)
    {
        var api = new MetalTimestampApi(new MetalTimestampTestNative { Stage = !stage, Blit = stage });
        var ex = Assert.Throws<MetalTimestampAdapterUnsupportedException>(() => api.CreatePool(2, stage));
        Assert.IsAssignableFrom<NotSupportedException>(ex);
        Assert.Equal(MetalTimestampAdapterRefusal.SamplingBoundaryUnsupported, ex.Reason);
        Assert.Equal(stage, ex.StageBoundary);
        var resources = api.CreatePool(2, !stage);
        api.DestroyPool(resources);
    }

    [Fact]
    public void NilResourceRefusalIdentifiesTheNativeResource()
    {
        var native = new MetalTimestampTestNative();
        var api = new MetalTimestampApi(native);
        native.FailSamples = true;
        var ex = Assert.Throws<MetalTimestampAdapterStateException>(() => api.CreatePool(2, true));
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal(MetalTimestampAdapterRefusal.NativeTimestampResourceUnavailable, ex.Reason);
        Assert.Equal("SampleBuffer", ex.ResourceKind);
        native.FailSamples = false;
        var resources = api.CreatePool(2, true);
        api.DestroyPool(resources);
    }

    [Fact]
    public void FixedNativeAdapterRefusesASecondPool()
    {
        using var native = new VulkanTimestampNativeContext();
        using var vk = new Vk(native);
        var alive = new DeviceLiveness();
        var api = new VulkanTimestampApi(vk, new Device(1), new VulkanDeviceLossLatch(alive), alive);
        ulong pool = api.CreatePool(2);
        var ex = Assert.Throws<VulkanTimestampAdapterException>(() => api.CreatePool(2));
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal(VulkanTimestampAdapterRefusal.PoolAlreadyCreated, ex.Reason);
        api.DestroyPool(pool);
    }
}
