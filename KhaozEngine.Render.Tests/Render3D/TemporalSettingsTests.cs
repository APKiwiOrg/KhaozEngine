using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary><see cref="PixelPostProcessSettings.Temporal"/>: the camera-cut thresholds and the sharpness, their
/// defaults, and the sharpness the pass is given.</summary>
public sealed class TemporalSettingsTests
{
    [Fact]
    public void TheSharpnessDefaultsToAQuarter()
    {
        var post = new PixelPostProcessSettings();
        Assert.Equal(0.25f, post.Temporal.Sharpness);
        Assert.Equal(0.25f, post.Temporal.ResolvedSharpness);
    }

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.NegativeInfinity, 0f)]
    [InlineData(-0.5f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.6f, 0.6f)]
    [InlineData(1f, 1f)]
    [InlineData(3f, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    public void TheSharpnessInEffectIsClampedToZeroToOneAndNaNIsOff(float sharpness, float inEffect)
    {
        var post = new PixelPostProcessSettings();
        post.Temporal.Sharpness = sharpness;
        Assert.Equal(inEffect, post.Temporal.ResolvedSharpness);
    }

    [Fact]
    public void TheCutThresholdsDefaultToSixteenMetresAndSixtyDegrees()
    {
        var post = new PixelPostProcessSettings();
        Assert.Equal(16f, post.Temporal.CutDistanceMetres);
        Assert.Equal(60f, post.Temporal.CutAngleDegrees);
    }

    [Fact]
    public void EverySettingsBagOwnsItsOwnTemporalSettings()
    {
        var a = new PixelPostProcessSettings();
        var b = new PixelPostProcessSettings();
        a.Temporal.CutDistanceMetres = 4f;
        Assert.Equal(16f, b.Temporal.CutDistanceMetres);
    }
}
