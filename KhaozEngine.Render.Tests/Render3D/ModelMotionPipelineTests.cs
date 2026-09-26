using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>What a model renderer builds against the base and the temporal model target. Later tasks add a fact per
/// converted path.</summary>
public sealed class ModelMotionPipelineTests
{
    static FakeGraphicsPipelineRequest[] Temporal(FakeGpuResourceFactory factory) =>
        factory.GraphicsPipelines.Where(p => p.Description.Outputs.Colour.Length == 4).ToArray();

    [Fact]
    public void TheBaseTargetBuildsNoVariantAndNoMotionResources()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var model = new ModelRenderer(device, ModelTargets.Base, 64, 1);

        Assert.Null(model.Motion);
        Assert.Empty(Temporal(factory));
        Assert.DoesNotContain(factory.GraphicsPipelines, p => p.FragmentGlsl.Contains("oMotion", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTemporalTargetBuildsTheRigidVariantAndSizesEveryBlendArray()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var model = new ModelRenderer(device, ModelTargets.Temporal, 64, 1);

        Assert.NotNull(model.Motion);
        FakeGraphicsPipelineRequest rigid = Assert.Single(factory.GraphicsPipelines,
            p => p.VertexGlsl == ShaderSources.ModelMotionVert);
        Assert.Equal(ShaderSources.ModelMotionFrag, rigid.FragmentGlsl);
        Assert.Equal(2, rigid.Description.ResourceLayouts.Length);
        Assert.Equal(3, rigid.Description.VertexLayouts.Count);
        Assert.Equal(1u, rigid.Description.VertexLayouts[2].InstanceStepRate);
        Assert.Equal(4u, rigid.Description.VertexLayouts[2].Stride);
        Assert.All(Temporal(factory), p =>
        {
            Assert.Equal(4, p.Description.BlendAttachments.Length);
            Assert.All(p.Description.BlendAttachments, b => Assert.False(b.BlendEnabled));
        });
    }

    [Fact]
    public void TheRigidVariantKeepsTheBasePipelinesFixedState()
    {
        using var baseDevice = new FakeGpuDevice();
        using var baseModel = new ModelRenderer(baseDevice, ModelTargets.Base, 64, 1);
        var baseFactory = (FakeGpuResourceFactory)baseDevice.Factory;
        GpuPipelineDescription basePipeline = Assert.Single(baseFactory.GraphicsPipelines,
            p => p.VertexGlsl == ShaderSources.ModelVert && p.FragmentGlsl == ShaderSources.ModelFrag).Description;
        using var device = new FakeGpuDevice();
        using var model = new ModelRenderer(device, ModelTargets.Temporal, 64, 1);
        GpuPipelineDescription rigid = Assert.Single(((FakeGpuResourceFactory)device.Factory).GraphicsPipelines,
            p => p.VertexGlsl == ShaderSources.ModelMotionVert).Description;

        Assert.Equal(basePipeline.DepthStencil, rigid.DepthStencil);
        Assert.Equal(basePipeline.Rasterizer, rigid.Rasterizer);
        Assert.Equal(basePipeline.Topology, rigid.Topology);
        Assert.Equal(basePipeline.BlendFactor, rigid.BlendFactor);
        Assert.Equal(Shape(basePipeline.VertexLayouts.Take(2)), Shape(rigid.VertexLayouts.Take(2)));   // mesh, instance
    }

    // A vertex layout by value: its stride, step rate and every element's name and format.
    static string[] Shape(IEnumerable<GpuVertexLayoutDescription> layouts) => layouts
        .Select(l => $"{l.Stride}/{l.InstanceStepRate}: "
            + string.Join(", ", l.Elements.Select(e => $"{e.Name} {e.Format}")))
        .ToArray();

    [Fact]
    public void TheTemporalTargetBuildsBothGpuSkinnedVariantsOnFourSets()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var model = new ModelRenderer(device, ModelTargets.Temporal, 64, 1);

        FakeGraphicsPipelineRequest[] skinned = factory.GraphicsPipelines
            .Where(p => p.VertexGlsl == ShaderSources.SkinnedModelMotionVert).ToArray();
        Assert.Equal(new[] { ShaderSources.SkinnedModelMotionFrag, ShaderSources.SkinnedModelDissolveMotionFrag },
            skinned.Select(p => p.FragmentGlsl).ToArray());
        Assert.All(skinned, p =>
        {
            Assert.Equal(4, p.Description.ResourceLayouts.Length);
            Assert.Same(model.Motion!.SkinnedPalette.Layout, p.Description.ResourceLayouts[3]);
        });
    }

    [Fact]
    public void TheTemporalTargetBuildsBothCpuSkinnedVariantsOverThePreviousPositionStream()
    {
        using var baseDevice = new FakeGpuDevice();
        using var baseModel = new ModelRenderer(baseDevice, ModelTargets.Base, 64, 1);
        GpuPipelineDescription basePipeline = Assert.Single(((FakeGpuResourceFactory)baseDevice.Factory).GraphicsPipelines,
            p => p.VertexGlsl == ShaderSources.ModelVert && p.FragmentGlsl == ShaderSources.ModelFrag).Description;
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var model = new ModelRenderer(device, ModelTargets.Temporal, 64, 1);

        FakeGraphicsPipelineRequest[] cpu = factory.GraphicsPipelines
            .Where(p => p.VertexGlsl == ShaderSources.ModelCpuSkinnedMotionVert).ToArray();
        Assert.Equal(new[] { ShaderSources.ModelDissolveMotionFrag, ShaderSources.ModelMotionFrag },
            cpu.Select(p => p.FragmentGlsl).ToArray());
        Assert.All(cpu, p =>
        {
            Assert.Equal(2, p.Description.ResourceLayouts.Length);
            Assert.Same(model.Motion!.FrameLayout, p.Description.ResourceLayouts[1]);
            Assert.Equal(3, p.Description.VertexLayouts.Count);
            Assert.Equal(Shape(basePipeline.VertexLayouts.Take(2)), Shape(p.Description.VertexLayouts.Take(2)));
            Assert.Equal(0u, p.Description.VertexLayouts[2].InstanceStepRate);   // per vertex, not per instance
            Assert.Equal(12u, p.Description.VertexLayouts[2].Stride);
        });
    }

    [Fact]
    public void TheTemporalTargetBuildsTheFoliageVariantWithTheMotionBlockAtSetTwo()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var model = new ModelRenderer(device, ModelTargets.Temporal, 64, 1);
        using IGpuCommandList commands = factory.CreateCommandList();
        model.UploadFoliageUniforms(commands, [default]);   // the foliage pipeline builds on its first upload

        FakeGraphicsPipelineRequest foliage = Assert.Single(factory.GraphicsPipelines,
            p => p.VertexGlsl == ShaderSources.FoliageMotionVert);
        Assert.Equal(ShaderSources.ModelMotionFrag, foliage.FragmentGlsl);
        Assert.Equal(3, foliage.Description.ResourceLayouts.Length);
        Assert.Same(model.Motion!.FrameLayout, foliage.Description.ResourceLayouts[2]);
        Assert.DoesNotContain(factory.GraphicsPipelines, p => p.VertexGlsl == ShaderSources.FoliageVert);
    }

    [Fact]
    public void TheTemporalTargetBuildsTheGroundVariantsOnThreeSets()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var model = new ModelRenderer(device, ModelTargets.Temporal, 64, 1);

        FakeGraphicsPipelineRequest splat = Assert.Single(factory.GraphicsPipelines,
            p => p.VertexGlsl == ShaderSources.SplatMotionVert);
        FakeGraphicsPipelineRequest ground = Assert.Single(factory.GraphicsPipelines,
            p => p.VertexGlsl == ShaderSources.TileGroundMotionVert);
        Assert.Equal(ShaderSources.SplatMotionFrag, splat.FragmentGlsl);
        Assert.Equal(ShaderSources.TileGroundMotionFrag, ground.FragmentGlsl);
        Assert.Equal(3, splat.Description.ResourceLayouts.Length);
        Assert.Equal(3, ground.Description.ResourceLayouts.Length);
        Assert.Same(model.Motion!.FrameLayout, splat.Description.ResourceLayouts[2]);
        Assert.Same(model.Motion!.FrameLayout, ground.Description.ResourceLayouts[2]);
        Assert.DoesNotContain(factory.GraphicsPipelines, p => p.VertexGlsl == ShaderSources.SplatVert);
        Assert.DoesNotContain(factory.GraphicsPipelines, p => p.VertexGlsl == ShaderSources.TileGroundVert);
    }

    [Fact]
    public void EveryPipelineATemporalRendererBuildsBindsAtMostVulkansFourGuaranteedSets()
    {
        // maxBoundDescriptorSets is only guaranteed to be 4, so a fifth set would fail pipeline creation on a
        // minimum-spec Vulkan device. The GPU-skinned variant already sits at the ceiling with the motion block at set 3.
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var model = new ModelRenderer(device, ModelTargets.Temporal, 64, 1);
        using IGpuCommandList commands = factory.CreateCommandList();
        model.UploadFoliageUniforms(commands, [default]);   // the foliage pipeline builds on its first upload

        Assert.NotEmpty(Temporal(factory));
        Assert.All(factory.GraphicsPipelines, p => Assert.InRange(p.Description.ResourceLayouts.Length, 1, 4));
    }

    [Fact]
    public void LeavingTheTemporalTargetRetiresTheMotionResources()
    {
        using var device = new FakeGpuDevice();
        using var model = new ModelRenderer(device, ModelTargets.Temporal, 64, 1);
        Assert.NotNull(model.Motion);

        model.SetOutputs(ModelTargets.Base);
        Assert.Null(model.Motion);

        model.SetOutputs(ModelTargets.Temporal);
        Assert.NotNull(model.Motion);
    }
}
