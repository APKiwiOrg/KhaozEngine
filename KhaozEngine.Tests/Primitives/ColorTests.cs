using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class ColorTests
{
    [Fact]
    public void FromBytes_NormalizesChannels()
    {
        var c = Color.FromBytes(255, 128, 0, 255);
        Assert.Equal(1f, c.R, 3);
        Assert.Equal(128f / 255f, c.G, 5);
        Assert.Equal(0f, c.B, 3);
        Assert.Equal(1f, c.A, 3);
    }

    [Theory]
    [InlineData("#FF8000", 255, 128, 0, 255)]
    [InlineData("FF8000", 255, 128, 0, 255)]
    [InlineData("#FF800080", 255, 128, 0, 128)]
    public void FromHex_ParsesRgbAndRgba(string hex, int r, int g, int b, int a)
    {
        var c = Color.FromHex(hex);
        Assert.Equal(Color.FromBytes((byte)r, (byte)g, (byte)b, (byte)a), c);
    }

    [Fact]
    public void ToHex_RoundTrips()
    {
        var c = Color.FromBytes(18, 52, 86, 171);
        Assert.Equal(c, Color.FromHex(Color.ToHex(c)));
    }

    [Fact]
    public void ToHex_FormatsRrggbbaaUpper()
    {
        Assert.Equal("#FF800080", Color.ToHex(Color.FromBytes(255, 128, 0, 128)));
    }

    [Fact]
    public void WithAlpha_ReplacesOnlyAlpha()
    {
        var c = new Color(0.2f, 0.4f, 0.6f, 1f).WithAlpha(0.5f);
        Assert.Equal(new Color(0.2f, 0.4f, 0.6f, 0.5f), c);
    }
}
