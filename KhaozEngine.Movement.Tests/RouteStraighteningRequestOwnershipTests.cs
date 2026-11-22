using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Navigation.GridPathPlanner;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;

namespace KhaozEngine.Tests.Movement;

public sealed partial class RouteStraighteningRequestTests
{
    [Fact]
    public void FactoryCannotReplaceTheAdmittedExpansionCap()
    {
        var navigation = Flat(9, 6, 0.25f);
        using var profilePool = navigation.CreatePlanningWorkspaces(1, Budget);
        var grid = Assert.IsType<GridPlanningWorkspaces>(profilePool).Planner;
        PathQueryBudget capOne = Budget with { MaxExpandedNodes = 1 };
        using var pool = Assert.IsType<GridPlanningWorkspaces>(grid.CreateRequestWorkspaces(grid, 1, capOne));
        var goal = Goal(Start with { X = 4.5f });

        Assert.Throws<ArgumentException>(() =>
        {
            using var oversized = pool.Begin(Start, goal, navigation.AgentRadius, capOne,
                owner => new GridSearchRequest(owner, Start, goal, navigation.AgentRadius, Budget));
        });

        using var next = pool.Begin(Start, goal, navigation.AgentRadius, capOne, null);
        Assert.Equal(NavPathStatus.Partial, Finish(next, [6]).Status);
        Assert.Equal(0, pool.Waits);
    }

    [Fact]
    public void ProfilePoolRefusesRawBeginThatWouldBypassStartAdmission()
    {
        var navigation = Flat(9, 6, 0.25f);
        var start = new Vector3(0.125f, 0f, 2.5f);
        var goal = Goal(new(3.5f, 0f, 2.5f));
        using var pool = Assert.IsType<GridPlanningWorkspaces>(navigation.CreatePlanningWorkspaces(1, Budget));

        Assert.Throws<ArgumentException>(() =>
        {
            using var raw = pool.Begin(start, goal, navigation.AgentRadius, Budget, null);
        });

        using var profile = ((IIncrementalRegionPathPlanner)navigation.Planner)
            .BeginPath(start, goal, navigation.AgentRadius, Budget, pool);
        Assert.Same(NavPath.Unreachable, Finish(profile, [1]));
        Assert.Equal(0, pool.Waits);
    }

    [Fact]
    public void ProfilePoolRefusesARequestWithoutItsAdapter()
    {
        var navigation = Flat(9, 6, 0.25f);
        using var pool = Assert.IsType<GridPlanningWorkspaces>(navigation.CreatePlanningWorkspaces(1, Budget));
        var goal = Goal(Start with { X = 4.5f });

        Assert.Throws<ArgumentException>(() =>
        {
            using var raw = pool.Begin(Start, goal, navigation.AgentRadius, Budget,
                owner => new GridSearchRequest(owner, Start, goal, navigation.AgentRadius, Budget));
        });

        using var profile = ((IIncrementalRegionPathPlanner)navigation.Planner)
            .BeginPath(Start, goal, navigation.AgentRadius, Budget, pool);
        AssertBits(Path(NavPathStatus.Complete, Waypoint(2.5f), Waypoint(3.5f), Waypoint(4.5f)), Finish(profile, [6]));
        Assert.Equal(0, pool.Waits);
    }

    [Fact]
    public void BakedProfileCannotPairAnExcludingFootprintWithTheSourceGraph()
    {
        var source = Flat(3, 1, 0f, (x, _) => x == 1 ? 1u : 0u);
        var view = source.ExcludingAreas(1u);
        var tuning = MoveTuning.Default with { CapsuleRadius = 0f };
        var hybrid = new GroundNavigation(source.Graph, tuning, view.Footprint);
        var start = new Vector3(0.5f, 0f, 0.5f);
        var end = new Vector3(2.5f, 0f, 0.5f);
        var expected = Path(NavPathStatus.Complete, new NavWaypoint(new(1.5f, 0.5f), 0), new NavWaypoint(new(2.5f, 0.5f), 0));

        AssertBits(expected, hybrid.Planner.FindPath(start, Goal(end), 0f, Budget));
        Assert.False(hybrid.AllowsSegment(start, end));
        Assert.False(hybrid.SupportsRequests);
        Assert.False(new RouteStraightener(hybrid).SupportsRequests);
    }
}
