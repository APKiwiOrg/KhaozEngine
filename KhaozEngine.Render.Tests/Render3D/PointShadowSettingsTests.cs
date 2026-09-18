using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// Pure coverage of <see cref="PointShadowSettings"/>: the defaults a game inherits without asking, the clamps that
/// keep a menu value from over-running the atlas or the fixed-size UBO arrays, the memory arithmetic a profile
/// screen quotes, and the deep copy. Nothing here touches a device, because none of it should need one.
/// </summary>
public sealed class PointShadowSettingsTests
{
    [Fact]
    public void Defaults_AreTheShippedProfile()
    {
        var s = new PointShadowSettings();

        Assert.True(s.Enabled);
        Assert.Equal(256, s.FaceResolution);
        Assert.Equal(8, s.MaxShadowedLights);
        Assert.Equal(2, s.MaxStaticRebuildsPerFrame);
        Assert.Equal(4, s.MaxDynamicLightsPerFrame);
        Assert.Equal(0.01f, s.Bias);
        Assert.Equal(0.02f, s.SlopeBias);
    }

    [Fact]
    public void ShadowSettings_CarriesPointShadowsByDefault()
    {
        var shadows = new ShadowSettings();

        Assert.NotNull(shadows.PointShadows);
        Assert.True(shadows.PointShadows.Enabled);
        Assert.NotSame(new ShadowSettings().PointShadows, shadows.PointShadows);
    }

    [Theory]
    [InlineData(7, PointShadowSettings.MinFaceResolution)]
    [InlineData(0, PointShadowSettings.MinFaceResolution)]
    [InlineData(-3, PointShadowSettings.MinFaceResolution)]
    [InlineData(4096, PointShadowSettings.MaxFaceResolution)]
    [InlineData(256, 256)]
    public void ResolvedFaceResolution_Clamps(int requested, int expected)
    {
        Assert.Equal(expected, new PointShadowSettings { FaceResolution = requested }.ResolvedFaceResolution);
    }

    [Theory]
    [InlineData(0, PointShadowSettings.MinLights)]
    [InlineData(-1, PointShadowSettings.MinLights)]
    [InlineData(99, PointShadowSettings.MaxLights)]
    [InlineData(8, 8)]
    public void ResolvedMaxLights_Clamps(int requested, int expected)
    {
        Assert.Equal(expected, new PointShadowSettings { MaxShadowedLights = requested }.ResolvedMaxLights);
    }

    // A NEGATIVE bias is the case worth the clamp. It is subtracted from the stored distance, which turns the
    // receiver's compare around: every surface reads as occluded by itself and the light leaks through what it
    // was lighting. NaN is undefined for Math.Clamp and poisons the compare outright, so it resolves to zero.
    [Theory]
    [InlineData(-0.5f, 0f)]
    [InlineData(-0.0001f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.01f, 0.01f)]
    [InlineData(PointShadowSettings.MaxBias, PointShadowSettings.MaxBias)]
    [InlineData(1f, PointShadowSettings.MaxBias)]
    [InlineData(float.PositiveInfinity, PointShadowSettings.MaxBias)]
    [InlineData(float.NegativeInfinity, 0f)]
    [InlineData(float.NaN, 0f)]
    public void ResolvedBias_Clamps(float requested, float expected)
    {
        Assert.Equal(expected, new PointShadowSettings { Bias = requested }.ResolvedBias);
    }

    [Theory]
    [InlineData(-0.5f, 0f)]
    [InlineData(0.02f, 0.02f)]
    [InlineData(1f, PointShadowSettings.MaxBias)]
    [InlineData(float.NaN, 0f)]
    public void ResolvedSlopeBias_Clamps(float requested, float expected)
    {
        Assert.Equal(expected, new PointShadowSettings { SlopeBias = requested }.ResolvedSlopeBias);
    }

    [Fact]
    public void ResolvedBiases_PassTheDefaultsThrough()
    {
        var s = new PointShadowSettings();

        Assert.Equal(s.Bias, s.ResolvedBias);
        Assert.Equal(s.SlopeBias, s.ResolvedSlopeBias);
    }

    [Fact]
    public void MaxLights_IsTheUboPointLightArraySize()
    {
        Assert.Equal(Scene3D.MaxPointLights, PointShadowSettings.MaxLights);
        Assert.Equal(16, PointShadowSettings.MaxLights);
    }

    [Fact]
    public void AtlasBytes_CountsColourAndDepthOverEveryCell()
    {
        // 6 face columns by MaxShadowedLights rows of FaceResolution square, 4 colour bytes (R32Float) plus 5
        // depth-stencil bytes (D32FloatS8UInt) a texel.
        var s = new PointShadowSettings { FaceResolution = 256, MaxShadowedLights = 8 };

        Assert.Equal(6L * 256 * 8 * 256 * 9, s.AtlasBytes);
    }

    [Fact]
    public void AtlasBytes_ReadsTheClampedValues()
    {
        var s = new PointShadowSettings { FaceResolution = 4096, MaxShadowedLights = 99 };

        Assert.Equal(6L * PointShadowSettings.MaxFaceResolution * PointShadowSettings.MaxLights
            * PointShadowSettings.MaxFaceResolution * 9, s.AtlasBytes);
    }

    [Fact]
    public void ForDetail_Low_TurnsPointShadowsOff()
    {
        Assert.False(ShadowSettings.ForDetail(ShadowMapDetail.Low).PointShadows.Enabled);
    }

    [Fact]
    public void ForDetail_Default_KeepsTheDefaults()
    {
        PointShadowSettings p = ShadowSettings.ForDetail(ShadowMapDetail.Default).PointShadows;

        Assert.True(p.Enabled);
        Assert.Equal(256, p.FaceResolution);
        Assert.Equal(8, p.MaxShadowedLights);
    }

    [Fact]
    public void ForDetail_High_RaisesTheResolutionAndTheLightBudget()
    {
        PointShadowSettings p = ShadowSettings.ForDetail(ShadowMapDetail.High).PointShadows;

        Assert.True(p.Enabled);
        Assert.Equal(384, p.FaceResolution);
        Assert.Equal(12, p.MaxShadowedLights);
    }

    /// <summary>
    /// WHAT EACH PROFILE COSTS IN VIDEO MEMORY, pinned so the next person to move one of these numbers reads the
    /// price in the same commit. The atlas is nine bytes a texel, so the face resolution is squared and the light
    /// budget is linear: raising High from 384 by 12 to 512 by 16 would take it from 96 MB to 226 MB, which is
    /// what these three numbers exist to make visible.
    /// <para>
    /// Low is off, so it allocates nothing at all. Its figure is what the atlas WOULD cost if a game turned the
    /// feature back on without touching the rest of the profile, which is why the number is Default's.
    /// </para>
    /// </summary>
    [Fact]
    public void ForDetail_AtlasBytes_AreTheMemoryEachProfileCosts()
    {
        Assert.Equal(28_311_552L, ShadowSettings.ForDetail(ShadowMapDetail.Default).PointShadows.AtlasBytes);
        Assert.Equal(95_551_488L, ShadowSettings.ForDetail(ShadowMapDetail.High).PointShadows.AtlasBytes);
        Assert.Equal(28_311_552L, ShadowSettings.ForDetail(ShadowMapDetail.Low).PointShadows.AtlasBytes);
        Assert.False(ShadowSettings.ForDetail(ShadowMapDetail.Low).PointShadows.Enabled);
    }

    [Fact]
    public void Clone_IsADeepCopy()
    {
        var s = new PointShadowSettings
        {
            Enabled = false,
            FaceResolution = 512,
            MaxShadowedLights = 3,
            MaxStaticRebuildsPerFrame = 5,
            MaxDynamicLightsPerFrame = 6,
            Bias = 0.5f,
            SlopeBias = 0.25f,
        };

        PointShadowSettings copy = s.Clone();

        Assert.NotSame(s, copy);
        Assert.False(copy.Enabled);
        Assert.Equal(512, copy.FaceResolution);
        Assert.Equal(3, copy.MaxShadowedLights);
        Assert.Equal(5, copy.MaxStaticRebuildsPerFrame);
        Assert.Equal(6, copy.MaxDynamicLightsPerFrame);
        Assert.Equal(0.5f, copy.Bias);
        Assert.Equal(0.25f, copy.SlopeBias);

        copy.FaceResolution = 64;
        copy.Enabled = true;
        Assert.Equal(512, s.FaceResolution);
        Assert.False(s.Enabled);
    }
}
