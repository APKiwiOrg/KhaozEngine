using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavGridTests
{
    static NavGrid MakeBlockedColumnGrid()
        => NavGrid.FromWalkable(6, 6, 1f, 0f, 0f, (x, z) => x != 3);

    [Fact]
    public void FromWalkable_BlockedColumn_ClearanceIsZero()
    {
        NavGrid grid = MakeBlockedColumnGrid();
        for (int z = 0; z < 6; z++)
            Assert.Equal(0, grid.ClearanceAt(3, z));
    }

    [Fact]
    public void FromWalkable_BlockedColumn_NeighborIsPassableAtSmallRadius()
    {
        NavGrid grid = MakeBlockedColumnGrid();
        Assert.True(grid.IsPassable(2, 2, 0.2f));
    }

    [Fact]
    public void FromWalkable_BlockedColumn_NeighborIsPassableAtZeroRadius()
    {
        NavGrid grid = MakeBlockedColumnGrid();
        Assert.True(grid.IsPassable(2, 2, 0f));
    }

    [Fact]
    public void FromWalkable_BlockedColumn_BlockedCellIsNeverPassable()
    {
        NavGrid grid = MakeBlockedColumnGrid();
        Assert.False(grid.IsPassable(3, 2, 0f));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(5, 0)]
    [InlineData(0, 5)]
    [InlineData(5, 5)]
    public void CellOf_RoundTrips_CellCenter_WithNegativeOrigin(int cx, int cz)
    {
        NavGrid grid = NavGrid.FromWalkable(6, 6, 1.5f, -8f, 3f, (_, _) => true);
        Vector2 center = grid.CellCenter(cx, cz);
        (int x, int z) = grid.CellOf(center.X, center.Y);
        Assert.Equal(cx, x);
        Assert.Equal(cz, z);
    }

    [Fact]
    public void ClearanceAt_OutOfBounds_ReturnsZero()
    {
        NavGrid grid = NavGrid.FromWalkable(4, 4, 1f, 0f, 0f, (_, _) => true);
        Assert.Equal(0, grid.ClearanceAt(-1, 0));
        Assert.Equal(0, grid.ClearanceAt(0, -1));
        Assert.Equal(0, grid.ClearanceAt(4, 0));
        Assert.Equal(0, grid.ClearanceAt(0, 4));
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(5f, true)]
    [InlineData(10f, true)]
    [InlineData(-0.1f, false)]
    [InlineData(10.1f, false)]
    public void ContainsY_RespectsBand(float y, bool expected)
    {
        NavGrid grid = NavGrid.FromWalkable(2, 2, 1f, 0f, 0f, (_, _) => true, yMin: 0f, yMax: 10f);
        Assert.Equal(expected, grid.ContainsY(y));
    }

    [Fact]
    public void FromWalkable_NullPredicate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NavGrid.FromWalkable(4, 4, 1f, 0f, 0f, null!));
    }

    [Fact]
    public void FromWalkable_NonPositiveWidth_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NavGrid.FromWalkable(0, 4, 1f, 0f, 0f, (_, _) => true));
    }

    [Fact]
    public void FromWalkable_NonPositiveHeight_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NavGrid.FromWalkable(4, 0, 1f, 0f, 0f, (_, _) => true));
    }

    [Fact]
    public void FromWalkable_NonPositiveCellSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NavGrid.FromWalkable(4, 4, 0f, 0f, 0f, (_, _) => true));
    }
}
