using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class DisplayManagerHelperTests
{
    [Theory]
    [InlineData(WindowMode.Windowed, false, true)]
    [InlineData(WindowMode.BorderlessFullscreen, true, false)]
    [InlineData(WindowMode.ExclusiveFullscreen, true, true)]
    public void ResolveMode_MapsEachMode(WindowMode mode, bool isFullScreen, bool hardwareModeSwitch)
    {
        var (fs, hw) = DisplayManager.ResolveMode(mode);
        Assert.Equal(isFullScreen, fs);
        Assert.Equal(hardwareModeSwitch, hw);
    }

    [Fact]
    public void ClampToMinimum_BelowFloor_ClampsPerAxis()
    {
        Assert.Equal(new Point(300, 200), DisplayManager.ClampToMinimum(new Point(100, 50), 300, 200));
    }

    [Fact]
    public void ClampToMinimum_AtOrAboveFloor_PassesThrough()
    {
        Assert.Equal(new Point(400, 300), DisplayManager.ClampToMinimum(new Point(400, 300), 300, 200));
        Assert.Equal(new Point(500, 400), DisplayManager.ClampToMinimum(new Point(500, 400), 300, 200));
    }

    [Fact]
    public void ClampToMinimum_ZeroFloor_IsNoOp()
    {
        Assert.Equal(new Point(120, 80), DisplayManager.ClampToMinimum(new Point(120, 80), 0, 0));
    }
}
