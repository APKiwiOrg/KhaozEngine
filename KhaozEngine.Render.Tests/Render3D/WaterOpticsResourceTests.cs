using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterOpticsResourceTests
{
    static WaterOpticsStats Stats(int fields, long live, long retired, long uploads) =>
        new(fields, live, retired, uploads) { LiveRetainedCpuBytes = live, RetiredRetainedCpuBytes = retired };

    static WaterOpticsField Field() => new(2, 2, [2f, -1f, 30f, 100f]);

    [Fact]
    public void LoadUploadsR32ExactlyOnce()
    {
        var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        var uploads = new List<float[]>();
        device.OnTextureUpload = (_, bytes) => uploads.Add(MemoryMarshal.Cast<byte, float>(bytes).ToArray());
        var field = new WaterOpticsField(3, 2, [2f, -1f, 30f, 100f, 0f, 0.125f]);

        WaterOpticsHandle handle = resources.Load(field);
        WaterOpticsResources.Entry entry = resources.Resolve(handle);
        Assert.Same(entry, resources.Resolve(handle));
        Assert.Same(field, entry.Payload.BilinearField);
        Assert.Equal(handle, entry.Handle);
        Assert.True(handle.IsValid);
        FakeTexture texture = Assert.Single(factory.Textures);
        Assert.Same(texture, entry.Texture);
        Assert.Equal(3u, texture.Width);
        Assert.Equal(2u, texture.Height);
        Assert.Equal(GpuPixelFormat.R32Float, texture.Format);
        Assert.Equal(GpuTextureUsage.Sampled, texture.Usage);
        Assert.Equal(new[] { 2f, -1f, 30f, 100f, 0f, 0.125f }, Assert.Single(uploads));
        Assert.Equal(Stats(1, 24, 0, 1), resources.Stats);
    }

    [Fact]
    public void SourceBytesCannotChangeAfterUpload()
    {
        var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        float[] source = [2f, -1f, 30f, 100f];
        byte[]? uploaded = null;
        device.OnTextureUpload = (_, bytes) => uploaded = (byte[])bytes.Clone();
        var field = new WaterOpticsField(2, 2, source);
        WaterOpticsHandle handle = resources.Load(field);

        Array.Fill(source, 999f);

        Assert.Equal(new[] { 2f, -1f, 30f, 100f }, resources.Resolve(handle).Payload.BilinearField!.RestDepths.ToArray());
        Assert.NotNull(uploaded);
        Assert.Equal(new[] { 2f, -1f, 30f, 100f }, MemoryMarshal.Cast<byte, float>(uploaded).ToArray());
        Assert.Equal(1, resources.Stats.UploadCount);
    }

    [Fact]
    public void ForeignOwnerAndStaleGenerationAreRefused()
    {
        var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var first = new WaterOpticsResources(device, retired);
        using var second = new WaterOpticsResources(device, retired);
        WaterOpticsHandle old = first.Load(Field());
        WaterOpticsHandle foreign = second.Load(Field());
        Assert.Equal(old.Index, foreign.Index);
        Assert.Equal(old.Generation, foreign.Generation);
        Assert.Throws<ArgumentException>(() => first.Resolve(foreign));
        Assert.Throws<ArgumentException>(() => first.Unload(foreign));

        first.Unload(old);
        WaterOpticsHandle replacement = first.Load(Field());
        Assert.Equal(old.Index, replacement.Index);
        Assert.NotEqual(old.Generation, replacement.Generation);
        Assert.Throws<ArgumentException>(() => first.Resolve(old));
        Assert.Throws<ArgumentException>(() => first.Unload(old));
        Assert.Throws<ArgumentException>(() => first.Resolve(default));
        Assert.Throws<ArgumentException>(() => first.Unload(new(old.Owner, -1, 0)));
        Assert.Equal(replacement, first.Resolve(replacement).Handle);
        first.Unload(default);
        Assert.Equal(1, first.Stats.LiveFields);
    }

    [Fact]
    public void ReleaseDefersDestructionButInvalidatesTheHandle()
    {
        var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        WaterOpticsHandle old = resources.Load(Field());
        var texture = (FakeTexture)resources.Resolve(old).Texture;

        resources.Unload(old);

        Assert.Throws<ArgumentException>(() => resources.Resolve(old));
        Assert.Throws<ArgumentException>(() => resources.Unload(old));
        Assert.False(texture.Disposed);
        Assert.Equal(Stats(0, 0, 16, 1), resources.Stats);
        WaterOpticsHandle replacement = resources.Load(Field());
        var replacementTexture = (FakeTexture)resources.Resolve(replacement).Texture;
        Assert.NotSame(texture, replacementTexture);
        retired.BeginFrame();
        Assert.False(texture.Disposed);
        Assert.Equal(Stats(1, 16, 16, 2), resources.Stats);
        retired.BeginFrame();
        Assert.True(texture.Disposed);
        Assert.False(replacementTexture.Disposed);
        Assert.Equal(Stats(1, 16, 0, 2), resources.Stats);
        Assert.Equal(0, device.WaitForIdleCalls);
    }

    [Fact]
    public void FailedCreationPublishesNoSlot()
    {
        var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        factory.FailTextureCreationAfter(0);

        Assert.Throws<InvalidOperationException>(() => resources.Load(Field()));
        Assert.Empty(factory.Textures);
        Assert.Equal(default, resources.Stats);
        factory.ThrowOnTextureCreate = 0;
        WaterOpticsHandle handle = resources.Load(Field());
        Assert.Equal(0, handle.Index);
        Assert.Equal(1, handle.Generation);
        Assert.Equal(Stats(1, 16, 0, 1), resources.Stats);
    }

    [Fact]
    public void FailedUploadRetiresItsTextureWithoutPublishingOrConsumingASlot()
    {
        var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        device.OnTextureUpload = (_, _) => throw new InvalidOperationException("planned upload failure");

        Assert.Throws<InvalidOperationException>(() => resources.Load(Field()));

        FakeTexture failed = Assert.Single(factory.Textures);
        Assert.False(failed.Disposed);
        Assert.Equal(Stats(0, 0, 16, 0), resources.Stats);
        device.OnTextureUpload = null;
        WaterOpticsHandle handle = resources.Load(Field());
        Assert.Equal(0, handle.Index);
        Assert.Equal(1, handle.Generation);
        retired.BeginFrame();
        Assert.False(failed.Disposed);
        retired.BeginFrame();
        Assert.True(failed.Disposed);
        Assert.Equal(Stats(1, 16, 0, 1), resources.Stats);
    }

    [Fact]
    public void DisposalRetiresLiveFieldsAndKeepsBytesUntilQueueDisposal()
    {
        var device = new FakeGpuDevice();
        var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        WaterOpticsHandle handle = resources.Load(Field());
        var texture = (FakeTexture)resources.Resolve(handle).Texture;

        resources.Dispose();
        resources.Dispose();

        Assert.Throws<ObjectDisposedException>(() => resources.Resolve(handle));
        Assert.Throws<ObjectDisposedException>(() => resources.Load(Field()));
        Assert.False(texture.Disposed);
        Assert.Equal(Stats(0, 0, 16, 1), resources.Stats);
        retired.Dispose();
        Assert.True(texture.Disposed);
        Assert.Equal(Stats(0, 0, 0, 1), resources.Stats);
    }

    [Fact]
    public void DisposedSceneDiagnosesUnloadAndBoundDrawAsDisposed()
    {
        var device = new FakeGpuDevice();
        var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        WaterOpticsHandle handle = scene.LoadWaterOpticsField(Field());
        var binding = new WaterOpticsBinding(handle, new Vector3(-5f, 0f, -5f), new Vector2(10f));
        scene.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scene.UnloadWaterOpticsField(handle));
        Assert.Throws<ObjectDisposedException>(() => scene.DrawWater(new WaterPlane(0f, 0f, 0f, 5f, optics: binding)));
        scene.UnloadWaterOpticsField(default);
    }

    [Fact]
    public void NewSceneReloadsCpuFieldAndRejectsOldHandle()
    {
        var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        var output = new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm);
        var oldScene = new Scene3D(device, output);
        WaterOpticsField field = Field();
        int from = factory.Textures.Count;
        WaterOpticsHandle old = oldScene.LoadWaterOpticsField(field);
        FakeTexture oldTexture = Assert.Single(factory.Textures.GetRange(from, factory.Textures.Count - from));
        oldScene.Dispose();
        Assert.True(oldTexture.Disposed);
        Assert.Equal(Stats(0, 0, 0, 1), oldScene.WaterOpticsStats);
        Assert.Throws<ObjectDisposedException>(() => oldScene.LoadWaterOpticsField(field));

        using var newScene = new Scene3D(new FakeGpuDevice(), output);
        newScene.Post.Water.SwellAmplitude = 0f;
        WaterOpticsHandle reloaded = newScene.LoadWaterOpticsField(field);
        Assert.NotEqual(old.Owner, reloaded.Owner);
        var binding = new WaterOpticsBinding(old, new Vector3(-5f, 0f, -5f), new Vector2(10f));
        Assert.Throws<ArgumentException>(() => newScene.DrawWater(new WaterPlane(0f, 0f, 0f, 5f, optics: binding)));
        Assert.Throws<ArgumentException>(() => newScene.UnloadWaterOpticsField(old));
        newScene.DrawWater(new WaterPlane(0f, 0f, 0f, 5f,
            optics: new WaterOpticsBinding(reloaded, binding.Origin, binding.Size)));
        Assert.Equal(1, newScene.WaterPlaneCount);
        newScene.UnloadWaterOpticsField(reloaded);
        Assert.Equal(Stats(0, 0, 16, 1), newScene.WaterOpticsStats);
        Assert.Throws<ArgumentException>(() => newScene.UnloadWaterOpticsField(reloaded));
        newScene.UnloadWaterOpticsField(default);
        newScene.Begin();
        newScene.Begin();
        newScene.Begin();
        Assert.Equal(Stats(0, 0, 0, 1), newScene.WaterOpticsStats);
    }

    [GpuFact]
    public void MaximumFieldIsSampledAsR32FloatOnTheBackend()
    {
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = gpu.GpuDevice;
        IGpuResourceFactory factory = device.Factory;
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        var samples = new float[1024 * 1024];
        samples[0] = -1f;
        samples[1023] = 2f;
        samples[1023 * 1024] = 30f;
        samples[^1] = 100f;
        WaterOpticsHandle handle = resources.Load(new WaterOpticsField(1024, 1024, samples));
        Assert.Equal(Stats(1, 4_194_304, 0, 1), resources.Stats);
        using IGpuTexture target = factory.CreateTexture(GpuTextureDescription.Texture2D(
            4, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using IGpuFramebuffer framebuffer = factory.CreateFramebuffer(null, target);
        using IGpuResourceLayout layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Depths", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("DepthSampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
        using IGpuResourceSet set = factory.CreateResourceSet(new GpuResourceSetDescription(
            layout, resources.Resolve(handle).Texture, device.PointSampler));
        using IGpuShaderSet shaders = factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert, @"#version 450
layout(set = 0, binding = 0) uniform texture2D Depths;
layout(set = 0, binding = 1) uniform sampler DepthSampler;
layout(location = 0) out vec4 oColor;
void main() {
    int corner = int(gl_FragCoord.x);
    ivec2 at = ivec2((corner % 2) * 1023, (corner / 2) * 1023);
    float depth = texelFetch(sampler2D(Depths, DepthSampler), at, 0).r;
    oColor = vec4((depth + 1.0) / 101.0, 0.0, 0.0, 1.0);
}");
        using IGpuPipeline pipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                GpuFrontFace.Clockwise, false, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [layout],
            ShaderSet = shaders,
            VertexLayouts = new List<GpuVertexLayoutDescription>(),
            Outputs = framebuffer.Outputs,
        });
        using (IGpuCommandList commands = factory.CreateCommandList())
        {
            commands.Begin();
            commands.SetFramebuffer(framebuffer);
            commands.ClearColorTarget(0, Color.Black);
            commands.SetPipeline(pipeline);
            commands.SetGraphicsResourceSet(0, set);
            commands.Draw(3);
            commands.End();
            device.Submit(commands);
            device.WaitForIdle();
        }
        byte[] pixels = GpuReadback.ToRgba(device, target, 4, 1);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 8, 0, 0, 255, 78, 0, 0, 255, 255, 0, 0, 255 }, pixels);
    }
}
