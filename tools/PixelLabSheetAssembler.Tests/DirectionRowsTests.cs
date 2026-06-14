using System;
using KhaozEngine.Sprites;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class DirectionRowsTests
{
    [Fact]
    public void NameToRow_matches_live_Direction8_order()
    {
        // PixelLab export dir-name -> the Direction8 it must land on.
        var expected = new (string Name, Direction8 Dir)[]
        {
            ("south", Direction8.S),
            ("south-east", Direction8.SE),
            ("east", Direction8.E),
            ("north-east", Direction8.NE),
            ("north", Direction8.N),
            ("north-west", Direction8.NW),
            ("west", Direction8.W),
            ("south-west", Direction8.SW),
        };

        Assert.Equal(8, DirectionRows.NameToRow.Count);
        foreach (var (name, dir) in expected)
        {
            Assert.True(DirectionRows.NameToRow.ContainsKey(name), $"missing dir name '{name}'");
            // Row must equal the enum's integer value AND the loader's RowFor (the source of truth).
            Assert.Equal((int)dir, DirectionRows.NameToRow[name]);
            Assert.Equal(PixelLabSpriteLoader.RowFor(dir), DirectionRows.NameToRow[name]);
        }

        // Every Direction8 member is covered by some name (no row left unmapped).
        foreach (Direction8 d in Enum.GetValues<Direction8>())
            Assert.Contains((int)d, DirectionRows.NameToRow.Values);
    }
}
