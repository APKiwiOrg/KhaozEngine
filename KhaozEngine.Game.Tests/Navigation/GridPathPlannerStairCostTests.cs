using System;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class GridPathPlannerStairCostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StairsCoverTheirMetricSpanAndKeepTheSourceCellMinimum(bool differentFrames)
    {
        // #1712 owner ruling: stairs never discount spatial travel, including across frames.
        NavGrid lower = NavGrid.FromBlockedSurfaces(4, 4, 0.5f, 0f, 0f, new bool[16], new float[16]);
        NavGrid upper = NavGrid.FromBlockedSurfaces(4, 4, differentFrames ? 1f : 0.5f,
            differentFrames ? 2f : 0f, 0f, new bool[16], new float[16]);
        var diagonal = new NavLink(0, 0, 0, 1, 1, 1);
        var space = new NavSpace(new[] { lower, upper }, new[] { diagonal });
        var planner = new GridPathPlanner(space);
        // Shared frame: half-metre diagonal. Different frames: centres (0.25, 0.25), (3.5, 1.5).
        double span = differentFrames ? Math.Sqrt(3.25 * 3.25 + 1.25 * 1.25) : 0.5f * MathF.Sqrt(2f);
        Assert.True(planner.LinkCostMetersForTests(diagonal) >= span);
        if (differentFrames) Assert.InRange(planner.SpatialBoundScaleForTests, 0.99f, 1f);
        else Assert.Equal(1f, planner.SpatialBoundScaleForTests);

        var vertical = new NavLink(0, 0, 0, 1, 0, 0);
        var shared = new GridPathPlanner(new NavSpace(new[] { lower, lower }, new[] { vertical }));
        Assert.Equal(lower.CellSize, shared.LinkCostMetersForTests(vertical));
        Assert.Equal(1f, shared.SpatialBoundScaleForTests);
    }
}
