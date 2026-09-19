using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The point-shadow TAIL of the shared frame UBO: where it sits, how big it is, what the two setters write into
/// it, and that every shader declaring the block declares the tail too.
/// <para>
/// THE DEFAULT STATE IS THE LOAD-BEARING CASE. A slot below zero is what makes the receiver skip the whole
/// sample, so a renderer nobody has called <see cref="ModelRenderer.SetPointShadowUniforms"/> on must already
/// read -1 in all sixteen slots. Zero-filled bytes would read as slot 0 and every point light in the engine
/// would start sampling an atlas that may not even exist, which is the one way this feature can break a scene
/// that never asked for it.
/// </para>
/// </summary>
public sealed class PointShadowUboLayoutTests
{
    // The tail rides AFTER the render origin, so every offset that existed before it is unchanged: a shader
    // reading ShadowMat or RenderOrigin finds them exactly where it always did.
    const uint OldUboBytes = 1008;

    static ModelRenderer NewRenderer(FakeGpuDevice device)
    {
        IGpuTexture target = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        IGpuFramebuffer framebuffer = device.Factory.CreateFramebuffer(null, target);
        return new ModelRenderer(device, framebuffer.Outputs, 64, 1);
    }

    /// <summary>One <c>PointShadowParams</c> entry, read out of the packed frame image the way the GPU reads it.</summary>
    static (float Slot, float Bias, float SlopeBias, float W) Slot(ModelRenderer model, int light)
    {
        ReadOnlySpan<float> f = MemoryMarshal.Cast<byte, float>(model.FrameImage);
        int i = (int)(ModelRenderer.PointShadowTailOffset / 4) + light * 4;
        return (f[i], f[i + 1], f[i + 2], f[i + 3]);
    }

    static (float X, float Y, float Z, float W) AtlasParams(ModelRenderer model)
    {
        ReadOnlySpan<float> f = MemoryMarshal.Cast<byte, float>(model.FrameImage);
        int i = (int)(ModelRenderer.PointShadowTailOffset / 4) + ModelRenderer.MaxPointLights * 4;
        return (f[i], f[i + 1], f[i + 2], f[i + 3]);
    }

    /// <summary>The filter vec4 at the end of the point-shadow compatibility tail: (mode, lightSizeMetres,
    /// maxPenumbraTexels, faceResolution). Cluster selection data follows it.</summary>
    static (float Mode, float LightSize, float MaxTexels, float FaceRes) FilterParams(ModelRenderer model)
    {
        ReadOnlySpan<float> f = MemoryMarshal.Cast<byte, float>(model.FrameImage);
        int i = (int)(ModelRenderer.PointShadowTailOffset / 4) + ModelRenderer.MaxPointLights * 4 + 4;
        return (f[i], f[i + 1], f[i + 2], f[i + 3]);
    }

    [Fact]
    public void ThePointShadowTailKeepsItsSizeAndTheClusterTailFollowsIt()
    {
        Assert.Equal((uint)(ModelRenderer.MaxPointLights * 16 + 32), ModelRenderer.PointShadowTailBytes);
        Assert.Equal(288u, ModelRenderer.PointShadowTailBytes);
        Assert.Equal(OldUboBytes + ModelRenderer.PointShadowTailBytes, ModelRenderer.ClusterTailOffset);
        Assert.Equal(1296u, ModelRenderer.ClusterTailOffset);
        Assert.Equal(1328u, ModelRenderer.UboBytes);
    }

    [Fact]
    public void EveryOffsetTheBlockAlreadyHadIsUnchanged()
    {
        Assert.Equal(176u, ModelRenderer.HeaderBytes);
        Assert.Equal(688u, ModelRenderer.ShadowTailOffset);
        Assert.Equal(992u, ModelRenderer.RenderOriginOffset);
        Assert.Equal(ModelRenderer.RenderOriginOffset + ModelRenderer.RenderOriginBytes,
            ModelRenderer.PointShadowTailOffset);
        Assert.Equal(OldUboBytes, ModelRenderer.PointShadowTailOffset);
    }

    [Fact]
    public void AFreshRendererAlreadyReadsMinusOneInEverySlot()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);

        for (int i = 0; i < ModelRenderer.MaxPointLights; i++)
            Assert.Equal(-1f, Slot(model, i).Slot);
        Assert.Equal((0f, 0f, 0f, 0f), AtlasParams(model));
        Assert.Equal((0f, 0f, 0f, 0f), FilterParams(model));
    }

    [Fact]
    public void ClearPointShadowUniforms_PutsEverySlotBackToMinusOne()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);
        model.SetPointShadowUniforms([0, 1, 2], 0.01f, 0.02f, 256, 8, PointShadowFilter.Soft, 0.15f, 6f);

        model.ClearPointShadowUniforms();

        for (int i = 0; i < ModelRenderer.MaxPointLights; i++)
            Assert.Equal((-1f, 0f, 0f, 0f), Slot(model, i));
        Assert.Equal((0f, 0f, 0f, 0f), AtlasParams(model));
        Assert.Equal((0f, 0f, 0f, 0f), FilterParams(model));
    }

    [Fact]
    public void SetPointShadowUniforms_WritesTheSlotsInLightOrderAndMinusOneForTheRest()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);

        model.SetPointShadowUniforms([3, -1, 0], 0.01f, 0.02f, 256, 8, PointShadowFilter.Soft, 0.15f, 6f);

        Assert.Equal((3f, 0.01f, 0.02f, 0f), Slot(model, 0));
        Assert.Equal((-1f, 0.01f, 0.02f, 0f), Slot(model, 1));
        Assert.Equal((0f, 0.01f, 0.02f, 0f), Slot(model, 2));
        for (int i = 3; i < ModelRenderer.MaxPointLights; i++)
            Assert.Equal(-1f, Slot(model, i).Slot);
    }

    [Fact]
    public void SetPointShadowUniforms_WritesTheAtlasTexelStepsAndItsShape()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);

        model.SetPointShadowUniforms([3, -1, 0], 0.01f, 0.02f, 256, 8, PointShadowFilter.Soft, 0.15f, 6f);

        Assert.Equal((1f / 1536f, 1f / 2048f, 8f, 6f), AtlasParams(model));
    }

    /// <summary>The filter vec4 carries the MODE the receiver branches on, the two knobs its penumbra arithmetic
    /// reads, and the face resolution it converts a penumbra into texels with. The face resolution rides here
    /// rather than being derived from the atlas texel steps because those are ATLAS-wide, and a receiver dividing
    /// one by the other to recover a face would be re-deriving a number the host already knows.</summary>
    [Fact]
    public void SetPointShadowUniforms_WritesTheFilterModeAndItsTwoKnobs()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);

        model.SetPointShadowUniforms([0], 0.01f, 0.02f, 256, 8, PointShadowFilter.Soft, 0.15f, 6f);
        Assert.Equal((1f, 0.15f, 6f, 256f), FilterParams(model));

        model.SetPointShadowUniforms([0], 0.01f, 0.02f, 384, 12, PointShadowFilter.Hard, 0.5f, 16f);
        Assert.Equal((0f, 0.5f, 16f, 384f), FilterParams(model));
    }

    [Fact]
    public void SetPointShadowUniforms_IgnoresSlotsPastTheLightArray()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);
        var slots = new int[ModelRenderer.MaxPointLights + 4];
        Array.Fill(slots, 2);
        model.EnsurePointShadowSlotCapacity(slots.Length);

        model.SetPointShadowUniforms(slots, 0.5f, 0.25f, 64, 1, PointShadowFilter.Hard, 1f, 16f);

        for (int i = 0; i < ModelRenderer.MaxPointLights; i++)
            Assert.Equal(2f, Slot(model, i).Slot);
        Assert.Equal((1f / 384f, 1f / 64f, 1f, 6f), AtlasParams(model));
    }

    [Fact]
    public void TheTailIsWrittenAtItsOwnOffsetAndDisturbsNothingBeforeIt()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);
        byte[] before = model.FrameImage.ToArray();

        model.SetPointShadowUniforms([5], 0.01f, 0.02f, 128, 4, PointShadowFilter.Soft, 0.2f, 4f);

        ReadOnlySpan<byte> after = model.FrameImage;
        Assert.Equal((int)ModelRenderer.UboBytes, after.Length);
        Assert.True(before.AsSpan(0, (int)ModelRenderer.PointShadowTailOffset)
            .SequenceEqual(after[..(int)ModelRenderer.PointShadowTailOffset]));
    }

    // The shared frame block's own marker. Every stage that declares the block declares this array, and no other
    // uniform block in the tree carries it, so DISCOVERING the shaders beats listing them: FoliageVert shares the
    // block with ModelFrag and reads none of it, which is exactly the kind of member a hand-written list drops.
    const string FrameBlockMarker = "vec4 PointPosRadius[16];";

    static IEnumerable<(string Name, string Source)> ShadersDeclaringTheFrameBlock() =>
        typeof(ShaderSources)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (f.Name, Source: (string)f.GetRawConstantValue()!))
            .Where(s => s.Source.Contains(FrameBlockMarker, StringComparison.Ordinal))
            .OrderBy(s => s.Name, StringComparer.Ordinal);

    [Fact]
    public void EveryShaderThatDeclaresTheBlockDeclaresTheTail()
    {
        // A stage that misses the tail still compiles and simply disagrees with every other stage about the size
        // of the block, which is the same trap the render origin left when it was appended (see UboLayoutTests).
        (string Name, string Source)[] shaders = ShadersDeclaringTheFrameBlock().ToArray();

        // A reflection walk that finds nothing passes every assertion under it, so pin the floor: ten model,
        // skinned, splat and tile-ground stages plus FoliageVert.
        Assert.True(shaders.Length >= 11,
            $"only {shaders.Length} shader sources carry the frame block. The discovery above has stopped "
            + "finding them, so this test is asserting nothing.");

        foreach ((string name, string src) in shaders)
        {
            int origin = src.IndexOf("vec4 RenderOrigin;", StringComparison.Ordinal);
            int slots = src.IndexOf("vec4 PointShadowParams[16];", StringComparison.Ordinal);
            int atlas = src.IndexOf("vec4 PointShadowAtlas;", StringComparison.Ordinal);
            int filter = src.IndexOf("vec4 PointShadowFilter;", StringComparison.Ordinal);
            int clusterDepth = src.IndexOf("vec4 ClusterDepth;", StringComparison.Ordinal);
            int clusterCamera = src.IndexOf("vec4 ClusterCamera;", StringComparison.Ordinal);
            Assert.True(origin >= 0, $"{name}: the frame block is missing the render origin.");
            Assert.True(slots >= 0, $"{name}: the frame block is missing PointShadowParams.");
            Assert.True(atlas >= 0, $"{name}: the frame block is missing PointShadowAtlas.");
            Assert.True(filter >= 0, $"{name}: the frame block is missing PointShadowFilter.");
            Assert.True(clusterDepth >= 0, $"{name}: the frame block is missing ClusterDepth.");
            Assert.True(clusterCamera >= 0, $"{name}: the frame block is missing ClusterCamera.");
            Assert.True(origin < slots,
                $"{name}: the point-shadow tail must follow the render origin, matching the C# offsets.");
            Assert.True(slots < atlas, $"{name}: PointShadowAtlas follows PointShadowParams.");
            Assert.True(atlas < filter, $"{name}: PointShadowFilter follows PointShadowAtlas.");
            Assert.True(filter < clusterDepth, $"{name}: ClusterDepth follows the compatibility tails.");
            Assert.True(clusterDepth < clusterCamera, $"{name}: ClusterCamera is the last member of the block.");
        }
    }

    [Fact]
    public void TheFoliageVertexSharesTheBlockWithTheModelFragment()
    {
        // Named on its own because it is the one stage that declares the block and reads NONE of it: the foliage
        // program is FoliageVert plus ModelFrag, so the fragment's tail obliges the vertex to carry it too.
        Assert.Contains(FrameBlockMarker, ShaderSources.FoliageVert, StringComparison.Ordinal);
        Assert.Contains("vec4 PointShadowParams[16];", ShaderSources.FoliageVert, StringComparison.Ordinal);
        Assert.Contains("vec4 PointShadowAtlas;", ShaderSources.FoliageVert, StringComparison.Ordinal);
        Assert.Contains("vec4 PointShadowFilter;", ShaderSources.FoliageVert, StringComparison.Ordinal);
        Assert.Contains("vec4 ClusterDepth;", ShaderSources.FoliageVert, StringComparison.Ordinal);
        Assert.Contains("vec4 ClusterCamera;", ShaderSources.FoliageVert, StringComparison.Ordinal);
    }
}
