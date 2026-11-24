using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
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

    [Fact]
    public void FailedReplacementKeepsTheAoTargetsUntilAnOrdinaryOutputIsReady()
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.Post.AmbientOcclusion.Enabled = true;
        rig.Frame();
        long activeBytes = rig.Scene.AmbientOcclusionTextureBytes;
        Assert.True(activeBytes > 0);

        var failure = new InvalidOperationException("Pipeline creation refused.");
        rig.Factory.PipelineCreationFailure = _ => failure;
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
}
