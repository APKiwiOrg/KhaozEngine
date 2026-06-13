using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Headless geometry tests for <see cref="PrimitiveRenderer.ComputeProgressBarLayout"/>.
/// Regression coverage for zoomed-out HP bars rendering as a solid border-colored
/// line: a short bar must still produce a visible (non-zero height) fill.
/// </summary>
public class ProgressBarLayoutTests
{
    [Fact]
    public void NormalBarKeepsRequestedBorderAndInsetFill()
    {
        // 96x4 bar, 1px border: classic case, fill inset by the border on all sides.
        (Rectangle fill, int border) = PrimitiveRenderer.ComputeProgressBarLayout(
            new Rectangle(10, 20, 96, 4), 0.5f, 1);

        Assert.Equal(1, border);
        Assert.Equal(11, fill.X);                 // 10 + border
        Assert.Equal(21, fill.Y);                 // 20 + border
        Assert.Equal(47, fill.Width);             // (96 - 2) * 0.5
        Assert.Equal(2, fill.Height);             // 4 - 2*border
    }

    [Fact]
    public void TwoPixelTallBarDropsBorderSoFillStaysVisible()
    {
        // The zoomed-out HP bar: (int)(4 * 0.6) = 2px tall. With a 1px border the
        // old code produced inner height 0, the fill never drew, and the border
        // covered the whole bar (grey line). The border must now drop to 0 so the
        // fill spans the full height.
        (Rectangle fill, int border) = PrimitiveRenderer.ComputeProgressBarLayout(
            new Rectangle(0, 0, 57, 2), 0.8f, 1);

        Assert.Equal(0, border);
        Assert.Equal(2, fill.Height);             // full height, not collapsed
        Assert.True(fill.Width > 0);              // 57 * 0.8 = 45
    }

    [Fact]
    public void ThreePixelTallBarKeepsOnePixelFill()
    {
        // (int)(4 * 0.75) = 3px tall: border stays at 1, leaving a 1px fill strip.
        (Rectangle fill, int border) = PrimitiveRenderer.ComputeProgressBarLayout(
            new Rectangle(0, 0, 40, 3), 1f, 1);

        Assert.Equal(1, border);
        Assert.Equal(1, fill.Height);
    }

    [Fact]
    public void ProgressIsClampedToZeroAndOne()
    {
        (Rectangle empty, _) = PrimitiveRenderer.ComputeProgressBarLayout(
            new Rectangle(0, 0, 100, 10), -0.5f, 1);
        Assert.Equal(0, empty.Width);

        (Rectangle full, _) = PrimitiveRenderer.ComputeProgressBarLayout(
            new Rectangle(0, 0, 100, 10), 2f, 1);
        Assert.Equal(98, full.Width);             // (100 - 2) * 1.0
    }
}
