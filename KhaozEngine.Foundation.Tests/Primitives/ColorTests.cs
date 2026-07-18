using System;
using System.Numerics;
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

    [Fact]
    public void ScaleRgb_ScalesRgbAndPreservesAlpha()
    {
        var c = new Color(0.2f, 0.4f, 0.6f, 0.8f).ScaleRgb(0.5f);
        Assert.Equal(new Color(0.1f, 0.2f, 0.3f, 0.8f), c);   // alpha untouched, unlike c * 0.5f
    }

    [Fact]
    public void ScaleRgb_ContrastsWithMultiply_OnAlpha()
    {
        var c = new Color(0.2f, 0.4f, 0.6f, 1f);
        Assert.Equal(1f, c.ScaleRgb(0.6f).A, 5);              // ScaleRgb keeps opacity
        Assert.Equal(0.6f, (c * 0.6f).A, 5);                  // operator * dims alpha too
    }

    [Theory]
    [InlineData(-0.25f)]   // unclamped below 0
    [InlineData(1.5f)]     // unclamped above 1
    public void ScaleRgb_IsUnclamped(float factor)
    {
        var c = new Color(0.2f, 0.4f, 0.6f, 0.8f).ScaleRgb(factor);
        Assert.Equal(new Color(0.2f * factor, 0.4f * factor, 0.6f * factor, 0.8f), c);
    }

    [Fact]
    public void ScaleRgbClamped_ScalesRgbAndPreservesAlpha_WhenInRange()
    {
        var c = new Color(0.2f, 0.4f, 0.6f, 0.8f).ScaleRgbClamped(0.5f);
        Assert.Equal(new Color(0.1f, 0.2f, 0.3f, 0.8f), c);   // same result as ScaleRgb while nothing overshoots
    }

    [Fact]
    public void ScaleRgbClamped_ClampsChannelsAboveOne_AlphaUntouched()
    {
        // 1.6x over a bright base pushes R and G past 1.0 (clamped to 1), while B stays in range, scaled normally.
        // Alpha is neither scaled nor clamped (it was already 1 here), matching ScaleRgb's alpha-preserving
        // contract. Compared channel-by-channel with a float tolerance (not full-struct equality) since 0.3f *
        // 1.6f is not bit-exact with the literal 0.48f.
        var c = new Color(0.9f, 0.8f, 0.3f, 1f).ScaleRgbClamped(1.6f);
        Assert.Equal(1f, c.R, 5);
        Assert.Equal(1f, c.G, 5);
        Assert.Equal(0.48f, c.B, 5);
        Assert.Equal(1f, c.A, 5);
    }

    [Fact]
    public void ScaleRgbClamped_ClampsChannelsBelowZero()
    {
        var c = new Color(0.2f, 0.4f, 0.6f, 0.8f).ScaleRgbClamped(-0.5f);
        Assert.Equal(new Color(0f, 0f, 0f, 0.8f), c);   // negative factor would otherwise go negative per channel
    }

    // --- error paths ---

    [Fact]
    public void FromHex_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Color.FromHex(null!));
    }

    [Theory]
    [InlineData("#12345")]      // wrong length (5 digits after #)
    [InlineData("#GG0000")]     // invalid hex digits
    public void FromHex_Malformed_ThrowsFormatException(string hex)
    {
        Assert.Throws<FormatException>(() => Color.FromHex(hex));
    }

    // --- clamping ---

    [Fact]
    public void ToHex_ClampsOutOfRangeChannels()
    {
        // R=2f->clamp->255=FF, G=-1f->clamp->0=00, B=0.5f->128=80, A=1f->255=FF
        Assert.Equal("#FF0080FF", Color.ToHex(new Color(2f, -1f, 0.5f, 1f)));
    }

    // --- operator / method round-trips ---

    [Fact]
    public void ImplicitToVector4_ThenExplicitBack_RoundTrips()
    {
        var original = new Color(0.1f, 0.2f, 0.3f, 0.4f);
        System.Numerics.Vector4 v4 = original;          // implicit
        var roundTripped = (Color)v4;                   // explicit
        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void ToVector4_FromVector4_RoundTrips()
    {
        var original = new Color(0.25f, 0.5f, 0.75f, 1f);
        Assert.Equal(original, Color.FromVector4(original.ToVector4()));
    }

    // --- sentinels ---

    [Fact]
    public void White_EqualsOneOneOneOne()
    {
        Assert.Equal(new Color(1f, 1f, 1f, 1f), Color.White);
    }

    [Fact]
    public void Black_EqualsZeroZeroZeroOne()
    {
        Assert.Equal(new Color(0f, 0f, 0f, 1f), Color.Black);
    }

    [Fact]
    public void Transparent_EqualsZeroZeroZeroZero()
    {
        Assert.Equal(new Color(0f, 0f, 0f, 0f), Color.Transparent);
    }

    // --- scalar multiply + lerp (6.3.0 additive helpers) ---

    [Fact]
    public void Multiply_ScalesAllChannelsIncludingAlpha_Unclamped()
    {
        var c = new Color(0.2f, 0.4f, 0.6f, 0.8f);
        Assert.Equal(new Color(0.4f, 0.8f, 1.2f, 1.6f), c * 2f);   // alpha scales too; not clamped
    }

    [Fact]
    public void Multiply_IsSymmetric()
    {
        var c = new Color(0.2f, 0.4f, 0.6f, 0.8f);
        Assert.Equal(c * 1.5f, 1.5f * c);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    [InlineData(-0.25f)]   // unclamped below 0
    [InlineData(1.5f)]     // unclamped above 1
    public void Lerp_IsByteIdenticalToVector4Lerp(float t)
    {
        var a = new Color(0.1f, 0.2f, 0.3f, 0.4f);
        var b = new Color(0.9f, 0.7f, 0.5f, 1.0f);
        var expected = (Color)Vector4.Lerp(a.ToVector4(), b.ToVector4(), t);
        Assert.Equal(expected, Color.Lerp(a, b, t));
    }
}
