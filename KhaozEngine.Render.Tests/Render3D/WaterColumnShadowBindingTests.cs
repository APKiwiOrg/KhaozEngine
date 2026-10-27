using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterColumnShadowBindingTests
{
    sealed class Harness : IDisposable
    {
        internal readonly FakeGpuDevice Device = new(capabilityOverride: new GpuCapabilities(
            false, true, "WaterColumnTests", false, false, 1, true, false, false)
        {
            MaxFragmentStorageBuffers = 3,
            MaxColorAttachments = 8,
        });
        internal readonly Scene3D Scene;
        internal readonly RenderResources Targets;
        internal readonly RecordingGpuCommandList Commands = new(new NullGpuCommandList()) { CapturePayloads = true };
        internal readonly WaterOpticsBinding Optics;
        internal readonly WaterRenderer Water;
        internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)Device.Factory;

        internal Harness()
        {
            Scene = new Scene3D(Device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm), new ShadowSettings
            {
                Mode = ShadowMode.ShadowMap, ShadowMapResolution = 256, ShadowCascadeCount = 2,
            });
            Targets = (RenderResources)typeof(Scene3D).GetField("_res", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Scene)!;
            Water = (WaterRenderer)typeof(Scene3D).GetField("_water", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Scene)!;
            Scene.Post.Water.SwellAmplitude = 0f;
            Scene.Post.Water.WaterColumnLight = true;
            var field = Scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [4f, 4f, 4f, 4f]));
            Optics = new WaterOpticsBinding(field, new Vector3(-5f, 0f, -5f), new Vector2(10f));
        }

        internal void Prepare(int count = 2)
        {
            Scene.Begin();
            for (int i = 0; i < count; i++)
                Scene.DrawWater(new WaterPlane(0f, 0f, 0f, 5f, optics: i % 2 == 0 ? Optics : default));
            Scene.PrepareFrame(96, 64);
        }

        internal FakeResourceSet[] WaterSets => Factory.ResourceSets.Where(s => !s.Disposed
            && s.Resources.Length >= 12 && s.Resources[6] is GpuBufferRange).ToArray();

        internal void VerifyPrepared() => Water.VerifyOpticsFrame(Targets, Scene.RelativeWaterPlanes(),
            Scene.Post.Water, Scene.EffectTimeSeconds);

        public void Dispose()
        {
            Scene.Dispose(); Commands.Dispose(); Device.Dispose();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void WaterSetUsesTheAlreadyUploadedModelFrame(int planes)
    {
        using var h = new Harness();
        h.Prepare(planes);
        FakeResourceSet[] sets = h.WaterSets;
        Assert.NotEmpty(sets);
        Assert.All(sets, set => Assert.Equal(15, set.Resources.Length));
        var model = (ModelRenderer)typeof(Scene3D).GetField("_model", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(h.Scene)!;
        IGpuBuffer frame = model.SharedFrameUniforms;
        Assert.All(sets, set =>
        {
            GpuResourceLayoutElement[] layout = ((FakeResourceLayout)set.Layout!).Description.Elements;
            Assert.Equal("ColumnShadowAtlas", layout[12].Name);
            Assert.Equal("ColumnShadowSampler", layout[13].Name);
            Assert.Equal("WaterLitFrame", layout[14].Name);
            Assert.Same(frame, set.Resources[14]);
            Assert.Same(model.ShadowMap.ShadowTexture, set.Resources[12]);
            Assert.Same(model.ShadowMap.ShadowSampler, set.Resources[13]);
            Assert.Equal(1280u, ((GpuBufferRange)set.Resources[6]).Size);
        });
        Matrix4x4[] matrices = [Matrix4x4.CreateScale(2f), Matrix4x4.CreateScale(3f),
            Matrix4x4.CreateScale(4f), Matrix4x4.CreateScale(5f)];
        model.SetShadowUniforms(matrices, 4, 1f / 512f, 0.001f, 0.002f, 0.75f, 80f, 0.05f, 0.1f,
            [0.01f, 0.02f, 0.03f, 0.04f]);
        model.SetFrameUniforms(h.Commands, Matrix4x4.Identity, Vector3.Zero, h.Scene.Post, []);
        var upload = Assert.Single(h.Commands.Uploads, u => ReferenceEquals(frame, u.Buffer));
        Assert.Equal(MemoryMarshal.AsBytes(matrices.AsSpan()).ToArray(), upload.Data!.AsSpan(688, 256).ToArray());
        Assert.Equal(new Vector4(4f, 0.75f, 0.001f, 0.002f), MemoryMarshal.Read<Vector4>(upload.Data.AsSpan(944)));
        Assert.Equal(new Vector4(1f / 512f, 80f, 0.05f, 0.1f), MemoryMarshal.Read<Vector4>(upload.Data.AsSpan(960)));
        Assert.Equal(new Vector4(0.01f, 0.02f, 0.03f, 0.04f), MemoryMarshal.Read<Vector4>(upload.Data.AsSpan(976)));
        Assert.Equal((Vector4)h.Scene.Post.AmbientColor, MemoryMarshal.Read<Vector4>(upload.Data.AsSpan(96)));
        h.Commands.Clear();
        WaterPlane[] queue = Enumerable.Range(0, planes).Select(i =>
            new WaterPlane(0f, 0f, 0f, 5f, optics: i % 2 == 0 ? h.Optics : default)).ToArray();
        h.Water.Draw(h.Commands, h.Targets, queue, WaterTestFrames.TopDown, WaterTestFrames.TopDown,
            -Vector3.UnitY, KhaozEngine.Primitives.Color.White, Vector3.UnitY * 100f,
            h.Scene.Post.Water, h.Scene.Post.Sky, 0f);
        Assert.DoesNotContain(h.Commands.Uploads, u => ReferenceEquals(frame, u.Buffer));
        Assert.Equal(688u, ModelRenderer.ShadowTailOffset);
        Assert.Equal(256, Marshal.OffsetOf<ModelRenderer.ShadowUbo>(nameof(ModelRenderer.ShadowUbo.Params)).ToInt32());
        Assert.Equal(944u, ModelRenderer.ShadowTailOffset + 256);
        Assert.Equal(960u, ModelRenderer.ShadowTailOffset + 272);
        Assert.Equal(976u, ModelRenderer.ShadowTailOffset + 288);
        Assert.Equal(96, Marshal.OffsetOf<ModelRenderer.FrameUbo>(nameof(ModelRenderer.FrameUbo.Ambient)).ToInt32());
    }

    [Fact]
    public void AtlasReplacementRebindsBoundAndUnboundWater()
    {
        using var h = new Harness();
        h.Prepare();
        FakeResourceSet[] oldSets = h.WaterSets;
        Assert.Equal(2, oldSets.Length);
        Assert.All(oldSets, s => Assert.Equal(15, s.Resources.Length));
        var atlas = (FakeTexture)oldSets[0].Resources[12];
        bool readersRetiredFirst = false;
        atlas.OnDispose = () => readersRetiredFirst = oldSets.All(s => s.Disposed);
        Assert.True(h.Scene.ReplaceShadowLayout(512, 3));
        Assert.Throws<InvalidOperationException>(h.VerifyPrepared);
        Assert.False(atlas.Disposed);
        h.Prepare();
        FakeResourceSet[] replacements = h.WaterSets.Except(oldSets).ToArray();
        Assert.Equal(2, replacements.Length);
        Assert.All(replacements, s => Assert.NotSame(atlas, s.Resources[12]));
        for (int i = 0; i < GpuRetireQueue.DefaultFrameDelay; i++) h.Scene.Begin();
        Assert.True(atlas.Disposed);
        Assert.True(readersRetiredFirst);
        Assert.All(oldSets, s => Assert.True(s.Disposed));
    }

    [Fact]
    public void FailedAtlasReplacementKeepsPreparedWaterUsable()
    {
        using var h = new Harness();
        h.Prepare();
        FakeResourceSet[] oldSets = h.WaterSets;
        h.Factory.ThrowOnTextureCreate = h.Factory.Textures.Count + 1;
        Assert.False(h.Scene.ReplaceShadowLayout(512, 3));
        h.VerifyPrepared();
        h.Factory.ThrowOnTextureCreate = 0;
        h.Prepare();
        Assert.Equal(oldSets, h.WaterSets);
    }

    [Fact]
    public void DisabledShadowsReturnOne()
    {
        using var device = new FakeGpuDevice();
        using var targets = new RenderResources(device, 96, 64, false);
        var uploads = new List<(IGpuTexture Texture, byte[] Data)>();
        device.OnTextureUpload = (texture, data) => uploads.Add((texture, data));
        using var water = new WaterRenderer(device, targets.ColorDepthFB.Outputs);
        var settings = new WaterSettings { SwellAmplitude = 0f, WaterColumnLight = true };
        var frame = new FramePrepare(settings, [new WaterPlane(0f, 0f, 0f, 5f)], 0f);
        water.PrepareFrame(frame);
        water.PrepareOpticsFrame(targets, frame);
        var factory = (FakeGpuResourceFactory)device.Factory;
        FakeResourceSet set = Assert.Single(factory.ResourceSets, s => s.Resources.Length >= 12);
        Assert.Equal(15, set.Resources.Length);
        var atlas = Assert.IsType<FakeTexture>(set.Resources[12]);
        Assert.Equal(GpuPixelFormat.R32Float, atlas.Format);
        Assert.Equal((1u, 1u), (atlas.Width, atlas.Height));
        Assert.Equal(BitConverter.GetBytes(1f), Assert.Single(uploads, u => ReferenceEquals(atlas, u.Texture)).Data);
        var uniforms = Assert.IsAssignableFrom<IGpuBuffer>(set.Resources[14]);
        Assert.Equal(1024u, uniforms.SizeInBytes);
        Assert.Contains("if (strength <= 0.0) return 1.0;", ShaderSources.WaterFrag, StringComparison.Ordinal);
        Assert.DoesNotContain("WaterVolumeBuffer", ShaderSources.WaterFrag, StringComparison.Ordinal);
    }

    [Fact]
    public void CascadeProjectionUsesItsOwnBoundsAndBias()
    {
        string shader = ShaderSources.WaterFrag;
        Assert.Contains("bool cascadeProject(mat4 matrix", shader, StringComparison.Ordinal);
        Assert.Contains("depth < 0.0 || depth > 1.0", shader, StringComparison.Ordinal);
        Assert.Contains("normalOffset * slopeSin", shader, StringComparison.Ordinal);
        Assert.Contains("float margin = texelStep * 2.0;", shader, StringComparison.Ordinal);
        Assert.Contains("float bias = ColumnShadowParams.z + ColumnShadowParams.w * slope;", shader, StringComparison.Ordinal);
        Assert.Contains("pcfCascade(ColumnShadowAtlas, ColumnShadowSampler", shader, StringComparison.Ordinal);
        var pair = SpirvCrossCompile.GlslPairToHlsl(ShaderSources.WaterVert, shader);
        GpuResourceLayoutElement[] elements = Assert.Single(pair.Reflection.ResourceLayouts).Elements;
        Assert.Equal(15, elements.Length);
        Assert.Equal(GpuResourceKind.TextureReadOnly, elements[12].Kind);
        Assert.Equal(GpuResourceKind.Sampler, elements[13].Kind);
        Assert.Equal(GpuResourceKind.UniformBuffer, elements[14].Kind);
        Assert.All(elements[12..], e => Assert.Equal(GpuShaderStages.Fragment, e.Stages));
        Assert.DoesNotContain(elements, e => e.Kind == GpuResourceKind.StructuredBufferReadOnly);
        ShaderValidation.ValidatePair(ShaderSources.WaterVert, shader, "column water");
        ShaderValidation.ValidatePair(ShaderSources.WaterClipmapVert, ShaderSources.WaterTriangleFrag, "triangle column water");
    }

    [Theory]
    [InlineData("atlas")]
    [InlineData("sampler")]
    [InlineData("frame")]
    public void EachShadowResourceIdentityInvalidatesPreparedBindings(string changed)
    {
        using var h = new Harness();
        h.Prepare();
        FakeResourceSet[] oldSets = h.WaterSets;
        using var replacementAtlas = new FakeTexture(1, 1, 1, 1, GpuPixelFormat.R32Float);
        using var replacementSampler = new FakeSampler();
        using var replacementFrame = new FakeBuffer(1024);
        WaterColumnBindings bindings = h.Water.ColumnShadows;
        bindings.SetShadows(changed == "atlas" ? replacementAtlas : bindings.ShadowAtlas,
            changed == "sampler" ? replacementSampler : bindings.ShadowSampler,
            changed == "frame" ? replacementFrame : bindings.FrameUniforms);
        Assert.Throws<InvalidOperationException>(h.VerifyPrepared);
        var frame = new FramePrepare(h.Scene.Post.Water, h.Scene.RelativeWaterPlanes(), h.Scene.EffectTimeSeconds);
        h.Water.PrepareOpticsFrame(h.Targets, frame);
        h.VerifyPrepared();
        FakeResourceSet[] replacements = h.WaterSets.Except(oldSets).ToArray();
        Assert.Equal(2, replacements.Length);
        int index = changed == "atlas" ? 12 : changed == "sampler" ? 13 : 14;
        Assert.All(replacements, set => Assert.NotSame(oldSets[0].Resources[index], set.Resources[index]));
    }
}
