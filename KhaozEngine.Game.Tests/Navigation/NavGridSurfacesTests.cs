using System;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavGridSurfacesTests
{
    [Fact]
    public void FromSurfaces_RecordsHeightField()
    {
        NavGrid grid = NavGrid.FromSurfaces(3, 3, 1f, 0f, 0f,
            (x, z) => new NavSurfaceSample(true, x + z, float.PositiveInfinity),
            stepHeight: 100f, agentHeight: 0f);

        Assert.True(grid.HasSurfaceHeights);
        Assert.Equal(2f, grid.SurfaceHeightAt(1, 1));
    }

    [Fact]
    public void FromSurfaces_BlockedCellHeightIsNull()
    {
        NavGrid grid = NavGrid.FromSurfaces(3, 3, 1f, 0f, 0f,
            (x, z) => (x, z) == (1, 1)
                ? new NavSurfaceSample(false, 0f, 0f)
                : new NavSurfaceSample(true, 5f, float.PositiveInfinity),
            stepHeight: 100f, agentHeight: 0f);

        Assert.Null(grid.SurfaceHeightAt(1, 1));
        Assert.Equal(0, grid.ClearanceAt(1, 1));
        Assert.Equal(5f, grid.SurfaceHeightAt(0, 0));
    }

    [Fact]
    public void FromSurfaces_StepRuleBlocksHigherCell()
    {
        NavGrid grid = NavGrid.FromSurfaces(3, 1, 1f, 0f, 0f,
            (x, z) => new NavSurfaceSample(true, x == 1 ? 10f : 0f, float.PositiveInfinity),
            stepHeight: 0.5f, agentHeight: 0f);

        Assert.Equal(0, grid.ClearanceAt(1, 0));
        Assert.True(grid.IsPassable(0, 0, 0f));
        Assert.True(grid.IsPassable(2, 0, 0f));
    }

    [Fact]
    public void FromSurfaces_ClearanceStillBakes()
    {
        NavGrid grid = NavGrid.FromSurfaces(6, 6, 1f, 0f, 0f,
            (_, _) => new NavSurfaceSample(true, 0f, float.PositiveInfinity),
            stepHeight: 100f, agentHeight: 0f);

        Assert.True(grid.ClearanceAt(3, 3) > 0);
        Assert.True(grid.IsPassable(3, 3, 0.2f));
    }

    [Fact]
    public void FromWalkable_HasNoHeightField()
    {
        NavGrid grid = NavGrid.FromWalkable(4, 4, 1f, 0f, 0f, (_, _) => true);

        Assert.False(grid.HasSurfaceHeights);
        Assert.Null(grid.SurfaceHeightAt(1, 1));
    }

    [Fact]
    public void FromSurfaces_NullSample_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            NavGrid.FromSurfaces(4, 4, 1f, 0f, 0f, null!, stepHeight: 0.5f, agentHeight: 1f));
    }

    [Theory]
    [InlineData(0, 4, 1f)]
    [InlineData(4, 0, 1f)]
    [InlineData(4, 4, 0f)]
    public void FromSurfaces_BadDims_Throw(int width, int height, float cellSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NavGrid.FromSurfaces(width, height, cellSize, 0f, 0f,
                (_, _) => new NavSurfaceSample(true, 0f, float.PositiveInfinity),
                stepHeight: 0.5f, agentHeight: 1f));
    }

    [Fact]
    public void FromSurfaces_Deterministic()
    {
        Func<int, int, NavSurfaceSample> sample = (x, z) =>
            new NavSurfaceSample((x + z) % 5 != 2, (x * 0.7f) + (z * 1.3f), 3f);

        NavGrid a = NavGrid.FromSurfaces(5, 5, 1f, 0f, 0f, sample, stepHeight: 0.75f, agentHeight: 1f);
        NavGrid b = NavGrid.FromSurfaces(5, 5, 1f, 0f, 0f, sample, stepHeight: 0.75f, agentHeight: 1f);

        for (int z = 0; z < 5; z++)
        {
            for (int x = 0; x < 5; x++)
            {
                Assert.Equal(a.ClearanceAt(x, z), b.ClearanceAt(x, z));
                Assert.Equal(a.SurfaceHeightAt(x, z), b.SurfaceHeightAt(x, z));
            }
        }
    }
}
