using System;
using System.Numerics;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class CellGridTests
{
    private static readonly CellGrid Shifted = new(100f, new Vector2(64f, -64f));

    [Theory]
    [InlineData(5f, 5f)]
    [InlineData(100f, 100f)]
    [InlineData(-5f, -5f)]
    [InlineData(-105f, 250f)]
    [InlineData(99.999f, -0.001f)]
    public void DefaultOrigin_KeysExactlyLikeFromWorld(float x, float y)
    {
        var grid = new CellGrid(100f, Vector2.Zero);
        Assert.Equal(CellCoord.FromWorld(x, y, 100f), grid.CoordFor(x, y));
    }

    [Theory]
    [InlineData(64f, -64f, 0, 0)]     // the origin itself is cell (0, 0)'s lower corner
    [InlineData(163.9f, 35.9f, 0, 0)] // just inside the upper corner
    [InlineData(164f, 36f, 1, 1)]     // the upper edge belongs to the next cell
    [InlineData(63.9f, -64.1f, -1, -1)]
    [InlineData(0f, 0f, -1, 0)]       // the world origin is no longer a cell corner
    [InlineData(120f, 0f, 0, 0)]      // the zero-origin grid would put this in (1, 0)
    public void ShiftedOrigin_KeysRelativeToTheOrigin(float x, float y, int expectX, int expectY)
    {
        Assert.Equal(new CellCoord(expectX, expectY), Shifted.CoordFor(x, y));
    }

    [Fact]
    public void BoundsAndCentre_SitWhereTheOriginSays()
    {
        (Vector2 min, Vector2 max) = Shifted.BoundsOf(new CellCoord(0, 0));
        Assert.Equal(new Vector2(64f, -64f), min);
        Assert.Equal(new Vector2(164f, 36f), max);
        Assert.Equal(new Vector2(114f, -14f), Shifted.CenterOf(new CellCoord(0, 0)));

        (min, max) = Shifted.BoundsOf(new CellCoord(-1, 2));
        Assert.Equal(new Vector2(-36f, 136f), min);
        Assert.Equal(new Vector2(64f, 236f), max);
    }

    [Fact]
    public void BoundsOf_RoundTripsThroughCoordFor()
    {
        foreach (CellCoord coord in new[] { new CellCoord(0, 0), new CellCoord(3, -2), new CellCoord(-7, 5) })
        {
            (Vector2 min, Vector2 max) = Shifted.BoundsOf(coord);
            Assert.Equal(coord, Shifted.CoordFor(min.X, min.Y));
            Assert.Equal(coord, Shifted.CoordFor(max.X - 0.01f, max.Y - 0.01f));
            Assert.Equal(new CellCoord(coord.X + 1, coord.Y + 1), Shifted.CoordFor(max.X, max.Y));
            Vector2 centre = Shifted.CenterOf(coord);
            Assert.Equal(coord, Shifted.CoordFor(centre.X, centre.Y));
        }
    }

    [Fact]
    public void Constructor_RejectsANonFiniteOriginOrABadCellSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellGrid(100f, new Vector2(float.NaN, 0f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellGrid(100f, new Vector2(0f, float.PositiveInfinity)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellGrid(0f, Vector2.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellGrid(float.NaN, Vector2.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellGrid(float.PositiveInfinity, Vector2.Zero));
    }
}
