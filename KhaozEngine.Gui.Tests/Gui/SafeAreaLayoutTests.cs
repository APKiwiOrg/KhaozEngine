using System;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public class SafeAreaLayoutTests
{
    static readonly Rect Viewport = new(100, 200, 800, 600);
    static readonly SafeAreaInsets Insets = new(20, 40, 30, 50);

    [Theory]
    [InlineData(Anchor.TopLeft, 130, 220)]
    [InlineData(Anchor.Top, 450, 220)]
    [InlineData(Anchor.TopRight, 770, 220)]
    [InlineData(Anchor.Left, 130, 470)]
    [InlineData(Anchor.Center, 450, 470)]
    [InlineData(Anchor.Right, 770, 470)]
    [InlineData(Anchor.BottomLeft, 130, 720)]
    [InlineData(Anchor.Bottom, 450, 720)]
    [InlineData(Anchor.BottomRight, 770, 720)]
    public void AllAnchors_RespectAsymmetricSafeArea(Anchor anchor, float x, float y)
    {
        Rect child = Layout.Resolve(Insets.Apply(Viewport), anchor, 80, 40);
        Assert.Equal(new Rect(x, y, 80, 40), child);
    }

    [Fact]
    public void Stretch_FillsSafeAreaAndRespectsAdditionalMargin()
        => Assert.Equal(new Rect(135, 230, 710, 520),
            Layout.Resolve(Insets.Apply(Viewport), Anchor.Stretch, 0, 0, 5, 10));

    [Fact]
    public void ZeroInsets_PreserveOriginalBounds()
        => Assert.Equal(Viewport, SafeAreaInsets.Zero.Apply(Viewport));

    [Fact]
    public void OversizedInsets_ProduceEmptyBoundsInsideViewport()
        => Assert.Equal(new Rect(900, 800, 0, 0), new SafeAreaInsets(700, 700, 900, 900).Apply(Viewport));

    [Theory]
    [InlineData(0, 0, 130, 220)]
    [InlineData(0.25f, 0.75f, 290, 595)]
    [InlineData(1, 1, 770, 720)]
    public void FractionalAnchor_PlacesWholeChildInsideSafeArea(float fx, float fy, float x, float y)
        => Assert.Equal(new Rect(x, y, 80, 40),
            Layout.ResolveFractional(Insets.Apply(Viewport), fx, fy, 80, 40));

    [Fact]
    public void FractionalPoint_CanUseViewportBoundsWithAnOffset()
        => Assert.Equal(new Rect(500, 483, 0, 0),
            Layout.ResolveFractional(Insets.Apply(Viewport), 0.5f, 0.5f, 0, 0, 10, -7));

    [Theory]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidInsets_AreRejectedOnEveryEdge(float value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SafeAreaInsets(value, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SafeAreaInsets(0, value, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SafeAreaInsets(0, 0, value, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SafeAreaInsets(0, 0, 0, value));
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    [InlineData(float.NaN)]
    public void InvalidFractions_AreRejected(float value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Layout.ResolveFractional(Viewport, value, 0, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layout.ResolveFractional(Viewport, 0, value, 10, 10));
    }
}
