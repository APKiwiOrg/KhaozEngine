using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

// Headless coverage for the pure, GPU-free surface of the world-space nameplate widget: NameplateLayout.Measure
// (panel size math), NameplateBar.ClampedFraction, Nameplate.IsEmpty (the cull-when-nothing-to-draw predicate),
// and NameplateRenderer.ShouldCull (the distance cull, shared with WorldLabel). NameplateRenderer.Draw needs a
// GPU SpriteBatch so it is not unit-tested here, mirroring WorldLabel.
public sealed class NameplateTests
{
    // A device-free ITextMeasurer: each glyph is a fixed width, every line the same height, so the layout math
    // is exact and independent of a real baked font.
    sealed class FakeFont : ITextMeasurer
    {
        readonly float _charW;
        public FakeFont(float charW, float lineHeight) { _charW = charW; LineHeight = lineHeight; }
        public float LineHeight { get; }
        public Vector2 Measure(string text) => new Vector2((text?.Length ?? 0) * _charW, LineHeight);
    }

    static NameplateBar Bar(float fraction = 1f) =>
        new NameplateBar(fraction, Color.White, Color.Black);

    static Nameplate Plate(string title, params NameplateBar[] bars) =>
        new Nameplate { Title = title, TitleColor = Color.White, Bars = bars };

    // charW=10, lineHeight=20; padding/spacing chosen so every term in the size math is distinct.
    static readonly FakeFont Font = new FakeFont(10f, 20f);
    static NameplateStyle Style => NameplateStyle.Default with
    {
        FontScale = 1f, PadX = 6f, PadY = 4f, MinBarWidth = 50f,
        BarHeight = 8f, BarSpacing = 3f, MaxWidth = 0f,
    };

    [Fact]
    public void Measure_matchesPaddingSpacingMath_forKnownFontAndStyle()
    {
        // Title "ABCD" => 4*10 = 40 wide, 20 tall. Inner width floored to MinBarWidth (50) since 40 < 50.
        // One bar => contentH = title(20) + spacing(3) + barHeight(8) = 31.
        // outer = inner + 2*Pad: (50 + 12, 31 + 8) = (62, 39).
        Vector2 size = NameplateLayout.Measure(Font, Plate("ABCD", Bar()), Style);
        Assert.Equal(62f, size.X, 3);
        Assert.Equal(39f, size.Y, 3);
    }

    [Fact]
    public void Measure_titleWiderThanMinBar_drivesWidth()
    {
        // Title "ABCDEFGH" => 80 wide > MinBarWidth 50, so inner width = 80, outer = 80 + 12 = 92.
        Vector2 size = NameplateLayout.Measure(Font, Plate("ABCDEFGH", Bar()), Style);
        Assert.Equal(92f, size.X, 3);
    }

    [Fact]
    public void Measure_growsWithTitleLength()
    {
        float shortW = NameplateLayout.Measure(Font, Plate("ABCDEF", Bar()), Style).X;   // 60 > 50
        float longW = NameplateLayout.Measure(Font, Plate("ABCDEFGHIJ", Bar()), Style).X; // 100 > 50
        Assert.True(longW > shortW);
    }

    [Fact]
    public void Measure_growsWithBarCount()
    {
        float one = NameplateLayout.Measure(Font, Plate("ABCD", Bar()), Style).Y;
        float three = NameplateLayout.Measure(Font, Plate("ABCD", Bar(), Bar(), Bar()), Style).Y;
        // Each extra bar adds BarSpacing + BarHeight = 11 in height.
        Assert.True(three > one);
        Assert.Equal(one + 2f * (3f + 8f), three, 3);
    }

    [Fact]
    public void Measure_titleOnly_hasNoBarHeight()
    {
        // No bars: contentH == titleH (20), outerH = 20 + 2*PadY(8) = 28. Width still floored to MinBarWidth.
        Vector2 size = NameplateLayout.Measure(Font, Plate("ABCD"), Style);
        Assert.Equal(62f, size.X, 3);
        Assert.Equal(28f, size.Y, 3);
    }

    [Fact]
    public void Measure_barsOnly_noLeadingSpacingAboveFirstBar()
    {
        // Empty title, two bars: no title row and no gap above the first bar.
        // contentH = barHeight + (spacing + barHeight) = 8 + 11 = 19, outerH = 19 + 8 = 27.
        Vector2 size = NameplateLayout.Measure(Font, Plate("", Bar(), Bar()), Style);
        Assert.Equal(27f, size.Y, 3);
    }

    [Fact]
    public void Measure_maxWidthCapsWidth()
    {
        // Title 100 wide would give outer 112; MaxWidth 80 caps it.
        NameplateStyle capped = Style with { MaxWidth = 80f };
        Vector2 size = NameplateLayout.Measure(Font, Plate("ABCDEFGHIJ", Bar()), capped);
        Assert.Equal(80f, size.X, 3);
    }

    [Fact]
    public void Measure_emptyPlate_isZero()
    {
        Assert.Equal(Vector2.Zero, NameplateLayout.Measure(Font, Plate(""), Style));
        Assert.Equal(Vector2.Zero, NameplateLayout.Measure(Font, Plate(null!), Style));
    }

    [Fact]
    public void ClampedFraction_clampsToUnitRange()
    {
        Assert.Equal(1f, Bar(1.5f).ClampedFraction, 3);
        Assert.Equal(0f, Bar(-0.5f).ClampedFraction, 3);
        Assert.Equal(0.5f, Bar(0.5f).ClampedFraction, 3);
    }

    [Fact]
    public void IsEmpty_trueWhenTitleAndBarsEmpty()
    {
        Assert.True(Plate("").IsEmpty);
        Assert.True(Plate(null!).IsEmpty);
        Assert.True(new Nameplate { Title = "", Bars = null! }.IsEmpty);
    }

    [Fact]
    public void IsEmpty_falseWhenTitleOrBarsPresent()
    {
        Assert.False(Plate("Name").IsEmpty);      // title only
        Assert.False(Plate("", Bar()).IsEmpty);   // bar only
    }

    [Fact]
    public void Ellipsize_returnsFullTextWhenItFits()
    {
        // "ABCD" = 40 wide <= 100, so no truncation.
        Assert.Equal("ABCD", NameplateLayout.Ellipsize(Font, "ABCD", 100f, 1f));
    }

    [Fact]
    public void Ellipsize_truncatesWithAsciiDotsWhenTooWide()
    {
        // charW=10: "ABCDEFGH" = 80 wide, cap 55. "..." = 30 wide, so prefix must be <= 25 => 2 chars => "AB...".
        // ASCII dots (not the "..." glyph) because the baked font only covers ASCII 32-126.
        string r = NameplateLayout.Ellipsize(Font, "ABCDEFGH", 55f, 1f);
        Assert.Equal("AB...", r);
        Assert.True(Font.Measure(r).X <= 55f);
    }

    [Fact]
    public void ShouldCull_matchesWorldLabelPredicate()
    {
        var target = new Vector3(100f, 0f, 0f);
        var from = Vector3.Zero;
        // Same distance predicate as WorldLabel: beyond the ring culls, inside does not, 0 never culls.
        Assert.Equal(WorldLabel.ShouldCull(target, from, 90f), NameplateRenderer.ShouldCull(target, from, 90f));
        Assert.True(NameplateRenderer.ShouldCull(target, from, 90f));
        Assert.False(NameplateRenderer.ShouldCull(new Vector3(80f, 0f, 0f), from, 90f));
        Assert.False(NameplateRenderer.ShouldCull(target, from, 0f));
    }
}
