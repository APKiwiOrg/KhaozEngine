using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary><see cref="PixelPostProcessSettings.Temporal"/>: the camera-cut thresholds and their defaults.</summary>
public sealed class TemporalSettingsTests
{
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
