using System;
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

public sealed class WaterColumnPackingTests
{
    [Fact]
    public void ExistingGlintSkyAndFftOffsetsStayFixed()
    {
        foreach ((string member, int offset) in new[]
                 {
                     (nameof(WaterRenderer.WaterUbo.ShoreGlint), 240),
                     (nameof(WaterRenderer.WaterUbo.SkyHorizon), 272), (nameof(WaterRenderer.WaterUbo.SkyZenith), 288),
                     (nameof(WaterRenderer.WaterUbo.SkyParams), 304), (nameof(WaterRenderer.WaterUbo.ReflectGlint), 320),
                     (nameof(WaterRenderer.WaterUbo.FftParams), 448), (nameof(WaterRenderer.WaterUbo.FftTiles), 464),
                     (nameof(WaterRenderer.WaterUbo.FftVariance), 480), (nameof(WaterRenderer.WaterUbo.FftFocus), 496),
                     (nameof(WaterRenderer.WaterUbo.FftRotCos), 512), (nameof(WaterRenderer.WaterUbo.FftRotSin), 528),
                     (nameof(WaterRenderer.WaterUbo.FftSector), 544), (nameof(WaterRenderer.WaterUbo.FftWave), 560),
                     (nameof(WaterRenderer.WaterUbo.SkyGround), 656), (nameof(WaterRenderer.WaterUbo.SkyDiscColor), 672),
                     (nameof(WaterRenderer.WaterUbo.SkyDiscDir), 800), (nameof(WaterRenderer.WaterUbo.SkyDiscHalo), 928),
                 })
            Assert.Equal(offset, Marshal.OffsetOf<WaterRenderer.WaterUbo>(member).ToInt32());
    }

    [Fact]
    public void ColumnTailIsZeroWhenDisabled()
    {
        WaterSettings settings = LegacySettings();
        settings.WaterColumnLight = false;
        settings.WaterColor = new Color(float.NaN, float.PositiveInfinity, -1f);
        settings.Clarity = float.NaN;

        byte[] disabledBytes = Bytes(Pack(settings));
        byte[] legacyPrefixBytes = LegacyPrefixBytes();

        Assert.Equal(new byte[48], disabledBytes.AsSpan(1088, 48).ToArray());
        Assert.Equal(legacyPrefixBytes, disabledBytes.AsSpan(0, 1088).ToArray());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(63u)]
    public void ColumnTailMatchesOpticsAndFitsSlot(uint phase)
    {
        var settings = new WaterSettings
        {
            WaterColumnLight = true,
            WaterColor = new Color(0.04f, 0.13f, 0.30f),
            Clarity = 2f,
        };
        WaterRenderer.WaterUbo u = Pack(settings);
        AssertCoefficients(u, settings, 0u);

        WaterColumnPacking.Pack(ref u, settings, phase);

        AssertCoefficients(u, settings, phase);
        Assert.Equal(1136, Bytes(u).Length);
        Assert.Equal(1280u, WaterRenderer.SlotBytes);
        Assert.Equal(0u, WaterRenderer.SlotBytes % 256u);
        Assert.True(WaterRenderer.PayloadBytes <= WaterRenderer.SlotBytes);
    }

    [Fact]
    public void DisabledRepackClearsAnExistingPayload()
    {
        WaterSettings settings = LegacySettings();
        settings.WaterColumnLight = true;
        WaterRenderer.WaterUbo u = Pack(settings);
        WaterColumnPacking.Pack(ref u, settings, 63u);
        AssertCoefficients(u, settings, 63u);
        settings.WaterColumnLight = false;
        settings.WaterColor = new Color(float.NaN, -1f, float.NegativeInfinity);
        settings.Clarity = -1f;

        WaterColumnPacking.Pack(ref u, settings, 63u);

        byte[] disabledBytes = Bytes(u);
        Assert.Equal(new byte[48], disabledBytes.AsSpan(1088, 48).ToArray());
        Assert.Equal(LegacyPrefixBytes(), disabledBytes.AsSpan(0, 1088).ToArray());
    }

    [Fact]
    public void EnabledThenDisabledSlotClearsTheEntireTail()
    {
        using var device = new FakeGpuDevice();
        using var resources = new RenderResources(device, 96, 64, false);
        using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        var look = new WaterLook { WaterColumnLight = true };
        WaterPlane[] planes = [new(0f, 0f, 0f, 5f, look: look)];
        var settings = new WaterSettings { SwellAmplitude = 0f };
        WaterTestFrames.Draw(renderer, commands, resources, planes, settings, Vector3.UnitY * 100f);
        RecordingGpuCommandList.Upload first = UniformUpload(commands);
        AssertCoefficients(ReadSlot(first, 0), settings, 0u);

        look.WaterColumnLight = false;
        WaterTestFrames.Draw(renderer, commands, resources, planes, settings, Vector3.UnitY * 100f);
        RecordingGpuCommandList.Upload disabled = UniformUpload(commands);

        Assert.Same(first.Buffer, disabled.Buffer);
        Assert.Single(commands.IndexedDraws);
        Assert.Equal(new byte[48], disabled.Data!.AsSpan(1088, 48).ToArray());
        Assert.Equal(first.Data!.AsSpan(0, 1088).ToArray(), disabled.Data.AsSpan(0, 1088).ToArray());
    }

    [Fact]
    public void MixedPlanesKeepTheirOwnColumnTails()
    {
        using var device = new FakeGpuDevice();
        using var resources = new RenderResources(device, 96, 64, false);
        using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        var scene = new WaterSettings { SwellAmplitude = 0f, WaterColumnLight = true };
        var second = new WaterLook { WaterColor = new Color(0.7f, 0.2f, 0.1f), Clarity = 5f };
        WaterPlane[] planes =
        [
            new(-30f, 0f, 0f, 5f),
            new(0f, 0f, 0f, 5f, look: new WaterLook
            {
                WaterColumnLight = false, WaterColor = new Color(float.NaN, -1f, 2f), Clarity = float.NaN,
            }),
            new(30f, 0f, 0f, 5f, look: second),
        ];

        WaterTestFrames.Draw(renderer, commands, resources, planes, scene, Vector3.UnitY * 100f);

        Assert.Equal(3, commands.IndexedDraws.Count);
        RecordingGpuCommandList.Upload upload = UniformUpload(commands);
        AssertCoefficients(ReadSlot(upload, 0), scene, 0u);
        Assert.Equal(new byte[48], upload.Data!.AsSpan(1280 + 1088, 48).ToArray());
        AssertCoefficients(ReadSlot(upload, 2), second.ResolveInto(new WaterSettings(), scene), 0u);
    }

    [Fact]
    public void ColumnTailFollowsOpticsInEveryWaterShader()
    {
        foreach (string shader in new[] { ShaderSources.WaterVert, ShaderSources.WaterClipmapVert,
                     ShaderSources.WaterFrag, ShaderSources.WaterTriangleFrag })
        {
            int previous = shader.IndexOf("vec4 OpticsParams;", StringComparison.Ordinal);
            Assert.True(previous >= 0);
            foreach (string member in new[] { "vec4 ColumnExtinction;", "vec4 ColumnScattering;", "vec4 ColumnAlbedo;" })
            {
                int next = shader.IndexOf(member, StringComparison.Ordinal);
                Assert.True(next > previous, $"Water UBO lost or reordered {member}");
                previous = next;
            }
        }
    }

    static RecordingGpuCommandList.Upload UniformUpload(RecordingGpuCommandList commands)
    {
        var upload = Assert.Single(commands.Uploads, u =>
            u.Buffer is FakeBuffer b && b.Usage.HasFlag(GpuBufferUsage.UniformBuffer));
        Assert.True(upload.IsWholeBuffer);
        Assert.Equal(0, upload.DrawsBefore);
        Assert.Equal(0, upload.FramebufferBindsBefore);
        return upload;
    }

    static WaterRenderer.WaterUbo ReadSlot(RecordingGpuCommandList.Upload upload, int slot)
        => MemoryMarshal.Read<WaterRenderer.WaterUbo>(upload.Data!.AsSpan(slot * 1280, 1136));

    static void AssertCoefficients(WaterRenderer.WaterUbo u, WaterSettings settings, uint phase)
    {
        WaterColumnCoefficients expected = WaterColumnOptics.Derive(settings.WaterColor, settings.Clarity);
        Assert.Equal(new Vector4(expected.Extinction, 1f), u.ColumnExtinction);
        Assert.Equal(new Vector4(expected.Scattering, 0f), u.ColumnScattering);
        Assert.Equal(new Vector4(expected.Albedo, phase), u.ColumnAlbedo);
    }

    static WaterRenderer.WaterUbo Pack(WaterSettings settings)
        => WaterRenderer.PackUbo(Matrix4x4.Identity, Matrix4x4.Identity, -Vector3.UnitY, Color.White,
            new Vector3(3f, 12f, -7f), settings, new SkySettings
            {
                SunEnabled = false, HorizonColor = new Color(0.1f, 0.2f, 0.3f),
                ZenithColor = new Color(0.4f, 0.5f, 0.6f),
            }, 1.25f);

    static byte[] Bytes(WaterRenderer.WaterUbo u)
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref u, 1)).ToArray();

    static WaterSettings LegacySettings() => new()
    {
        DeepColor = new Color(0.1f, 0.2f, 0.3f, 0.4f),
        ShallowColor = new Color(0.5f, 0.6f, 0.7f, 0.8f),
        HorizonColor = new Color(0.9f, 0.8f, 0.7f, 0.6f),
        WaveScale = 2f, WaveSpeed = 3f, NormalStrength = 4f,
        ShoreFadeDistance = 5f, GlintStrength = 6f, GlintExponent = 7f, Opacity = 0.75f,
        WaveWarpStrength = 8f, DetailFadeDistance = 9f, DistantDetailScale = 10f, ShallowDepth = 11f,
        SkyReflectionStrength = 0.2f, SkyReflectionSunStrength = 0.3f,
        GlintRoughness = 0.4f, GlintDistantRoughness = 0.5f,
        SwellAmplitude = 0.6f, SwellWavelength = 12f, SwellDirectionDegrees = 0f, SwellSpreadDegrees = 0f,
        SwellSteepness = 0.7f, SwellSpeed = 13f, SwellComponents = 3, SwellSeed = 14f,
        AbsorptionPerMetre = new Color(0.11f, 0.12f, 0.13f, 0f),
        FoamColor = new Color(0.8f, 0.7f, 0.6f, 0.5f),
        FoamStrength = 0.9f, FoamCrestCoverage = 0.4f, FoamShoreWidth = 2f, FoamPatternScale = 3f,
        RippleComponents = 4, RippleLacunarity = 2f, RippleGain = 0.5f, RippleSeed = 15f,
        FootprintSamples = 3f, VarianceToRoughness = 0.25f, ClipmapBandLimitSamples = 2f,
        SeaState = new WaterSeaState
        {
            OnshoreFocusPoint = new Vector2(16f, 17f), OnshoreFocusStrength = 0.5f,
            WindDirectionDegrees = 0f, CascadeRotationDegrees = Vector3.Zero,
            DomainWarpMetres = 18f, DomainWarpWavelengthMetres = 19f, OnshoreFocusSectors = 4,
        },
        ShoalingStrength = 0.3f, ShoalingDepthScale = 4f,
        SurfStrength = 0.2f, SurfBandWidth = 0.5f, SurfCrestBias = 0.6f,
        SurfTrailWidth = 2f, SurfAmplitudeCollapse = 0.7f,
    };

    // An independent pre-column packing fixture, with literal values for the settings above. Unassigned
    // fields are the inactive FFT/optics maps and absent sky discs, all zero in the legacy payload.
    static byte[] LegacyPrefixBytes()
    {
        Matrix4x4.Invert(Matrix4x4.Identity, out Matrix4x4 inverse);
        var legacy = new WaterRenderer.WaterUbo
        {
            ViewProj = Matrix4x4.Identity, InvViewProj = inverse,
            LightDir = new Vector4(-Vector3.UnitY, 0f), LightColor = Vector4.One,
            CameraPos = new Vector4(3f, 12f, -7f, 0f),
            DeepColor = new Vector4(0.1f, 0.2f, 0.3f, 0.4f),
            ShallowColor = new Vector4(0.5f, 0.6f, 0.7f, 0.8f),
            HorizonColor = new Vector4(0.9f, 0.8f, 0.7f, 0.6f),
            WaveParams = new Vector4(2f, 3f, 4f, 1.25f),
            ShoreGlint = new Vector4(5f, 6f, 7f, 0.75f), DetailParams = new Vector4(8f, 9f, 10f, 11f),
            SkyHorizon = new Vector4(0.1f, 0.2f, 0.3f, 1f), SkyZenith = new Vector4(0.4f, 0.5f, 0.6f, 1f),
            ReflectGlint = new Vector4(0.2f, 0.3f, 0.4f, 0.5f),
            SwellParams = new Vector4(0.6f, 12f, 0f, 0f), SwellShape = new Vector4(0.7f, 13f, 3f, 14f),
            Absorption = new Vector4(0.11f, 0.12f, 0.13f, 0f),
            FoamColor = new Vector4(0.8f, 0.7f, 0.6f, 0.5f), FoamParams = new Vector4(0.9f, 0.4f, 2f, 3f),
            RippleSpectrum = new Vector4(4f, 2f, 0.5f, 15f), FootprintParams = new Vector4(3f, 0.25f, 2f, 0f),
            FftFocus = new Vector4(16f, 17f, 0.5f, 0f), FftRotCos = new Vector4(1f, 1f, 1f, 18f),
            FftRotSin = new Vector4(0f, 0f, 0f, 19f),
            FftSector = new Vector4(4f, MathF.Cos(MathF.PI / 2f), MathF.Sin(MathF.PI / 2f), 0f),
            BathyParams = new Vector4(0f, 0.3f, 4f, 0f), SurfParams = new Vector4(0.2f, 0f, 0.5f, 0.6f),
            SurfShape = new Vector4(2f, 0.7f, 0f, 0f), SkyGround = new Vector4(0f, 0f, 0f, -1f),
        };
        return Bytes(legacy).AsSpan(0, 1088).ToArray();
    }
}
