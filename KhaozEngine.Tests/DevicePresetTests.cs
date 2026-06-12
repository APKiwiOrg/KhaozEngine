using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class DevicePresetTests
{
    [Fact]
    public void IPhone15ProMax_Landscape_Is932x430()
    {
        var s = DevicePresets.IPhone15ProMax.Landscape();
        Assert.Equal(932, s.Width);
        Assert.Equal(430, s.Height);
        Assert.Equal(DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight,
            s.SupportedOrientations);
    }

    [Fact]
    public void IPhone15ProMax_Portrait_Is430x932()
    {
        var s = DevicePresets.IPhone15ProMax.Portrait();
        Assert.Equal(430, s.Width);
        Assert.Equal(932, s.Height);
        Assert.Equal(DisplayOrientation.Portrait | DisplayOrientation.PortraitDown,
            s.SupportedOrientations);
    }

    [Fact]
    public void Landscape_SwapsPortraitDims()
    {
        var p = new DevicePreset("test", 390, 844);
        Assert.Equal(390, p.Portrait().Width);
        Assert.Equal(844, p.Portrait().Height);
        Assert.Equal(844, p.Landscape().Width);
        Assert.Equal(390, p.Landscape().Height);
    }

    [Fact]
    public void IPadPro129_Landscape_Is1366x1024()
    {
        var s = DevicePresets.IPadPro129.Landscape();
        Assert.Equal(1366, s.Width);
        Assert.Equal(1024, s.Height);
    }
}
