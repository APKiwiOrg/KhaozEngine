using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using FoliageUniforms = KhaozEngine.Render3D.Rendering.ModelRenderer.FoliageUniforms;

namespace KhaozEngine.Tests.Render3D;

public sealed class FoliageUniformsTests
{
    [Fact]
    public void TheShaderBlockMirrorsTheStructWithTheWindFadeLast()
    {
        string source = ShaderSources.FoliageVert;
        int start = source.IndexOf("uniform Foliage {", StringComparison.Ordinal);
        int end = source.IndexOf("};", start, StringComparison.Ordinal);
        string[] members = source[(start + "uniform Foliage {".Length)..end]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(["vec4 FocusRadius", "vec4 Density", "vec4 FadeWind", "vec4 WindTime", "vec4 Interactors[4]",
            "vec4 Strengths", "vec4 WindFade"], members);
        // Every std140 member is a vec4 or a vec4 array, so each one advances exactly 16 bytes per element.
        Assert.Equal(144, (int)Marshal.OffsetOf<FoliageUniforms>(nameof(FoliageUniforms.WindFade)));
        Assert.Equal(128, (int)Marshal.OffsetOf<FoliageUniforms>(nameof(FoliageUniforms.Strengths)));
        Assert.Equal(160, Marshal.SizeOf<FoliageUniforms>());
        Assert.Equal(160u, FoliageUniforms.SizeInBytes);
        Assert.True(FoliageUniforms.SizeInBytes <= FoliageUniforms.SlotBytes);
    }

    [Fact]
    public void BuildPacksTheFadeHeightAndLeavesTheScaleForTheUpload()
    {
        var settings = new FoliageRenderSettings { WindStrength = .4f, WindFadeBladePixels = 6.5f };

        FoliageUniforms data = FoliageUniforms.Build(Vector3.Zero, settings, ReadOnlySpan<FoliageInteractor>.Empty, 2f);

        Assert.Equal(new Vector4(6.5f, 0f, 0f, 0f), data.WindFade);
        Assert.Equal(.4f, data.FadeWind.Y);
    }

    [Fact]
    public void TheDefaultSettingsPackAZeroFadeHeight()
    {
        FoliageUniforms data = FoliageUniforms.Build(Vector3.Zero, new FoliageRenderSettings(),
            ReadOnlySpan<FoliageInteractor>.Empty, 0f);

        Assert.Equal(Vector4.Zero, data.WindFade);
    }

    [Fact]
    public void PerspectiveScaleIsMetresPerInternalPixelAtUnitDepth()
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, .1f, 500f);
        const int Height = 900;

        float scale = FoliageUniforms.MetresPerPixel(projection, Height);

        Assert.Equal(2f * MathF.Tan(MathF.PI / 6f) / Height, scale, 1e-7f);
        // A one metre upright segment at 30 m must cover 1 / (30 * scale) internal pixels.
        float pixels = ProjectedPixels(projection, Height, depth: 30f, metres: 1f);
        Assert.Equal(1f / (30f * scale), pixels, .001f);
    }

    [Fact]
    public void OrthographicScaleIsViewHeightOverRenderHeight()
    {
        Matrix4x4 projection = Matrix4x4.CreateOrthographic(32f, 18f, .1f, 100f);

        float scale = FoliageUniforms.MetresPerPixel(projection, 900);

        Assert.Equal(18f / 900f, scale, 1e-7f);
        Assert.Equal(1f / scale, ProjectedPixels(projection, 900, depth: 40f, metres: 1f), .001f);
    }

    [Fact]
    public void AYFlippedProjectionKeepsAPositiveScale()
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(1f, 1.5f, .1f, 100f) *
                               Matrix4x4.CreateScale(1f, -1f, 1f);

        Assert.Equal(FoliageUniforms.MetresPerPixel(
            Matrix4x4.CreatePerspectiveFieldOfView(1f, 1.5f, .1f, 100f), 720),
            FoliageUniforms.MetresPerPixel(projection, 720));
    }

    [Theory]
    [InlineData(0f, 900)]
    [InlineData(1f, 0)]
    [InlineData(float.NaN, 900)]
    [InlineData(float.PositiveInfinity, 900)]
    public void DegenerateProjectionsOrTargetsYieldNoScale(float m22, int height)
    {
        Matrix4x4 projection = Matrix4x4.Identity;
        projection.M22 = m22;

        Assert.Equal(0f, FoliageUniforms.MetresPerPixel(projection, height));
    }

    [Fact]
    public void ApplyingTheScaleWritesEverySlotAndKeepsEverythingElse()
    {
        FoliageUniforms[] slots =
        [
            FoliageUniforms.Build(Vector3.One, new FoliageRenderSettings { WindFadeBladePixels = 3f },
                ReadOnlySpan<FoliageInteractor>.Empty, 1f),
            FoliageUniforms.Build(Vector3.Zero, new FoliageRenderSettings(), ReadOnlySpan<FoliageInteractor>.Empty, 1f),
            FoliageUniforms.Build(Vector3.UnitX, new FoliageRenderSettings { WindFadeBladePixels = 9f },
                [new FoliageInteractor(Vector3.One, 2f, .5f)], 4f),
        ];
        FoliageUniforms[] before = slots.ToArray();

        FoliageUniforms.ApplyPixelScale(slots, .0125f);

        for (int i = 0; i < slots.Length; i++)
        {
            Assert.Equal(before[i].WindFade with { Y = .0125f }, slots[i].WindFade);
            FoliageUniforms restored = slots[i];
            restored.WindFade = before[i].WindFade;
            Assert.Equal(before[i], restored);
        }
    }

    static float ProjectedPixels(Matrix4x4 projection, int height, float depth, float metres)
    {
        Vector4 root = Vector4.Transform(new Vector4(0f, 0f, -depth, 1f), projection);
        Vector4 tip = Vector4.Transform(new Vector4(0f, metres, -depth, 1f), projection);
        return (tip.Y / tip.W - root.Y / root.W) * .5f * height;
    }
}
