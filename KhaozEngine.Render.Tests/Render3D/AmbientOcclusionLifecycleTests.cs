using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AmbientOcclusionLifecycleTests
{
    [Fact]
    public void NativeTemporalRetainsItsBaseAttachmentsWhenAoHasNoSpareSlot()
    {
        using var rig = new HeadlessSceneRig(GpuBackendKind.VulkanNative, maxColorAttachments: 5);
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Scene.Post.AmbientOcclusion.Enabled = true;
        rig.Frame();

        Assert.Equal(AmbientOcclusionStatus.InsufficientColorAttachments, rig.Scene.EffectiveAmbientOcclusion);
        Assert.NotNull(rig.Scene.ResolvedVisibilityTarget);
        Assert.True(rig.Scene.ResolvedLastRenderForTests);
        Assert.Equal(0, rig.Scene.AmbientOcclusionTextureBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedReplacementKeepsTheAoTargetsUntilAnOrdinaryOutputIsReady(bool foliage)
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.Post.AmbientOcclusion.Enabled = true;
        rig.Frame();
        long activeBytes = rig.Scene.AmbientOcclusionTextureBytes;
        Assert.True(activeBytes > 0);

        var failure = new InvalidOperationException("Pipeline creation refused.");
        rig.Factory.PipelineCreationFailure = description =>
            description.ShaderSet is FakeShaderSet shader &&
            shader.VertexGlsl == (foliage ? ShaderSources.FoliageVert : ShaderSources.ModelVert) ? failure : null;
        rig.Scene.Post.AmbientOcclusion.Enabled = false;
        rig.Scene.Begin();
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            rig.Scene.PrepareFrame(HeadlessSceneRig.Width, HeadlessSceneRig.Height)));
        Assert.Equal(activeBytes, rig.Scene.AmbientOcclusionTextureBytes);

        rig.Factory.PipelineCreationFailure = null;
        rig.Frame();
        Assert.Equal(AmbientOcclusionStatus.Disabled, rig.Scene.EffectiveAmbientOcclusion);
        Assert.Equal(0, rig.Scene.AmbientOcclusionTextureBytes);
    }

    [Fact]
    public void RetryAfterAaLayoutRefusalBindsTheNewOpaqueOutputs()
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Scene.Post.AmbientOcclusion.Enabled = true;
        rig.Frame();
        Assert.Equal(AmbientOcclusionStatus.Active, rig.Scene.EffectiveAmbientOcclusion);

        var failure = new InvalidOperationException("Pipeline creation refused.");
        rig.Factory.PipelineCreationFailure = description =>
            description.ShaderSet is FakeShaderSet shader && shader.VertexGlsl == ShaderSources.ModelVert ? failure : null;
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        rig.Scene.Begin();
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            rig.Scene.PrepareFrame(HeadlessSceneRig.Width, HeadlessSceneRig.Height)));

        rig.Factory.PipelineCreationFailure = null;
        using var commands = (NullGpuCommandList)rig.Factory.CreateCommandList();
        int opaqueOutputs = 0;
        commands.PipelineBinding = pipeline =>
        {
            if (pipeline is FakePipeline { Request: { } request } &&
                request.FragmentGlsl.Contains("oAmbientDiffuse", StringComparison.Ordinal) &&
                request.VertexGlsl == ShaderSources.ModelVert)
                opaqueOutputs = request.Description.Outputs.Colour.Length;
        };
        rig.Frame(commands: commands);
        Assert.Equal(4, opaqueOutputs);
        Assert.Equal(AmbientOcclusionStatus.Active, rig.Scene.EffectiveAmbientOcclusion);
        Assert.False(rig.Scene.ResolvedLastRenderForTests);
    }

}
