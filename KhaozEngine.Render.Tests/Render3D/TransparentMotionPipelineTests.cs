using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Every transparent pass that draws into the model framebuffer leaves the motion attachment exactly as the
/// opaque surface behind it wrote it (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 4), and against the base target
/// builds exactly what it always did.</summary>
public sealed class TransparentMotionPipelineTests
{
    static void AssertPreserving(FakeGpuResourceFactory factory, string vertex, string fragment, int pipelines)
    {
        FakeGraphicsPipelineRequest[] built = factory.GraphicsPipelines
            .Where(p => p.VertexGlsl == vertex && p.FragmentGlsl == fragment).ToArray();
        Assert.Equal(pipelines, built.Length);
        Assert.All(built, p =>
        {
            GpuBlendAttachment[] blends = p.Description.BlendAttachments;
            Assert.Equal(4, blends.Length);
            for (int i = 1; i < blends.Length; i++)
            {
                Assert.True(blends[i].BlendEnabled);
                Assert.Equal((GpuBlendFactor.Zero, GpuBlendFactor.One), (blends[i].SourceColorFactor, blends[i].DestinationColorFactor));
                Assert.Equal((GpuBlendFactor.Zero, GpuBlendFactor.One), (blends[i].SourceAlphaFactor, blends[i].DestinationAlphaFactor));
            }
        });
    }

    [Fact]
    public void OnATemporalTargetEveryTransparentPassPreservesTheMotionAttachment()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;

        using (new TexturedBillboardRenderer(device, ModelTargets.Temporal))
            AssertPreserving(factory, ShaderSources.BillboardVert, ShaderSources.TexturedBillboardMotionFrag, 2);
        using (new BeamRenderer(device, ModelTargets.Temporal))
            AssertPreserving(factory, ShaderSources.BeamVert, ShaderSources.BeamMotionFrag, 1);
        using (new TrailRenderer(device, ModelTargets.Temporal))
            AssertPreserving(factory, ShaderSources.TrailVert, ShaderSources.TrailMotionFrag, 2);
        using (new OverlayMeshRenderer(device, ModelTargets.Temporal))
            AssertPreserving(factory, ShaderSources.OverlayUnlitVert, ShaderSources.OverlayUnlitMotionFrag, 1);
        using (new SilhouetteRenderer(device, ModelTargets.Temporal))
            AssertPreserving(factory, ShaderSources.SilhouetteVert, ShaderSources.SilhouetteMotionFrag, 1);
    }

    [Fact]
    public void OnTheBaseTargetTheTransparentPassesBuildWhatTheyAlwaysDid()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;

        using (new TexturedBillboardRenderer(device, ModelTargets.Base))
        using (new BeamRenderer(device, ModelTargets.Base))
        using (new TrailRenderer(device, ModelTargets.Base))
        using (new OverlayMeshRenderer(device, ModelTargets.Base))
        using (new SilhouetteRenderer(device, ModelTargets.Base))
        {
            Assert.Equal(7, factory.GraphicsPipelines.Count);
            Assert.All(factory.GraphicsPipelines, p =>
            {
                Assert.Equal(3, p.Description.BlendAttachments.Length);
                Assert.DoesNotContain("oMotion", p.FragmentGlsl, StringComparison.Ordinal);
            });
        }
    }
}
