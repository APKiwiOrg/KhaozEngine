using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class DirectionRowsTests
{
    [Fact]
    public void NameToRow_matches_the_canonical_Direction8_order()
    {
        // The canonical S, SE, E, NE, N, NW, W, SW order (rows 0..7) the PixelLab sheet rows must follow.
        // (Was cross-checked against KhaozEngine.Sprites.Direction8; that legacy MonoGame package is gone, so the
        // order is asserted directly here.)
        var expected = new (string Name, int Row)[]
        {
            ("south", 0),
            ("south-east", 1),
            ("east", 2),
            ("north-east", 3),
            ("north", 4),
            ("north-west", 5),
            ("west", 6),
            ("south-west", 7),
        };

        Assert.Equal(8, DirectionRows.NameToRow.Count);
        foreach (var (name, row) in expected)
        {
            Assert.True(DirectionRows.NameToRow.ContainsKey(name), $"missing dir name '{name}'");
            Assert.Equal(row, DirectionRows.NameToRow[name]);
        }

        for (int r = 0; r < 8; r++)
        {
            Assert.Contains(r, DirectionRows.NameToRow.Values);
        }
    }
}
