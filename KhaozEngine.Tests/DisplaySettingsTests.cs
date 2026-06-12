using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class DisplaySettingsTests
{
    [Fact]
    public void Landscape_SetsDimsAndLandscapeOrientations()
    {
        var s = DisplaySettings.Landscape(932, 430);
        Assert.Equal(932, s.Width);
        Assert.Equal(430, s.Height);
        Assert.Equal(DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight,
            s.SupportedOrientations);
    }

    [Fact]
    public void Portrait_SetsDimsAndPortraitOrientations()
    {
        var s = DisplaySettings.Portrait(430, 932);
        Assert.Equal(430, s.Width);
        Assert.Equal(932, s.Height);
        Assert.Equal(DisplayOrientation.Portrait | DisplayOrientation.PortraitDown,
            s.SupportedOrientations);
    }

    [Fact]
    public void Defaults_AreWindowedNonResizableNoFloor()
    {
        var s = DisplaySettings.Landscape(800, 480);
        Assert.Equal(WindowMode.Windowed, s.Mode);
        Assert.False(s.AllowUserResizing);
        Assert.Equal(0, s.MinWidth);
        Assert.Equal(0, s.MinHeight);
        Assert.Null(s.Title);
    }

    [Fact]
    public void With_ExpressionOverridesSingleProperty()
    {
        var s = DisplaySettings.Landscape(800, 480) with { Mode = WindowMode.BorderlessFullscreen };
        Assert.Equal(WindowMode.BorderlessFullscreen, s.Mode);
        Assert.Equal(800, s.Width); // unchanged
    }
}
