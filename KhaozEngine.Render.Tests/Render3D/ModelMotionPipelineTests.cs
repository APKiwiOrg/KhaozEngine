using System;
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
