using System;
using System.Linq;
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

public sealed class IndirectFrameTailTests
{
    [Fact]
    public void LitFragments_DeclareTheIndirectTailAfterTheInteriorCut()
        => VerifyLitFragmentTail();

    [Fact]
    public void LitFragments_ReadIndirectDiffuse()
        => VerifyLitFragmentReads();

    const int TailOffset = 1376;
    const int TailBytes = 160;

    static readonly string[] LitFragments =
    [
        ShaderSources.ModelFrag, ShaderSources.ModelDissolveFrag,
        ShaderSources.SkinnedModelFrag, ShaderSources.SkinnedModelDissolveFrag,
        ShaderSources.SplatFrag, ShaderSources.TileGroundFrag,
    ];

    internal static void VerifyLitFragmentTail()
    {
        string[] expected = MotionUboLayoutTests.Members(ShaderSources.ModelVert, "uniform U {")
            .Concat(["vec4 IndirectParams", "vec4 IrradianceSH[9]"]).ToArray();
        Assert.Equal("vec4 InteriorCutWindow", expected[^4]);
        Assert.Equal("vec4 InteriorCutHeights", expected[^3]);
        foreach (string fragment in LitFragments)
            Assert.Equal(expected, MotionUboLayoutTests.Members(fragment, "uniform U {"));
    }

    internal static void VerifyLitFragmentReads()
    {
        foreach (string fragment in LitFragments)
        {
            string normal = fragment == ShaderSources.TileGroundFrag ? "Ngeo" : "N";
            Assert.Contains($"indirectDiffuse({normal}, vWorldPos) + diffuse", fragment);
            Assert.DoesNotContain("Ambient.rgb + diffuse", fragment);
        }
    }

    [Fact]
    public void UniformModeLeavesTheHeaderUnchangedAndTheTailZero()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);
        using var commands = new NullGpuCommandList();
        var settings = new PixelPostProcessSettings();
        Upload(model, commands, settings);
        byte[] prefix = model.FrameImage[..TailOffset].ToArray();
        Assert.Equal(TailOffset + TailBytes, model.FrameImage.Length);
        Assert.All(model.FrameImage[TailOffset..].ToArray(), value => Assert.Equal((byte)0, value));

        settings.AmbientIrradiance = DistinctCoefficients();
        settings.IndirectDiffuse = IndirectDiffuseMode.Directional;
        Upload(model, commands, settings);
        Assert.True(prefix.AsSpan().SequenceEqual(model.FrameImage[..TailOffset]));
        Assert.Equal(1f, MemoryMarshal.Read<Vector4>(model.FrameImage[TailOffset..]).X);

        settings.IndirectDiffuse = IndirectDiffuseMode.Uniform;
        Upload(model, commands, settings);
        Assert.True(prefix.AsSpan().SequenceEqual(model.FrameImage[..TailOffset]));
        Assert.All(model.FrameImage[TailOffset..].ToArray(), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void DirectionalModeUploadsTheCoefficients()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);
        using var commands = new NullGpuCommandList();
        var settings = new PixelPostProcessSettings
        {
            IndirectDiffuse = IndirectDiffuseMode.Directional,
            AmbientIrradiance = DistinctCoefficients(),
        };
        Upload(model, commands, settings);
        Assert.Equal(TailOffset + TailBytes, model.FrameImage.Length);
        ReadOnlySpan<Vector4> tail = MemoryMarshal.Cast<byte, Vector4>(model.FrameImage[TailOffset..]);
        Assert.Equal(new Vector4(1, 0, 0, 0), tail[0]);
        for (int i = 0; i < IrradianceSH.CoefficientCount; i++)
            Assert.Equal(new Vector4(settings.AmbientIrradiance.Coefficient(i), 0), tail[i + 1]);
    }

    [Fact]
    public void DirectionalWithoutAtmosphereFallsBackToUniformAmbient()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);
        using var commands = new NullGpuCommandList();
        var settings = new PixelPostProcessSettings
        {
            IndirectDiffuse = IndirectDiffuseMode.Directional,
            AmbientColor = new Color(0.25f, 0.5f, 0.75f, 1),
        };
        settings.Atmosphere.Enabled = false;
        Upload(model, commands, settings);
        Assert.Equal(TailOffset + TailBytes, model.FrameImage.Length);
        ReadOnlySpan<Vector4> tail = MemoryMarshal.Cast<byte, Vector4>(model.FrameImage[TailOffset..]);
        Assert.Equal(new Vector4(1, 0, 0, 0), tail[0]);
        Assert.Equal(new Vector4(IrradianceSH.Uniform(new Vector3(0.25f, 0.5f, 0.75f)).Coefficient(0), 0), tail[1]);
        for (int i = 2; i < tail.Length; i++) Assert.Equal(Vector4.Zero, tail[i]);
    }

    [Fact]
    public void DisablingTheAtmosphereReturnsToThePaletteAmbient()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);
        using var commands = new NullGpuCommandList();
        var settings = new PixelPostProcessSettings { IndirectDiffuse = IndirectDiffuseMode.Directional };
        settings.Atmosphere.Enabled = true;
        var cycle = new SunCycleSettings();
        SunCycle.Apply(SunCycle.Evaluate(.5f, cycle), settings);
        Upload(model, commands, settings);
        Assert.NotEqual(IrradianceSH.Zero, settings.AmbientIrradiance);

        settings.Atmosphere.Enabled = false;
        var palette = SunCycle.Evaluate(0, cycle);
        SunCycle.Apply(palette, settings);

        Assert.Equal(palette.AmbientColor, settings.AmbientColor);
        Assert.Equal(palette.FillLightColor, settings.FillLightColor);
        Assert.Equal(IrradianceSH.Zero, settings.AmbientIrradiance);
        Upload(model, commands, settings);
        ReadOnlySpan<Vector4> tail = MemoryMarshal.Cast<byte, Vector4>(model.FrameImage[TailOffset..]);
        Assert.Equal(new Vector4(1, 0, 0, 0), tail[0]);
        var ambient = new Vector3(palette.AmbientColor.R, palette.AmbientColor.G, palette.AmbientColor.B);
        Assert.Equal(new Vector4(IrradianceSH.Uniform(ambient).Coefficient(0), 0), tail[1]);
        for (int i = 2; i < tail.Length; i++) Assert.Equal(Vector4.Zero, tail[i]);
    }

    [Fact]
    public void CustomIrradianceSetAfterPaletteApplyIsUploaded()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);
        using var commands = new NullGpuCommandList();
        var settings = new PixelPostProcessSettings { IndirectDiffuse = IndirectDiffuseMode.Directional };
        settings.Atmosphere.Enabled = false;
        SunCycle.Apply(SunCycle.Evaluate(0, new SunCycleSettings()), settings);
        var custom = IrradianceSH.SkyAndGround(new Vector3(.4f, .6f, .8f), new Vector3(.2f, .1f, .05f));
        settings.AmbientIrradiance = custom;

        Upload(model, commands, settings);

        ReadOnlySpan<Vector4> tail = MemoryMarshal.Cast<byte, Vector4>(model.FrameImage[TailOffset..]);
        Assert.Equal(new Vector4(1, 0, 0, 0), tail[0]);
        for (int i = 0; i < IrradianceSH.CoefficientCount; i++)
            Assert.Equal(new Vector4(custom.Coefficient(i), 0), tail[i + 1]);
    }

    [Fact]
    public void DirectionalWithAtmospherePreservesZeroIrradiance()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer model = NewRenderer(device);
        using var commands = new NullGpuCommandList();
        var settings = new PixelPostProcessSettings { IndirectDiffuse = IndirectDiffuseMode.Directional };
        settings.Atmosphere.Enabled = true;
        Upload(model, commands, settings);
        Assert.Equal(TailOffset + TailBytes, model.FrameImage.Length);
        ReadOnlySpan<Vector4> tail = MemoryMarshal.Cast<byte, Vector4>(model.FrameImage[TailOffset..]);
        Assert.Equal(new Vector4(1, 0, 0, 0), tail[0]);
        for (int i = 1; i < tail.Length; i++) Assert.Equal(Vector4.Zero, tail[i]);
    }

    static IrradianceSH DistinctCoefficients() => new(
    [
        new(1, 2, 3), new(-4, 5, -6), new(7, -8, 9),
        new(10, 11, 12), new(-13, 14, 15), new(16, -17, 18),
        new(19, 20, -21), new(22, -23, 24), new(-25, 26, 27),
    ]);

    static void Upload(ModelRenderer model, NullGpuCommandList commands, PixelPostProcessSettings settings)
        => model.SetFrameUniforms(commands, Matrix4x4.Identity, Vector3.Zero, settings,
            ReadOnlySpan<ModelRenderer.PointLightData>.Empty);

    static ModelRenderer NewRenderer(FakeGpuDevice device)
    {
        IGpuTexture target = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        IGpuFramebuffer framebuffer = device.Factory.CreateFramebuffer(null, target);
        return new ModelRenderer(device, framebuffer.Outputs, 64, 1);
    }
}
