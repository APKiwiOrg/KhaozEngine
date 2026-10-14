using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavGridRestingHeightTests
{
    [Fact]
    public void RestingHeightDefaultsToSurfaceHeight()
    {
        NavGrid[] grids =
        {
            TopologyGrid(),
            NavGrid.FromSurfaces(2, 2, 1f, 0f, 0f,
                (x, z) => new NavSurfaceSample(x != z, 2f, 3f), 0.3f, 1.8f),
            NavGrid.FromWalkable(2, 2, 1f, 0f, 0f, (x, z) => x != z),
        };

        foreach (NavGrid grid in grids)
        {
            Assert.False(grid.HasRestingHeights);
            for (int z = -1; z <= grid.Height; z++)
                for (int x = -1; x <= grid.Width; x++)
                    Assert.Equal(grid.SurfaceHeightAt(x, z), grid.RestingHeightAt(x, z));
        }
    }

    [Fact]
    public void WithRestingHeightsKeepsTopology()
    {
        NavGrid source = TopologyGrid();
        float[] resting = { 0.0152f, float.NaN, float.PositiveInfinity, 0.3152f };

        NavGrid grid = source.WithRestingHeights(resting);

        Assert.NotSame(source, grid);
        Assert.True(grid.HasRestingHeights);
        Assert.False(source.HasRestingHeights);
        Assert.Equal(source.HasSurfaceHeights, grid.HasSurfaceHeights);
        Assert.Equal(source.Width, grid.Width);
        Assert.Equal(source.Height, grid.Height);
        Assert.Equal(source.CellSize, grid.CellSize);
        Assert.Equal(source.OriginX, grid.OriginX);
        Assert.Equal(source.OriginZ, grid.OriginZ);
        Assert.Equal(source.YawRadians, grid.YawRadians);
        Assert.Equal(source.YMin, grid.YMin);
        Assert.Equal(source.YMax, grid.YMax);
        for (int z = 0; z < source.Height; z++)
            for (int x = 0; x < source.Width; x++)
            {
                Assert.Equal(source.SurfaceHeightAt(x, z), grid.SurfaceHeightAt(x, z));
                Assert.Equal(source.ClearanceAt(x, z), grid.ClearanceAt(x, z));
                Assert.Equal(source.CellCenter(x, z), grid.CellCenter(x, z));
                Assert.Equal(source.IsPassable(x, z, 0f), grid.IsPassable(x, z, 0f));
                Assert.Equal(source.IsPassable(x, z, 0.8f), grid.IsPassable(x, z, 0.8f));
            }
        Assert.Equal(0.0152f, grid.RestingHeightAt(0, 0));
        Assert.Equal(0.3152f, grid.RestingHeightAt(1, 1));
        Assert.Null(grid.RestingHeightAt(1, 0));
        Assert.Null(grid.RestingHeightAt(0, 1));
        Assert.Null(grid.RestingHeightAt(-1, 0));
        Assert.Null(grid.RestingHeightAt(0, 2));
    }

    [Fact]
    public void WithRestingHeightsCopiesAndReplacesTheSuppliedArray()
    {
        NavGrid source = OneCell(0f);
        float[] resting = { 0.0152f };
        NavGrid first = source.WithRestingHeights(resting);
        resting[0] = float.NaN;
        NavGrid second = first.WithRestingHeights(new[] { 0.02f });

        Assert.Equal(0f, source.RestingHeightAt(0, 0));
        Assert.Equal(0.0152f, first.RestingHeightAt(0, 0));
        Assert.Equal(0.02f, second.RestingHeightAt(0, 0));
        Assert.Equal(0f, second.SurfaceHeightAt(0, 0));
    }

    [Fact]
    public void WithRestingHeightsRejectsNull()
        => Assert.Throws<ArgumentException>(() => OneCell(0f).WithRestingHeights(null!));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void WithRestingHeightsRejectsTheWrongCellCount(int count)
        => Assert.Throws<ArgumentException>(() => TopologyGrid().WithRestingHeights(new float[count]));

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void WithRestingHeightsRejectsNonFiniteOpenCells(float height)
        => Assert.Throws<ArgumentOutOfRangeException>(() => OneCell(0f).WithRestingHeights(new[] { height }));

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void WithRestingHeightsIgnoresNonFiniteBlockedCells(float height)
    {
        NavGrid blocked = NavGrid.FromBlockedSurfaces(1, 1, 1f, 0f, 0f, new[] { true }, new[] { 0f });
        NavGrid grid = blocked.WithRestingHeights(new[] { height });

        Assert.True(grid.HasRestingHeights);
        Assert.Null(grid.RestingHeightAt(0, 0));
        Assert.Null(grid.SurfaceHeightAt(0, 0));
    }

    [Fact]
    public void RegionGoalUsesRestingHeight()
    {
        NavGrid grid = OneCell(0f).WithRestingHeights(new[] { 0.0152f });
        var planner = new GridPathPlanner(NavSpace.Single(grid));
        var start = new Vector3(0.5f, 0.0152f, 0.5f);
        var restingGoal = new NavGoalRegion(start, 0f, feet => MathF.Abs(feet.Y - 0.0152f) <= 0.001f);
        var rawGoal = new NavGoalRegion(new Vector3(0.5f, 0f, 0.5f), 0f,
            feet => MathF.Abs(feet.Y) <= 0.001f);
        var budget = new PathQueryBudget { MaxExpandedNodes = 10, SnapRadius = 0f };

        Assert.Equal(NavPathStatus.Complete, planner.FindPath(start, restingGoal, 0f, budget).Status);
        Assert.Equal(NavPathStatus.Unreachable, planner.FindPath(start, rawGoal, 0f, budget).Status);
    }

    [Fact]
    public void LayerAtUsesRestingHeight()
    {
        NavGrid lower = OneCell(0f, -0.05f, 0.05f).WithRestingHeights(new[] { 0.29f });
        NavGrid upper = OneCell(0.3f, 0.25f, 0.35f).WithRestingHeights(new[] { 0.3f });
        var space = new NavSpace(new[] { lower, upper });

        Assert.Equal(0, space.LayerAt(new Vector3(0.5f, 0.29f, 0.5f)));
        Assert.Equal(1, space.LayerAt(new Vector3(0.5f, 0.3f, 0.5f)));
        Assert.Equal(1, space.LayerOf(0.29f));
        Assert.Equal(1, space.LayerAt(new Vector3(2f, 0.29f, 2f)));
    }

    static NavGrid OneCell(float height, float yMin = float.NegativeInfinity, float yMax = float.PositiveInfinity)
        => NavGrid.FromBlockedSurfaces(1, 1, 1f, 0f, 0f, new[] { false }, new[] { height }, yMin, yMax);

    static NavGrid TopologyGrid()
        => NavGrid.FromBlockedSurfaces(2, 2, 2f, 3f, -4f,
            new[] { false, true, true, false }, new[] { 0f, float.NaN, float.PositiveInfinity, 0.3f },
            yMin: -1f, yMax: 2f, yawRadians: 0.3f);
}
