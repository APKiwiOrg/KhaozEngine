using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterColumnResourceLifecycleTests
{
    [Fact]
    public void FreshSceneBorrowsShadowsWithoutWaterAllocationsOrRetirements()
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
        var factory = (FakeGpuResourceFactory)device.Factory;
        Assert.Equal(0, scene.RetiredResourceCount);
        // The construction inventory before M5 (0af218d5b), without standalone column fallbacks, plus the one
        // caustic placeholder texture and sampler a scene that never participates still owns.
        Assert.Equal((20, 32, 5), (factory.Textures.Count, factory.Buffers.Count, factory.SamplerCreateCount));
        var water = scene.WaterShadowBindingsForTests;
        var model = scene.ModelShadowBindingsForTests;
        Assert.Same(model.Atlas, water.Atlas);
        Assert.Same(model.Sampler, water.Sampler);
        Assert.Same(model.FrameUniforms, water.FrameUniforms);
        scene.DisposeWaterForTests();
        Assert.False(Assert.IsType<FakeTexture>(model.Atlas).Disposed);
        Assert.False(Assert.IsType<FakeBuffer>(model.FrameUniforms).Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyFramesDoNotCreateOrUploadWaterColumnResources(bool viewportPrepared)
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
        scene.Post.Starfield = false;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        var uploads = new List<IGpuTexture>();
        device.OnTextureUpload = (texture, _) => uploads.Add(texture);
        scene.Begin();
        if (viewportPrepared) scene.PrepareFrame(96, 64);
        else scene.PrepareFrame();
        Assert.Null(scene.WaterColumnColorTextureForTests);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var target = factory.CreateFramebuffer(null, scene.ResolvedDepthTarget);
        scene.RenderInternal(commands, 96, 64, target);
        Assert.Null(scene.WaterColumnColorTextureForTests);
        Assert.Empty(uploads);
        Assert.Empty(commands.TextureCopies);
        Assert.Equal(default, scene.WaterColumnLightDiagnostics);
    }

    [Fact]
    public void StandaloneWaterDrawsWithAndDisposesItsOwnedNeutralFallbacks()
    {
        using var device = new FakeGpuDevice();
        using var targets = new RenderResources(device, 96, 64, false);
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 3);
        var uploads = new List<(IGpuTexture Texture, byte[] Data)>();
        device.OnTextureUpload = (texture, data) => uploads.Add((texture, data));
        using var water = new WaterRenderer(device, targets.ColorDepthFB.Outputs, retired);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
        WaterTestFrames.Draw(water, commands, targets, [new WaterPlane(0f, 0f, 0f, 5f)],
            new WaterSettings { SwellAmplitude = 0f, WaterColumnLight = true }, new Vector3(0f, 100f, 0f));
        Assert.Single(commands.IndexedDraws);
        var factory = (FakeGpuResourceFactory)device.Factory;
        FakeResourceSet set = Assert.Single(factory.ResourceSets, s => s.Resources.Length == 17);
        var atlas = Assert.IsType<FakeTexture>(set.Resources[12]);
        var frame = Assert.IsType<FakeBuffer>(set.Resources[14]);
        var color = Assert.IsType<FakeTexture>(set.Resources[15]);
        Assert.Equal(BitConverter.GetBytes(1f), Assert.Single(uploads, u => ReferenceEquals(atlas, u.Texture)).Data);
        Assert.Equal(new byte[4], Assert.Single(uploads, u => ReferenceEquals(color, u.Texture)).Data);
        Assert.Equal(1024u, frame.SizeInBytes);
        Assert.Equal(0, retired.PendingCount);
        water.Dispose();
        Assert.True(atlas.Disposed);
        Assert.True(frame.Disposed);
        Assert.True(color.Disposed);
    }
}
