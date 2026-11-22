using System;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

/// <summary>Asserts slicing and exact route equality on the navigation fixtures.</summary>
internal static partial class GridSearchRequestFixtures
{
    internal static IIncrementalRegionPathPlanner Incremental(GridPathPlanner planner)
        => Assert.IsAssignableFrom<IIncrementalRegionPathPlanner>(planner);

    internal static NavPath Finish(PathPlanRequest request, int quantum)
    {
        for (int advance = 0; request.State == PathPlanState.Pending; advance++)
        {
            Assert.True(advance < 2_000_000, "the request did not finish");
            long before = request.WorkSpent;
            request.Advance(new PathWorkBudget(quantum));
            if (request.State == PathPlanState.Pending) Assert.True(request.WorkSpent > before);
        }
        Assert.Equal(PathPlanState.Ready, request.State);
        return Assert.IsType<NavPath>(request.Result);
    }

    internal static void EqualBits(NavPath expected, NavPath actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Waypoints.Count, actual.Waypoints.Count);
        for (int i = 0; i < expected.Waypoints.Count; i++)
        {
            NavWaypoint want = expected.Waypoints[i], got = actual.Waypoints[i];
            Assert.Equal(want.Kind, got.Kind);
            Assert.Equal(want.Layer, got.Layer);
            Assert.Equal(BitConverter.SingleToInt32Bits(want.Position.X), BitConverter.SingleToInt32Bits(got.Position.X));
            Assert.Equal(BitConverter.SingleToInt32Bits(want.Position.Y), BitConverter.SingleToInt32Bits(got.Position.Y));
        }
    }
}
