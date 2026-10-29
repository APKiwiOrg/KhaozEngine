using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterColumnResourceLifecycleTests
{
    static T Field<T>(Scene3D scene, string name) => (T)typeof(Scene3D)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;

    [Fact]
    public void FreshSceneBorrowsShadowsWithoutWaterAllocationsOrRetirements()
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
        var factory = (FakeGpuResourceFactory)device.Factory;
        GpuRetireQueue retired = Field<GpuRetireQueue>(scene, "_retired");
        Assert.Equal(0, retired.PendingCount);
        // The construction inventory before M5 (0af218d5b), without standalone column fallbacks.
        Assert.Equal((19, 32, 4), (factory.Textures.Count, factory.Buffers.Count, factory.SamplerCreateCount));
        WaterRenderer water = Field<WaterRenderer>(scene, "_water");
        ModelRenderer model = Field<ModelRenderer>(scene, "_model");
        Assert.Same(model.ShadowMap.ShadowTexture, water.ColumnShadows.ShadowAtlas);
        Assert.Same(model.ShadowMap.ShadowSampler, water.ColumnShadows.ShadowSampler);
        Assert.Same(model.SharedFrameUniforms, water.ColumnShadows.FrameUniforms);
        water.Dispose();
        Assert.False(Assert.IsType<FakeTexture>(model.ShadowMap.ShadowTexture).Disposed);
        Assert.False(Assert.IsType<FakeBuffer>(model.SharedFrameUniforms).Disposed);
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
        Assert.Null(Field<WaterColumnColorSource?>(scene, "_waterColumnColor"));
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var target = factory.CreateFramebuffer(null, scene.ResolvedDepthTarget);
        scene.RenderInternal(commands, 96, 64, target);
        Assert.Null(Field<WaterColumnColorSource?>(scene, "_waterColumnColor"));
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
