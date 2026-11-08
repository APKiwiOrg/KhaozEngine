using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavDropPlannerTests
{
    [Fact]
    public void AdjacentDropPreservesBothAnchorsAndDirection()
    {
        var (space, graph) = Chain();
        var planner = new GridPathPlanner(space, graph);
        NavPath path = planner.FindPath(new(0.6f, 4, 0.5f), new Vector3(1.6f, 2, 0.5f), 0, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[] { new NavWaypoint(new(0.5f, 0.5f), 0), new NavWaypoint(new(1.5f, 0.5f), 0) { Kind = NavWaypointKind.Drop } }, path.Waypoints);
        Assert.Equal(NavPathStatus.Unreachable, planner.FindPath(new(1.5f, 2, 0.5f), new Vector3(0.5f, 4, 0.5f), 0, PathQueryBudget.Default).Status);
    }

    [Fact]
    public void ConsecutiveDropsKeepTheSharedAnchorAndFinalLanding()
    {
        var (space, graph) = Chain();
        var planner = new GridPathPlanner(space, graph);
        NavPath point = planner.FindPath(new(0.5f, 4, 0.5f), new Vector3(2.6f, 0, 0.5f), 0, PathQueryBudget.Default);
        NavPath region = planner.FindPath(new(0.5f, 4, 0.5f), new NavGoalRegion(new(2.5f, 0, 0.5f), 0, p => p == new Vector3(2.5f, 0, 0.5f)), 0, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, point.Status);
        Assert.Equal(point.Waypoints, region.Waypoints);
        Assert.Equal(new[] { NavWaypointKind.Walk, NavWaypointKind.Drop, NavWaypointKind.Drop }, point.Waypoints.Select(w => w.Kind));
        Assert.False(point.IsCollinearPassThrough(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DropCostDoesNotUndercutTheCheaperOrdinaryRoute(bool ordinaryExit)
    {
        var drop = new NavLink(0, 0, 0, 0, 1, 0) { Kind = NavLinkKind.Drop };
        var grid = NavGrid.FromBlockedSurfaces(2, 2, 1, 0, 0, new bool[4], new float[] { 4, 0, 4, 0 });
        var space = new NavSpace(new[] { grid }, new[] { drop });
        byte[] exits = { ordinaryExit ? (byte)5 : (byte)4, 0, 1, 8 };
        var graph = new NavTraversalGraph(space, 0, 1, new[] { new NavTraversalLayer(2, 2, new[] { true, true, true, true }, exits) }, new[] { drop });
        NavPath path = new GridPathPlanner(space, graph).FindPath(new(0.5f, 4, 0.5f), new Vector3(1.5f, 0, 0.5f), 0, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.DoesNotContain(path.Waypoints, w => w.Kind == NavWaypointKind.Drop);
        Assert.Equal(ordinaryExit ? 1 : 3, path.Waypoints.Count);
    }

    // FM-D15 keeps existing routes and lets a pursuit ask for the longer supported walk.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WalkOnlySkipsTheCheaperDropButKeepsAWalkingDetour(bool walkOnly, bool region)
    {
        var drop = new NavLink(0, 0, 0, 0, 1, 0) { Kind = NavLinkKind.Drop };
        var grid = NavGrid.FromBlockedSurfaces(2, 4, 1, 0, 0, new bool[8], new float[] { 4, 0, 4, 0, 4, 0, 4, 0 });
        var space = new NavSpace(new[] { grid }, new[] { drop });
        var graph = new NavTraversalGraph(space, 0, 1,
            new[] { new NavTraversalLayer(2, 4, Enumerable.Repeat(true, 8).ToArray(), new byte[] { 4, 0, 4, 8, 4, 8, 1, 8 }) }, new[] { drop });
        var planner = new GridPathPlanner(space, graph);
        Vector3 start = new(0.5f, 4, 0.5f), end = new(1.5f, 0, 0.5f);
        PathQueryBudget budget = PathQueryBudget.Default with { WalkOnly = walkOnly };
        NavPath path = region ? planner.FindPath(start, new NavGoalRegion(end, 0, p => p == end), 0, budget)
            : planner.FindPath(start, end, 0, budget);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(!walkOnly, path.Waypoints.Any(w => w.Kind == NavWaypointKind.Drop));
        Assert.Equal(walkOnly ? 7 : 2, path.Waypoints.Count);
    }

    [Theory]
    [InlineData(NavLinkKind.Drop, false)]
    [InlineData(NavLinkKind.Hop, false)]
    [InlineData(NavLinkKind.Stair, true)]
    public void WalkOnlyKeepsStairsAndRefusesAnOnlyHopOrDrop(NavLinkKind kind, bool complete)
    {
        var grid = NavGrid.FromBlockedSurfaces(2, 1, 1, 0, 0, new bool[2], new float[] { 1, 0 });
        var link = new NavLink(0, 0, 0, 0, 1, 0) { Kind = kind };
        var space = new NavSpace(new[] { grid }, new[] { link });
        var graph = new NavTraversalGraph(space, 0, 1,
            new[] { new NavTraversalLayer(2, 1, new[] { true, true }, new byte[2]) }, new[] { link });
        var planner = new GridPathPlanner(space, graph);
        Assert.Equal(NavPathStatus.Complete, planner.FindPath(new(0.5f, 1, 0.5f), new Vector3(1.5f, 0, 0.5f), 0, PathQueryBudget.Default).Status);
        NavPath path = planner.FindPath(new(0.5f, 1, 0.5f), new Vector3(1.5f, 0, 0.5f), 0,
            PathQueryBudget.Default with { WalkOnly = true });
        Assert.Equal(complete, path.Status == NavPathStatus.Complete);
        if (complete)
        {
            Assert.NotEmpty(path.Waypoints);
            Assert.All(path.Waypoints, waypoint => Assert.Equal(NavWaypointKind.Walk, waypoint.Kind));
        }
    }

    [Fact]
    public void UnguardedDropAtFirstEdgeKeepsItsSourceAndAppendsExactGoal()
    {
        var upper = NavGrid.FromBlockedSurfaces(1, 1, 1, 0, 0, new bool[1], new float[] { 4 });
        var lower = NavGrid.FromBlockedSurfaces(1, 1, 1, 1, 0, new bool[1], new float[] { 0 });
        var link = new NavLink(0, 0, 0, 1, 0, 0) { Kind = NavLinkKind.Drop };
        var space = new NavSpace(new[] { upper, lower }, new[] { link });
        NavPath path = new GridPathPlanner(space).FindPath(new(0.5f, 4, 0.5f), new Vector3(1.6f, 0, 0.5f), 0, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[] { new NavWaypoint(new(0.5f, 0.5f), 0), new NavWaypoint(new(1.5f, 0.5f), 1) { Kind = NavWaypointKind.Drop }, new NavWaypoint(new(1.6f, 0.5f), 1) }, path.Waypoints);
    }

    internal static (NavSpace Space, NavTraversalGraph Graph) Chain()
    {
        var grid = NavGrid.FromBlockedSurfaces(3, 1, 1, 0, 0, new bool[3], new float[] { 4, 2, 0 });
        var links = new[] { new NavLink(0, 0, 0, 0, 1, 0) { Kind = NavLinkKind.Drop }, new NavLink(0, 1, 0, 0, 2, 0) { Kind = NavLinkKind.Drop } };
        var space = new NavSpace(new[] { grid }, links);
        return (space, new NavTraversalGraph(space, 0, 1, new[] { new NavTraversalLayer(3, 1, new[] { true, true, true }, new byte[3]) }, links));
    }
}
