using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
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

    [Theory]
    [InlineData("Model", 15)]
    [InlineData("ModelDissolve", 15)]
    [InlineData("SkinnedModel", 7)]
    [InlineData("SkinnedModelDissolve", 7)]
    [InlineData("Splat", 6)]
    [InlineData("TileGround", 6)]
    public void LitLayoutsAppendWaterWithoutMovingExistingBindings(string programName, int binding)
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer renderer = NewRenderer(device);
        renderer.EnsureSkinnedMainCapacity(1);
        var factory = (FakeGpuResourceFactory)device.Factory;
        ShippedGraphicsProgram program = ShippedShaderPrograms.GraphicsPrograms().Single(p => p.Name == programName);
        var pipeline = factory.GraphicsPipelines.First(p => p.FragmentGlsl == program.FragmentGlsl);
        var layout = Assert.IsType<FakeResourceLayout>(pipeline.Description.ResourceLayouts[0]);
        GpuResourceLayoutElement[] elements = layout.Description.Elements;
        Assert.Equal(binding + 1, elements.Length);
        Assert.Equal("WaterVolumes", elements[binding].Name);
        Assert.Equal(GpuResourceKind.StructuredBufferReadOnly, elements[binding].Kind);
        Assert.Equal(GpuShaderStages.Fragment, elements[binding].Stages);
        var sets = factory.ResourceSets.Where(s => ReferenceEquals(s.Layout, layout)).ToArray();
        Assert.NotEmpty(sets);
        FakeBuffer water = Assert.Single(factory.Buffers, b => b.SizeInBytes == 652800);
        Assert.All(sets, set => Assert.Same(water, set.Resources[binding]));
    }

    [Fact]
    public void EveryLitVariantRetainsItsWaterReaderAfterOptimization()
    {
        ShippedGraphicsProgram[] programs = ShippedShaderPrograms.GraphicsPrograms()
            .Where(p => p.WaterVolumeBinding.HasValue).ToArray();
        Assert.Equal(30, programs.Length);
        foreach (ShippedGraphicsProgram program in programs)
        {
            CrossCompiledPair compiled = SpirvCrossCompile.GlslPairToHlsl(
                program.VertexGlsl, program.FragmentGlsl, program.Name);
            GpuResourceLayoutElement[] elements = compiled.Reflection.ResourceLayouts[0].Elements;
            int binding = program.WaterVolumeBinding!.Value;
            Assert.Equal(binding + 1, elements.Length);
            Assert.Equal(GpuResourceKind.StructuredBufferReadOnly, elements[binding].Kind);
            Assert.Contains(binding == 15 ? "register(t10)" : "register(t3)",
                compiled.FragmentSource, StringComparison.Ordinal);
        }
    }

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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SceneRefusesInsufficientStorageBeforeCreatingAnyGpuResources(int reported)
    {
        using var device = new FakeGpuDevice(capabilityOverride: new GpuCapabilities(false, true)
            { MaxFragmentStorageBuffers = reported });
        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm)));
        Assert.Contains("maxPerStageDescriptorStorageBuffers", error.Message, StringComparison.Ordinal);
        var factory = (FakeGpuResourceFactory)device.Factory;
        Assert.Empty(factory.Buffers);
        Assert.Empty(factory.Textures);
        Assert.Empty(factory.Framebuffers);
        Assert.Empty(factory.ResourceSets);
        Assert.Empty(factory.ShaderRequests);
        Assert.Empty(factory.ShaderSets);
        Assert.Empty(factory.GraphicsPipelines);
        Assert.Empty(factory.Pipelines);
        Assert.Empty(factory.CommandLists);
        Assert.Equal(0, factory.SamplerCreateCount);
        Assert.Equal(0, factory.ResourceLayoutCreateCount);
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
