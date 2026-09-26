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
    // Each pass's pipelines built from `vertex` and `fragment`, in build order: attachment 0 takes that pipeline's
    // colour blend, and every later attachment keeps its destination whole, the motion one included.
    static void AssertBlends(FakeGpuResourceFactory factory, string vertex, string fragment, int attachments,
        params GpuBlendAttachment[] colours)
    {
        FakeGraphicsPipelineRequest[] built = factory.GraphicsPipelines
            .Where(p => p.VertexGlsl == vertex && p.FragmentGlsl == fragment).ToArray();
        Assert.Equal(colours.Length, built.Length);
        for (int p = 0; p < built.Length; p++)
        {
            GpuBlendAttachment[] blends = built[p].Description.BlendAttachments;
            Assert.Equal(attachments, blends.Length);
            Assert.Equal(colours[p], blends[0]);
            for (int i = 1; i < blends.Length; i++) Assert.Equal(GpuBlendAttachment.PreserveDestination, blends[i]);
        }
    }

    [Fact]
    public void OnATemporalTargetEveryTransparentPassPreservesTheMotionAttachment()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        GpuBlendAttachment alpha = GpuBlendAttachment.AlphaBlend, additive = GpuBlendAttachment.Additive;

        using (new TexturedBillboardRenderer(device, ModelTargets.Temporal))
            AssertBlends(factory, ShaderSources.BillboardVert, ShaderSources.TexturedBillboardMotionFrag, 4,
                alpha, additive);
        using (new BeamRenderer(device, ModelTargets.Temporal))
            AssertBlends(factory, ShaderSources.BeamVert, ShaderSources.BeamMotionFrag, 4, additive);
        using (new TrailRenderer(device, ModelTargets.Temporal))
            AssertBlends(factory, ShaderSources.TrailVert, ShaderSources.TrailMotionFrag, 4, additive, alpha);
        using (new OverlayMeshRenderer(device, ModelTargets.Temporal))
            AssertBlends(factory, ShaderSources.OverlayUnlitVert, ShaderSources.OverlayUnlitMotionFrag, 4, alpha);
        using (new SilhouetteRenderer(device, ModelTargets.Temporal))
            AssertBlends(factory, ShaderSources.SilhouetteVert, ShaderSources.SilhouetteMotionFrag, 4, alpha);
    }

    [Fact]
    public void OnTheBaseTargetTheTransparentPassesBuildWhatTheyAlwaysDid()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        GpuBlendAttachment alpha = GpuBlendAttachment.AlphaBlend, additive = GpuBlendAttachment.Additive;

        using (new TexturedBillboardRenderer(device, ModelTargets.Base))
        using (new BeamRenderer(device, ModelTargets.Base))
        using (new TrailRenderer(device, ModelTargets.Base))
        using (new OverlayMeshRenderer(device, ModelTargets.Base))
        using (new SilhouetteRenderer(device, ModelTargets.Base))
        {
            Assert.Equal(7, factory.GraphicsPipelines.Count);
            AssertBlends(factory, ShaderSources.BillboardVert, ShaderSources.TexturedBillboardFrag, 3,
                alpha, additive);
            AssertBlends(factory, ShaderSources.BeamVert, ShaderSources.BeamFrag, 3, additive);
            AssertBlends(factory, ShaderSources.TrailVert, ShaderSources.TrailFrag, 3, additive, alpha);
            AssertBlends(factory, ShaderSources.OverlayUnlitVert, ShaderSources.OverlayUnlitFrag, 3, alpha);
            AssertBlends(factory, ShaderSources.SilhouetteVert, ShaderSources.SilhouetteFrag, 3, alpha);
            Assert.All(factory.GraphicsPipelines, p =>
                Assert.DoesNotContain("oMotion", p.FragmentGlsl, StringComparison.Ordinal));

            // Nothing compiled the motion programs either, whether or not a pipeline would have used them.
            string[] motion =
            {
                ShaderSources.TexturedBillboardMotionFrag, ShaderSources.BeamMotionFrag, ShaderSources.TrailMotionFrag,
                ShaderSources.OverlayUnlitMotionFrag, ShaderSources.SilhouetteMotionFrag,
            };
            Assert.NotEmpty(factory.ShaderRequests);
            Assert.DoesNotContain(factory.ShaderRequests, r => motion.Contains(r.FragmentGlsl)
                || r.FragmentGlsl.Contains("oMotion", StringComparison.Ordinal));
        }
    }
}
