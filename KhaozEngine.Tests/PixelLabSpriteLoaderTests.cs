using KhaozEngine.Sprites;
using Xunit;

namespace KhaozEngine.Tests;

public class PixelLabSpriteLoaderTests
{
    // PixelLab's directional row order is S, SE, E, NE, N, NW, W, SW. The loader maps each
    // Direction8 to its sheet row; this is the single place that assumption lives, so pin it.
    [Theory]
    [InlineData(Direction8.S, 0)]
    [InlineData(Direction8.SE, 1)]
    [InlineData(Direction8.E, 2)]
    [InlineData(Direction8.NE, 3)]
    [InlineData(Direction8.N, 4)]
    [InlineData(Direction8.NW, 5)]
    [InlineData(Direction8.W, 6)]
    [InlineData(Direction8.SW, 7)]
    public void RowFor_matches_pixellab_export_row_order(Direction8 direction, int expectedRow)
    {
        Assert.Equal(expectedRow, PixelLabSpriteLoader.RowFor(direction));
    }
}
