using System.Numerics;
using KhaozEngine.Game;
using Xunit;

namespace KhaozEngine.Tests;

public class BootScreenThemeTests
{
    private const float Eps = 1e-5f;

    [Fact]
    public void DefaultMarqueeIsBrighterThanBarFillOnEachChannelWithMatchingAlpha()
    {
        // A fresh theme's marquee has no explicit override, so it falls back to a lightened BarFill: strictly
        // brighter on each colour channel, alpha untouched.
        var theme = BootScreenTheme.Default;

        Assert.True(theme.MarqueeColor.X > theme.BarFill.X);
        Assert.True(theme.MarqueeColor.Y > theme.BarFill.Y);
        Assert.True(theme.MarqueeColor.Z > theme.BarFill.Z);
        Assert.Equal(theme.BarFill.W, theme.MarqueeColor.W, Eps);
    }

    [Fact]
    public void AssigningBarFillAfterConstructionMovesMarqueeColorWithIt()
    {
        // The Ruinborne case: a game restyles only BarFill after construction. MarqueeColor is resolved on read,
        // so it tracks the new fill without needing to be reassigned too.
        var theme = BootScreenTheme.Default;
        var fill = new Vector4(0.2f, 0.6f, 0.9f, 0.8f);
        theme.BarFill = fill;

        // Mirrors BootScreenTheme's own lighten factor and formula (alpha passed through unchanged) so this
        // asserts the exact resolved value, not just "brighter than".
        const float lighten = 0.35f;
        var expected = new Vector4(
            fill.X + (1f - fill.X) * lighten,
            fill.Y + (1f - fill.Y) * lighten,
            fill.Z + (1f - fill.Z) * lighten,
            fill.W);

        Assert.Equal(expected.X, theme.MarqueeColor.X, Eps);
        Assert.Equal(expected.Y, theme.MarqueeColor.Y, Eps);
        Assert.Equal(expected.Z, theme.MarqueeColor.Z, Eps);
        Assert.Equal(expected.W, theme.MarqueeColor.W, Eps);
    }

    [Fact]
    public void ExplicitMarqueeColorOverridesAndIgnoresLaterBarFillChanges()
    {
        // Once MarqueeColor is assigned directly it is no longer derived, so a later restyle of BarFill must not
        // move it.
        var theme = BootScreenTheme.Default;
        var overrideColor = new Vector4(1f, 0f, 0f, 1f);
        theme.MarqueeColor = overrideColor;

        theme.BarFill = new Vector4(0.1f, 0.2f, 0.3f, 1f);

        Assert.Equal(overrideColor, theme.MarqueeColor);
    }
}
