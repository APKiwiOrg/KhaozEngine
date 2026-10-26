using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterVolumeLayoutTests
{
    static ModelRenderer NewRenderer(FakeGpuDevice device) => new(device,
        new(GpuPixelFormat.D32FloatS8UInt, GpuPixelFormat.R8G8B8A8UNorm,
            GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float), 128, 1);

    [Fact]
    public void BufferSizeAndStrideMatchThePackedClusterImage()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer renderer = NewRenderer(device);
        FakeBuffer buffer = Assert.Single(((FakeGpuResourceFactory)device.Factory).Buffers,
            b => b.SizeInBytes == 652800);
        Assert.Equal(GpuBufferUsage.StructuredBufferReadOnly, buffer.Usage);
        Assert.Equal(16u, buffer.StructureByteStride);
        Assert.Equal((uint)(WaterVolumeClusterBuilder.ImageUIntCount * sizeof(uint)), buffer.SizeInBytes);
        Assert.Equal(3456, WaterVolumeClusterBuilder.HeaderRegionUInts);
        Assert.Equal(114048, WaterVolumeClusterBuilder.RecordRegionUInts);
        Assert.Equal(12, WaterVolumeClusterBuilder.RecordStrideUInts);
        Assert.Equal(163200, WaterVolumeClusterBuilder.ImageUIntCount);
    }

    [Fact]
    public void WaterCountUsesTheExistingClusterCameraLaneWithoutGrowingTheFrameBlock()
    {
        Assert.Equal(1312u, ModelRenderer.ClusterTailOffset);
        Assert.Equal(32u, ModelRenderer.ClusterTailBytes);
        foreach (string source in new[] { ShaderSources.ModelFrag, ShaderSources.SkinnedModelFrag,
            ShaderSources.SplatFrag, ShaderSources.TileGroundFrag, ShaderSources.FoliageVert, ShaderSources.ModelMotionVert })
            Assert.Contains("vec4 ClusterCamera;", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FewerStorageBuffersThanNeededRefusesTheRenderer(int reported)
    {
        using var device = new FakeGpuDevice(capabilityOverride: new GpuCapabilities(false, true)
            { MaxFragmentStorageBuffers = reported });
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => NewRenderer(device));
        Assert.Contains("maxPerStageDescriptorStorageBuffers", error.Message, StringComparison.Ordinal);
        Assert.Empty(((FakeGpuResourceFactory)device.Factory).ShaderRequests);
        Assert.Empty(((FakeGpuResourceFactory)device.Factory).Buffers);
    }

    [Fact]
    public void ExactlyThreeFragmentStorageBuffersCanCreateTheRenderer()
    {
        using var device = new FakeGpuDevice(capabilityOverride: new GpuCapabilities(false, true)
            { MaxFragmentStorageBuffers = 3 });
        using ModelRenderer renderer = NewRenderer(device);
    }

    [Fact]
    public void NativeBackendsReportTheirStorageLimitsAndLegacyCapabilitiesKeepTheVulkanFloor()
    {
        Assert.Equal(4, default(GpuCapabilities).MaxFragmentStorageBuffers);
        Assert.Equal(4, new GpuCapabilities(false, true).MaxFragmentStorageBuffers);
        Assert.Equal(128, KhaozEngine.Gpu.D3D11.Internal.D3D11CapabilityRead.Assemble("device", 1,
            supportsShadowMaps: true, supportsCompletionFences: true).MaxFragmentStorageBuffers);
        Assert.Equal(31, KhaozEngine.Gpu.Metal.Internal.MetalCapabilityRead.Assemble("device", 1)
            .MaxFragmentStorageBuffers);
    }
}
