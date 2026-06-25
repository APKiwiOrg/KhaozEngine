using System;
using System.Collections.Generic;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class CellCoordTests
{
    [Theory]
    [InlineData(5f, 5f, 0, 0)]        // inside the origin cell
    [InlineData(105f, 5f, 1, 0)]      // one cell east
    [InlineData(5f, 250f, 0, 2)]      // two cells north
    [InlineData(100f, 100f, 1, 1)]    // exact edge belongs to the higher cell
    [InlineData(-5f, -5f, -1, -1)]    // negative floors down (not truncates toward zero)
    [InlineData(-105f, 250f, -2, 2)]  // mixed-sign
    public void FromWorld_FloorsPositionIntoCell(float x, float y, int expectX, int expectY)
    {
        CellCoord coord = CellCoord.FromWorld(x, y, cellSize: 100f);
        Assert.Equal(new CellCoord(expectX, expectY), coord);
    }

    [Fact]
    public void FromWorld_RejectsNonPositiveCellSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CellCoord.FromWorld(1f, 1f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => CellCoord.FromWorld(1f, 1f, -10f));
    }

    [Fact]
    public void Equality_SameComponents_AreEqualAndShareHash()
    {
        var a = new CellCoord(3, -7);
        var b = new CellCoord(3, -7);
        var c = new CellCoord(3, 7);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.True(a != c);
    }

    [Fact]
    public void UsableAsDictionaryKey()
    {
        var map = new Dictionary<CellCoord, int>
        {
            [new CellCoord(0, 0)] = 1,
            [new CellCoord(1, 0)] = 2,
        };

        Assert.Equal(1, map[new CellCoord(0, 0)]);
        Assert.Equal(2, map[new CellCoord(1, 0)]);
        Assert.False(map.ContainsKey(new CellCoord(0, 1)));
    }
}
