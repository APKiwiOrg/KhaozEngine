using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// What a REFUSED point-shadow layout does, device-free. <c>Scene3D.EnsurePointShadowAtlas</c> is documented to
/// answer false and leave the previous atlas drawable, and the atlas texture is only half of what a layout costs:
/// the pass behind it is four shader sets and four pipelines, any of which a backend can refuse.
/// <para>
/// The seam is <see cref="FakeGpuResourceFactory.ThrowOnResourceSetCreate"/>. The pass's slot ring is the LAST
/// thing its constructor builds, so a refused resource set fails after all four pipelines exist, which is the
/// worst case for the cleanup: everything the attempt allocated has to come back.
/// </para>
/// </summary>
public sealed class PointShadowAtlasFailureTests
{
    [Fact]
    public void A_refused_pass_keeps_the_previous_atlas_and_answers_false()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using IGpuTexture targetTexture = factory.CreateTexture(GpuTextureDescription.Texture2D(
            64, 64, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using IGpuFramebuffer target = factory.CreateFramebuffer(null, targetTexture);
        using var scene = new Scene3D(device, target.Outputs);

        Assert.True(scene.EnsurePointShadowAtlas(32, 2));
        IGpuTexture? live = scene.PointShadowTexture;
        Assert.NotNull(live);

        int texturesBefore = factory.Textures.Count;
        int buffersBefore = factory.Buffers.Count;
        factory.ThrowOnResourceSetCreate = factory.ResourceSets.Count + 1;

        Assert.False(scene.EnsurePointShadowAtlas(64, 4));

        // The live layout is untouched, so the frame that asked for a bigger one still has somewhere to render.
        Assert.Same(live, scene.PointShadowTexture);
        Assert.Equal(32, scene.PointShadowFaceResolution);
        Assert.Equal(2, scene.PointShadowRows);

        // And the refused attempt left nothing behind: its atlas pair and its slot buffer are freed.
        Assert.Equal(texturesBefore + 2, factory.Textures.Count);
        for (int i = texturesBefore; i < factory.Textures.Count; i++) Assert.True(factory.Textures[i].Disposed);
        Assert.Equal(buffersBefore + 1, factory.Buffers.Count);
        for (int i = buffersBefore; i < factory.Buffers.Count; i++) Assert.True(factory.Buffers[i].Disposed);
    }

    [Fact]
    public void The_same_layout_can_be_asked_for_again_after_a_refusal()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using IGpuTexture targetTexture = factory.CreateTexture(GpuTextureDescription.Texture2D(
            64, 64, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using IGpuFramebuffer target = factory.CreateFramebuffer(null, targetTexture);
        using var scene = new Scene3D(device, target.Outputs);

        factory.ThrowOnResourceSetCreate = factory.ResourceSets.Count + 1;
        Assert.False(scene.EnsurePointShadowAtlas(32, 2));
        Assert.Null(scene.PointShadowTexture);

        factory.ThrowOnResourceSetCreate = 0;
        Assert.True(scene.EnsurePointShadowAtlas(32, 2));
        Assert.NotNull(scene.PointShadowTexture);
        Assert.Equal(32, scene.PointShadowFaceResolution);
    }

    /// <summary>
    /// The pass's own half of the same rule: a constructor that got as far as its pipelines and was then refused
    /// frees every one of them rather than letting the throw carry them away. Counted through the factory's
    /// records, because a leaked pipeline has no other observable.
    /// </summary>
    [Fact]
    public void A_refused_constructor_frees_every_resource_it_had_already_built()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using IGpuTexture targetTexture = factory.CreateTexture(GpuTextureDescription.Texture2D(
            64, 64, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using IGpuFramebuffer target = factory.CreateFramebuffer(null, targetTexture);
        using var scene = new Scene3D(device, target.Outputs);

        int pipelinesBefore = factory.GraphicsPipelines.Count;
        int setsBefore = factory.ResourceSets.Count;
        factory.ThrowOnResourceSetCreate = setsBefore + 1;

        Assert.False(scene.EnsurePointShadowAtlas(32, 2));

        // Four rigid and three skinned pipelines were built before the refusal.
        Assert.Equal(pipelinesBefore + 7, factory.GraphicsPipelines.Count);
        // The refused set was never handed out, so nothing was added to the record and nothing is left live.
        Assert.Equal(setsBefore, factory.ResourceSets.Count);
    }
}
