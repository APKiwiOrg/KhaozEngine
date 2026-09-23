using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedPointShadowShaderTests
{
    [Fact]
    public void PointSkinnedVertexUsesIndependentFaceCasterAndPaletteIndices()
    {
        string source = ShaderSources.PointShadowSkinnedDissolveVert;
        Assert.Contains("layout(set=0, binding=0) uniform U", source, StringComparison.Ordinal);
        Assert.Contains("layout(set=1, binding=0) uniform Caster", source, StringComparison.Ordinal);
        Assert.Contains("layout(set=2, binding=0) uniform Palette", source, StringComparison.Ordinal);
        Assert.Contains("layout(location=4) in vec4 BoneIndices;", source, StringComparison.Ordinal);
        Assert.Contains("layout(location=5) in vec4 BoneWeights;", source, StringComparison.Ordinal);
        Assert.Contains("layout(location=6) in vec4 Tangent;", source, StringComparison.Ordinal);
        Assert.Contains("world.x += sink * 1e-30;", source, StringComparison.Ordinal);
        Assert.Contains("vNoisePos = (world.xyz + Noise.yzw) * Noise.x;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PointSkinnedCasterHeaderSlotsAre256Bytes()
    {
        Assert.Equal(256u, PointShadowRenderer.SkinnedCasterSlotBytes);
    }

    [Fact]
    public void CpuSkinnedPointCasterReusesUploadedStreamsAndPreservesBaseVertex()
    {
        using var device = new FakeGpuDevice();
        using var palette = new SkinnedBonePalette(device);
        using PointShadowAtlas atlas = Assert.IsType<PointShadowAtlas>(
            PointShadowAtlas.TryCreate(device, 32, 2));
        using var renderer = new PointShadowRenderer(device, palette.Layout);
        using IGpuCommandList inner = device.Factory.CreateCommandList();
        using var commands = new RecordingGpuCommandList(inner);
        using var deformed = new FakeBuffer(4096);
        using var instances = new FakeBuffer(512);
        using var indices = new FakeBuffer(128);
        commands.Begin();
        renderer.BeginPass(commands, atlas);
        renderer.BeginFace(commands, atlas, 0, 0, 0, ShadowCastKind.Opaque);

        renderer.DrawCpuSkinnedCaster(commands, deformed, instances, indices, 12,
            GpuIndexFormat.UInt16, 17, 2);
        commands.End();

        Assert.Empty(commands.Uploads);
        RecordingGpuCommandList.IndexedDraw draw = Assert.Single(commands.IndexedDraws);
        Assert.Same(deformed, draw.VertexBuffer);
        Assert.Same(indices, draw.IndexBuffer);
        Assert.Equal(17, draw.VertexOffset);
        Assert.Equal(12u, draw.IndexCount);
    }

    [Fact]
    public void CasterHeaderRingUploadsWholeBufferWithDistinctModelAndDissolveSlots()
    {
        using var device = new FakeGpuDevice();
        using var palette = new SkinnedBonePalette(device);
        using var renderer = new PointShadowRenderer(device, palette.Layout);
        renderer.EnsureSkinnedCasterCapacity(2);
        Matrix4x4 moved = Matrix4x4.CreateTranslation(3f, 4f, 5f);
        renderer.PackSkinnedCaster(0, Matrix4x4.Identity, 0f, 0f);
        renderer.PackSkinnedCaster(1, moved, 0.375f, 1f);
        using IGpuCommandList inner = device.Factory.CreateCommandList();
        using var commands = new RecordingGpuCommandList(inner) { CapturePayloads = true };
        commands.Begin();

        renderer.UploadSkinnedCasters(commands);
        commands.End();

        RecordingGpuCommandList.Upload upload = Assert.Single(commands.Uploads);
        Assert.True(upload.IsWholeBuffer);
        Assert.Equal(512u, upload.Bytes);
        byte[] bytes = Assert.IsType<byte[]>(upload.Data);
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 0));
        Assert.Equal(3f, BitConverter.ToSingle(bytes, 256 + 48));
        Assert.Equal(4f, BitConverter.ToSingle(bytes, 256 + 52));
        Assert.Equal(5f, BitConverter.ToSingle(bytes, 256 + 56));
        Assert.Equal(0.375f, BitConverter.ToSingle(bytes, 256 + 64));
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 256 + 68));
    }

    [Fact]
    public void BaseOnlyBranchPrecedesEveryTransientRead()
    {
        string source = ShaderSources.LightingCommonGlsl;
        int function = source.IndexOf("float samplePointShadowCombined(", StringComparison.Ordinal);
        Assert.True(function >= 0);
        int gate = source.IndexOf("if (params.w < 0.0)", function, StringComparison.Ordinal);
        int baseReturn = source.IndexOf("return samplePointShadow(baseAtlas, samp", function, StringComparison.Ordinal);
        int combinedCall = source.IndexOf("samplePointShadowHardCombined(", function, StringComparison.Ordinal);
        Assert.True(function < gate && gate < baseReturn && baseReturn < combinedCall);
        int bodyStart = source.IndexOf('{', function) + 1;
        Assert.True(bodyStart > function && bodyStart < gate);
        string beforeGate = source.Substring(bodyStart, gate - bodyStart);
        Assert.DoesNotContain("transientAtlas", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("PointShadowTransientAtlas", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("PointShadowTransientMap", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("samplePointShadowHardCombined", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("samplePointShadowSoftCombined", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("pointShadowCombinedDepthAt", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("pointShadowDepthAtFaceUv", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("textureLod(", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("texture(", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("texelFetch(", beforeGate, StringComparison.Ordinal);
    }

    [Fact]
    public void HardBlockerAndFilterTapsTakeTheNearerStoredDistance()
    {
        string source = ShaderSources.LightingCommonGlsl;
        Assert.Contains("return min(baseStored, transientStored);", source, StringComparison.Ordinal);
        Assert.Contains("samplePointShadowHardCombined", source, StringComparison.Ordinal);
        Assert.Contains("pointShadowBlockerSearchCombined", source, StringComparison.Ordinal);
        Assert.Contains("pointShadowFilterDiscCombined", source, StringComparison.Ordinal);
    }
}
