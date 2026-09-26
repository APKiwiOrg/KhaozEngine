using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The motion target is the model framebuffer's fourth attachment exactly while it is asked for, and nothing
/// of it exists otherwise (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 4).</summary>
public sealed class MotionTargetResourcesTests
{
    [Fact]
    public void WithoutMotionTheModelTargetKeepsThreeAttachmentsAndCreatesNoMotionTexture()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var res = new RenderResources(device, 64, 32, hdrColor: true);
        res.Resize(96, 48, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: true);

        Assert.False(res.MotionAllocated);
        Assert.Null(res.MotionTex);
        Assert.Equal(new[] { GpuPixelFormat.R16G16B16A16Float, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float },
            res.ModelFB.Outputs.Colour);
        Assert.DoesNotContain(factory.Textures, t => t.Format == GpuPixelFormat.R16G16Float);
    }

    [Fact]
    public void ResizingWithMotionOnRecreatesTheTargetAtTheNewSize()
    {
        using var device = new FakeGpuDevice();
        using var res = new RenderResources(device, 64, 32, hdrColor: true);
        res.Resize(96, 48, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: true, motion: true);
        var before = (FakeTexture)res.MotionTex!;

        res.Resize(128, 72, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: true, motion: true);

        var after = Assert.IsType<FakeTexture>(res.MotionTex);
        Assert.True(before.Disposed);
        Assert.Equal((128u, 72u), (after.Width, after.Height));
        Assert.Equal(4, res.ModelFB.Outputs.Colour.Length);
    }

    [Fact]
    public void TurningMotionOnAtTheSameSizeRebuildsTheTargets()
    {
        using var device = new FakeGpuDevice();
        using var res = new RenderResources(device, 64, 32, hdrColor: true);
        res.Resize(96, 48, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: true);
        int generation = res.Generation;

        res.Resize(96, 48, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: true, motion: true);

        Assert.True(res.MotionAllocated);
        Assert.True(res.Generation > generation);
        Assert.Equal(4, res.ModelFB.Outputs.Colour.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheModelPassClearsTheMotionTargetToTheSentinelOnlyWhileItExists(bool motion)
    {
        using var device = new FakeGpuDevice();
        using var res = new RenderResources(device, 64, 32, hdrColor: true);
        res.Resize(96, 48, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: true, motion: motion);
        using var model = new ModelRenderer(device, res.ModelFB.Outputs, 64, 1);
        var settings = new PixelPostProcessSettings();
        var commands = new RecordingGpuCommandList(new NullGpuCommandList());

        model.BeginModelPass(commands, res, settings);

        Color background = settings.BackgroundColor.WithAlpha(0f);
        var expected = new List<RecordingGpuCommandList.ColourClear>
        {
            new(0, background), new(1, background), new(2, background),
        };
        if (motion) expected.Add(new(MotionMath.Attachment, MotionMath.SentinelColor));
        Assert.Equal(expected, commands.ColourClears);
    }

    [Fact]
    public void WithMotionTheTargetIsAFourthRg16fAttachmentAtTheInternalSize()
    {
        using var device = new FakeGpuDevice();
        using var res = new RenderResources(device, 64, 32, hdrColor: true);
        res.Resize(96, 48, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: true, motion: true);

        Assert.True(res.MotionAllocated);
        var motion = Assert.IsType<FakeTexture>(res.MotionTex);
        Assert.Equal(GpuPixelFormat.R16G16Float, motion.Format);
        Assert.Equal((96u, 48u), (motion.Width, motion.Height));
        Assert.Equal(GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled, motion.Usage);
        Assert.Equal(new[] { GpuPixelFormat.R16G16B16A16Float, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float,
            GpuPixelFormat.R16G16Float }, res.ModelFB.Outputs.Colour);
        Assert.Single(res.ColorDepthFB.Outputs.Colour);   // the passes after the model pass never see it
    }

    [Fact]
    public void DroppingMotionFreesTheTargetAndRestoresThreeAttachments()
    {
        using var device = new FakeGpuDevice();
        using var res = new RenderResources(device, 64, 32, hdrColor: true);
        res.Resize(96, 48, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: true, motion: true);
        var motion = (FakeTexture)res.MotionTex!;
        int generation = res.Generation;

        res.Resize(96, 48, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: true, motion: false);

        Assert.True(motion.Disposed);
        Assert.Null(res.MotionTex);
        Assert.Equal(3, res.ModelFB.Outputs.Colour.Length);
        Assert.True(res.Generation > generation);
    }

    [Fact]
    public void MotionRefusesAMultisampledTargetBeforeTouchingTheCurrentOne()
    {
        using var device = new FakeGpuDevice();
        using var res = new RenderResources(device, 64, 32, hdrColor: true);
        IGpuFramebuffer before = res.ModelFB;

        Assert.Throws<ArgumentException>(() =>
            res.Resize(96, 48, mipped: false, sampleCount: 4, bloomEnabled: false, hdrColor: true, motion: true));
        Assert.Same(before, res.ModelFB);
    }
}
