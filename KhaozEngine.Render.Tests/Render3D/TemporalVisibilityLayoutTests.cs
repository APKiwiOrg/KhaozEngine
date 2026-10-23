using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalVisibilityLayoutTests
{
    internal static GpuOutputDescription Layout(bool motion, bool visibility) => new(
        GpuPixelFormat.D32FloatS8UInt,
        new[] { GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float }
            .Concat(motion ? new[] { GpuPixelFormat.R16G16Float } : Array.Empty<GpuPixelFormat>())
            .Concat(visibility ? new[] { GpuPixelFormat.R8UNorm } : Array.Empty<GpuPixelFormat>()).ToArray());

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MotionRequiresItsOwnSlotAndFormat(bool motion, bool visibility)
        => Assert.Equal(motion, MotionMath.IsTemporal(Layout(motion, visibility)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryLitAndPreservingPipelineNamesTheVisibilitySlot(bool motion)
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        GpuOutputDescription outputs = Layout(motion, true);
        using var model = new ModelRenderer(device, outputs, 64, 1);
        using var billboards = new TexturedBillboardRenderer(device, outputs);
        using var beams = new BeamRenderer(device, outputs);
        using var trails = new TrailRenderer(device, outputs);
        using var overlays = new OverlayMeshRenderer(device, outputs);
        using var silhouettes = new SilhouetteRenderer(device, outputs);
        using IGpuCommandList commands = factory.CreateCommandList();
        model.UploadFoliageUniforms(commands, [default]);
        var pipelines = factory.GraphicsPipelines.Where(p => p.Description.Outputs.Colour.Length == outputs.Colour.Length).ToArray();
        Assert.NotEmpty(pipelines);
        Assert.All(pipelines, p =>
        {
            Assert.Contains($"layout(location={(motion ? 4 : 3)}) out float oKeyVisibility;", p.FragmentGlsl);
            Assert.Equal(outputs.Colour.Length, p.Description.BlendAttachments.Length);
            if (p.FragmentGlsl.Contains("float keyShadow =", StringComparison.Ordinal))
                Assert.Contains("oKeyVisibility = keyShadow;", p.FragmentGlsl);
            else
                Assert.Equal(GpuBlendAttachment.PreserveDestination, p.Description.BlendAttachments[^1]);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TargetRecreationTracksIndependentVisibilityAndMotion(bool motion, bool visibility)
    {
        using var device = new FakeGpuDevice();
        using var targets = new RenderResources(device, 32, 24, false);
        int generation = targets.Generation;
        targets.Resize(32, 24, false, 1, false, false, motion, visibility: visibility);
        Assert.Equal(3 + (motion ? 1 : 0) + (visibility ? 1 : 0), targets.ModelFB.Outputs.Colour.Length);
        Assert.Equal(motion, MotionMath.IsTemporal(targets.ModelFB.Outputs));
        Assert.Equal(visibility, ShadowVisibilityTargets.HasVisibility(targets.ModelFB.Outputs));
        Assert.Equal(visibility, targets.LitVisibilityTex is not null);
        if (motion || visibility) Assert.True(targets.Generation > generation);
        var old = targets.LitVisibilityTex as FakeTexture;
        targets.Resize(32, 24, false, 1, false, false);
        Assert.Null(targets.LitVisibilityTex);
        Assert.Null(targets.MsLitVisibilityTex);
        if (old is not null) Assert.True(old.Disposed);
    }

    [Fact]
    public void FourAttachmentDeviceUsesApprovedVisibilityPolicy()
    {
        using var device = new FakeGpuDevice(maxColorAttachments: 4);
        using var targets = new RenderResources(device, 32, 24, false, visibility: true);
        targets.Resize(32, 24, false, 1, false, false, motion: true, visibility: true);
        Assert.Null(targets.LitVisibilityTex);
        Assert.False(ShadowVisibilityTargets.HasVisibility(targets.ModelFB.Outputs));
        Assert.True(MotionMath.IsTemporal(targets.ModelFB.Outputs));
        Assert.Equal(4, targets.ModelFB.Outputs.Colour.Length);
        using var model = new ModelRenderer(device, targets.ModelFB.Outputs, 64, 1);
        Assert.All(((FakeGpuResourceFactory)device.Factory).GraphicsPipelines, p =>
        {
            Assert.True(p.Description.Outputs.Colour.Length <= 4);
            Assert.DoesNotContain("oKeyVisibility", p.FragmentGlsl);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OffAndFxaaReleaseTheTemporalVisibilityAllocation(bool fxaa)
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.Post.Quality.AntiAliasing = fxaa ? AntiAliasing.Fxaa : AntiAliasing.Off;
        rig.Frame();
        Assert.Null(rig.Scene.ResolvedVisibilityTarget);
        rig.Scene.ForceTemporalForTests = true;
        rig.Frame();
        Assert.NotNull(rig.Scene.ResolvedVisibilityTarget);
        rig.Scene.ForceTemporalForTests = false;
        rig.Frame();
        Assert.Null(rig.Scene.ResolvedVisibilityTarget);
    }
}
