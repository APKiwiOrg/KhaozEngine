using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
    static readonly GpuOutputDescription Outputs = new(GpuPixelFormat.D32FloatS8UInt, GpuPixelFormat.R8G8B8A8UNorm,
        GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float);

    static ModelRenderer NewRenderer(FakeGpuDevice device) => new(device, Outputs, 128, 1);

    static FakeResourceSet[] LiveLitSets(FakeGpuResourceFactory factory) => factory.ResourceSets
        .Where(set => !set.Disposed && set.Layout is FakeResourceLayout layout
            && layout.Description.Elements.Any(element => element.Name == "CausticMap")).ToArray();

    static void AssertBindsCausticMap(FakeResourceSet set, IGpuTexture map)
    {
        GpuResourceLayoutElement[] elements = Assert.IsType<FakeResourceLayout>(set.Layout).Description.Elements;
        int binding = Array.FindIndex(elements, element => element.Name == "CausticMap");
        Assert.Same(map, set.Resources[binding]);
        Assert.IsType<FakeSampler>(set.Resources[binding + 1]);
    }

    static FakeResourceSet DrawOpaqueFoliage(ModelRenderer renderer, FakeGpuResourceFactory factory)
    {
        using var cl = new RecordingGpuCommandList(new NullGpuCommandList()) { CaptureBindings = true };
        renderer.UploadFoliageUniforms(cl, new ModelRenderer.FoliageUniforms[1]);
        IGpuBuffer vertices = factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.VertexBuffer));
        IGpuBuffer indices = factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.IndexBuffer));
        renderer.DrawFoliageMesh(cl, vertices, indices, 3, GpuIndexFormat.UInt16, vertices, 0, 1, 0,
            materialSet: null, opaqueMaterial: true);
        return Assert.IsType<FakeResourceSet>(Assert.Single(cl.Bindings).Sets[0].Set);
    }

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
        Assert.Equal(binding + 3, elements.Length);
        Assert.Equal("WaterVolumes", elements[binding].Name);
        Assert.Equal(GpuResourceKind.StructuredBufferReadOnly, elements[binding].Kind);
        Assert.Equal(GpuShaderStages.Fragment, elements[binding].Stages);
        Assert.Equal("CausticMap", elements[binding + 1].Name);
        Assert.Equal(GpuResourceKind.TextureReadOnly, elements[binding + 1].Kind);
        Assert.Equal(GpuShaderStages.Fragment, elements[binding + 1].Stages);
        Assert.Equal("CausticSampler", elements[binding + 2].Name);
        Assert.Equal(GpuResourceKind.Sampler, elements[binding + 2].Kind);
        Assert.Equal(GpuShaderStages.Fragment, elements[binding + 2].Stages);
        var sets = factory.ResourceSets.Where(s => ReferenceEquals(s.Layout, layout)).ToArray();
        Assert.NotEmpty(sets);
        FakeBuffer water = Assert.Single(factory.Buffers, b => b.SizeInBytes == 652816);
        Assert.All(sets, set =>
        {
            Assert.Same(water, set.Resources[binding]);
            AssertBindsCausticMap(set, renderer.CausticMap);
        });
    }

    [Fact]
    public void EveryLitVariantRetainsItsWaterReaderAfterOptimization()
    {
        ShippedGraphicsProgram[] programs = ShippedShaderPrograms.GraphicsPrograms()
            .Where(p => p.WaterVolumeBinding.HasValue).ToArray();
        Assert.Equal(34, programs.Length);
        foreach (ShippedGraphicsProgram program in programs)
        {
            CrossCompiledPair compiled = SpirvCrossCompile.GlslPairToHlsl(
                program.VertexGlsl, program.FragmentGlsl, program.Name);
            GpuResourceLayoutElement[] elements = compiled.Reflection.ResourceLayouts[0].Elements;
            int binding = program.WaterVolumeBinding!.Value;
            Assert.Equal(binding + 3, elements.Length);
            Assert.Equal(GpuResourceKind.StructuredBufferReadOnly, elements[binding].Kind);
            Assert.Equal(GpuResourceKind.TextureReadOnly, elements[binding + 1].Kind);
            Assert.Equal(GpuResourceKind.Sampler, elements[binding + 2].Kind);
            (string water, string map, string sampler) = binding switch
            {
                15 => ("t10", "t11", "s3"),
                10 => ("t6", "t7", "s2"),
                _ => ("t3", "t4", "s1"),
            };
            Assert.Contains($"register({water})", compiled.FragmentSource, StringComparison.Ordinal);
            // The optimizer strips resource names, so the type and register identify each one.
            Assert.Matches($@"Texture2D<float4> \S+ : register\({map}\);", compiled.FragmentSource);
            Assert.Matches($@"SamplerState \S+ : register\({sampler}\);", compiled.FragmentSource);
        }
    }

    [Fact]
    public void Caustic_placeholder_is_one_owned_texel_of_one()
    {
        using var device = new FakeGpuDevice();
        var uploads = new Dictionary<IGpuTexture, byte[]>();
        device.OnTextureUpload = (texture, data) => uploads[texture] = data;
        ModelRenderer renderer = NewRenderer(device);
        FakeTexture placeholder = Assert.IsType<FakeTexture>(renderer.CausticMap);
        Assert.Equal((1u, 1u, GpuPixelFormat.R16G16B16A16Float),
            (placeholder.Width, placeholder.Height, placeholder.Format));
        Assert.Equal(new[] { (Half)1f, (Half)1f, (Half)1f, (Half)1f },
            MemoryMarshal.Cast<byte, Half>(uploads[placeholder]).ToArray());
        renderer.Dispose();
        Assert.True(placeholder.Disposed);
    }

    [Fact]
    public void Caustic_map_replacement_rebinds_every_lit_set()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var retired = GpuRetireQueue.CreateFrameCounted(device, GpuRetireQueue.DefaultFrameDelay);
        using var renderer = new ModelRenderer(device, Outputs, 128, 1, retired);
        IGpuResourceSet rigid = renderer.CreateMaterialSet();
        IGpuResourceSet skinned = renderer.CreateSkinnedMaterialSet();
        renderer.EnsureSkinnedMainCapacity(1);
        FakeResourceSet foliageOpaque = DrawOpaqueFoliage(renderer, factory);

        IGpuTexture placeholder = renderer.CausticMap;
        FakeResourceSet[] before = LiveLitSets(factory);
        // Default, rigid material, splat frame, tile-ground frame, skinned main and the foliage opaque subset.
        Assert.Equal(6, before.Length);
        Assert.All(before, set => AssertBindsCausticMap(set, placeholder));

        IGpuResourceSet rigidBefore = rigid, skinnedBefore = skinned;
        IGpuTexture map = factory.CreateTexture(GpuTextureDescription.Texture2D(1024, 1024,
            GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        void Commit(Func<IGpuResourceSet, IGpuResourceSet> replace)
        {
            rigid = replace(rigid);
            skinned = replace(skinned);
        }
        bool drained = false;
        device.OnWaitForIdle = () =>
        {
            drained = true;
            Assert.All(before, set => Assert.False(set.Disposed));
        };
        renderer.SetCausticMap(map, [rigid, skinned], Commit);
        device.OnWaitForIdle = null;

        Assert.True(drained);
        Assert.Same(map, renderer.CausticMap);
        Assert.False(Assert.IsType<FakeTexture>(placeholder).Disposed);
        Assert.NotSame(rigidBefore, rigid);
        Assert.True(Assert.IsType<FakeResourceSet>(rigidBefore).Disposed);
        // Set 1 of the skinned pipeline declares no caustic binding, so it has nothing to rebuild.
        Assert.Same(skinnedBefore, skinned);
        Assert.False(Assert.IsType<FakeResourceSet>(skinnedBefore).Disposed);
        AssertBindsCausticMap(Assert.IsType<FakeResourceSet>(rigid), map);

        // The foliage opaque subset follows the default set's generation on its next draw.
        FakeResourceSet rebuiltFoliage = DrawOpaqueFoliage(renderer, factory);
        Assert.NotSame(foliageOpaque, rebuiltFoliage);
        AssertBindsCausticMap(rebuiltFoliage, map);

        // Sets created after the replacement, and the transactions that rebuild them, keep the live map.
        IGpuResourceSet created = renderer.CreateMaterialSet();
        AssertBindsCausticMap(Assert.IsType<FakeResourceSet>(created), map);
        renderer.EnsureSkinnedMainCapacity(9);
        void CommitWithCreated(Func<IGpuResourceSet, IGpuResourceSet> replace)
        {
            Commit(replace);
            created = replace(created);
        }
        renderer.EnsurePointLightCapacity(17, [rigid, skinned, created], CommitWithCreated);
        var settings = new ShadowSettings();
        settings.CommitAtlas();
        Assert.True(renderer.ReplaceShadowLayout(256, 2, settings, [rigid, skinned, created], CommitWithCreated, () => { }));
        AssertBindsCausticMap(DrawOpaqueFoliage(renderer, factory), map);
        for (int i = 0; i < GpuRetireQueue.DefaultFrameDelay; i++) retired.BeginFrame();
        FakeResourceSet[] after = LiveLitSets(factory);
        Assert.Equal(7, after.Length);
        Assert.All(after, set => AssertBindsCausticMap(set, map));

        int sets = factory.ResourceSets.Count;
        renderer.SetCausticMap(map, [rigid, skinned, created], CommitWithCreated);
        Assert.Equal(sets, factory.ResourceSets.Count);
    }

    // Default, rigid material, splat frame, tile-ground frame, then skinned main, in build order.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Failed_caustic_map_replacement_keeps_every_current_set_and_can_retry(int failingSet)
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using ModelRenderer renderer = NewRenderer(device);
        IGpuResourceSet material = renderer.CreateMaterialSet();
        renderer.EnsureSkinnedMainCapacity(1);
        IGpuTexture placeholder = renderer.CausticMap;
        IGpuResourceSet originalMaterial = material;
        FakeResourceSet[] live = LiveLitSets(factory);
        IGpuTexture map = factory.CreateTexture(GpuTextureDescription.Texture2D(1024, 1024,
            GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        int created = factory.ResourceSets.Count;
        factory.ThrowOnResourceSetCreate = created + failingSet;

        Assert.Throws<InvalidOperationException>(() =>
            renderer.SetCausticMap(map, [material], replace => material = replace(material)));

        Assert.Same(placeholder, renderer.CausticMap);
        Assert.Same(originalMaterial, material);
        Assert.All(live, set =>
        {
            Assert.False(set.Disposed);
            AssertBindsCausticMap(set, placeholder);
        });
        Assert.Equal(failingSet - 1, factory.ResourceSets.Count - created);
        Assert.All(factory.ResourceSets.Skip(created), set => Assert.True(set.Disposed));

        factory.ThrowOnResourceSetCreate = 0;
        renderer.SetCausticMap(map, [material], replace => material = replace(material));
        Assert.Same(map, renderer.CausticMap);
        Assert.Equal(5, LiveLitSets(factory).Length);
        Assert.All(LiveLitSets(factory), set => AssertBindsCausticMap(set, map));
    }

    [Fact]
    public void BufferSizeAndStrideMatchThePackedClusterImage()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer renderer = NewRenderer(device);
        FakeBuffer buffer = Assert.Single(((FakeGpuResourceFactory)device.Factory).Buffers,
            b => b.SizeInBytes == 652816);
        Assert.Equal(GpuBufferUsage.StructuredBufferReadOnly, buffer.Usage);
        Assert.Equal(16u, buffer.StructureByteStride);
        Assert.Equal((uint)(WaterVolumeClusterBuilder.ImageUIntCount * sizeof(uint)), buffer.SizeInBytes);
        Assert.Equal(3456, WaterVolumeClusterBuilder.HeaderRegionUInts);
        Assert.Equal(114048, WaterVolumeClusterBuilder.RecordRegionUInts);
        Assert.Equal(12, WaterVolumeClusterBuilder.RecordStrideUInts);
        Assert.Equal(163200, WaterVolumeClusterBuilder.WindowUIntOffset);
        Assert.Equal(40800, WaterVolumeClusterBuilder.WindowVectorIndex);
        Assert.Equal(163204, WaterVolumeClusterBuilder.ImageUIntCount);
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
        LitReceiverNotSupportedException error = Assert.Throws<LitReceiverNotSupportedException>(() => NewRenderer(device));
        Assert.IsAssignableFrom<NotSupportedException>(error);
        Assert.Equal(LitReceiverRefusal.FragmentStorageBufferLimitInsufficient, error.Reason);
        Assert.Equal(3, error.RequiredCount);
        Assert.Equal(reported, error.AvailableCount);
        Assert.Equal("maxPerStageDescriptorStorageBuffers", error.CapabilityName);
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
        LitReceiverNotSupportedException error = Assert.Throws<LitReceiverNotSupportedException>(() =>
            new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm)));
        Assert.IsAssignableFrom<NotSupportedException>(error);
        Assert.Equal(LitReceiverRefusal.FragmentStorageBufferLimitInsufficient, error.Reason);
        Assert.Equal(3, error.RequiredCount);
        Assert.Equal(reported, error.AvailableCount);
        Assert.Equal("maxPerStageDescriptorStorageBuffers", error.CapabilityName);
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
