using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;

namespace KhaozEngine.Tests.Movement;

public sealed class GroundNavigationPlanningWorkspaceTests
{
    [Fact]
    public void CountOnlyFactoryPrewarmsTheProfilesDefaultQueryBudget()
    {
        var navigation = Flat(2, 1, 0f);
        var planner = (IIncrementalRegionPathPlanner)navigation.Planner;
        var start = new Vector3(0.5f, 0f, 0.5f);
        var goal = Goal(new Vector3(1.5f, 0f, 0.5f));
        using var workspaces = navigation.CreatePlanningWorkspaces(2);

        Assert.Equal(2, workspaces.Count);
        PathQueryBudget budget = PathQueryBudget.Default;
        using var request = planner.BeginPath(start, goal, 0f, budget, workspaces);
        request.Advance(new PathWorkBudget(int.MaxValue));

        var route = Assert.IsType<NavPath>(request.Result);
        Assert.Equal(NavPathStatus.Complete, route.Status);
        var step = Assert.Single(route.Waypoints);
        Assert.Equal(0, step.Layer);
        Assert.Equal(NavWaypointKind.Walk, step.Kind);
        Assert.Equal(0x3fc00000, BitConverter.SingleToInt32Bits(step.Position.X));
        Assert.Equal(0x3f000000, BitConverter.SingleToInt32Bits(step.Position.Y));
        Assert.Throws<ArgumentException>("budget", () =>
        {
            using var oversized = planner.BeginPath(start, goal, 0f,
                budget with { MaxExpandedNodes = budget.MaxExpandedNodes + 1 }, workspaces);
        });
    }

    [Fact]
    public void CountOnlyFactoryRefusesZeroWorkspaces()
    {
        var navigation = Flat(2, 1, 0f);

        Assert.Throws<ArgumentOutOfRangeException>("count", () =>
        {
            using var empty = navigation.CreatePlanningWorkspaces(0);
        });
    }

    [Fact]
    public void CountOnlyFactoryRefusesAreaExcludingViews()
    {
        var navigation = Flat(2, 1, 0f, (x, _) => x == 1 ? 1u : 0u);
        var view = navigation.ExcludingAreas(1u);
        using var accepted = navigation.CreatePlanningWorkspaces(1);

        Assert.Throws<NotSupportedException>(() =>
        {
            using var excluded = view.CreatePlanningWorkspaces(1);
        });
    }
}
