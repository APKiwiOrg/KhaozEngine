using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class GuardedGridPathPlannerTests
{
    static readonly PathQueryBudget NoSnap = new() { MaxExpandedNodes = 100, SnapRadius = 0f };

    [Fact]
    public void LegacyOpenPointQueryKeepsTheExactGoalWaypoint()
    {
        NavGrid grid = NavGrid.FromWalkable(5, 3, 1f, 0f, 0f, (_, _) => true);
        NavPath path = new GridPathPlanner(NavSpace.Single(grid)).FindPath(
            new Vector3(0.5f, 0f, 1.5f), new Vector3(4.2f, 0f, 1.8f), 0.2f);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[] { new NavWaypoint(new Vector2(4.2f, 1.8f), 0) }, path.Waypoints);
    }

    [Fact]
    public void LegacyStairPointQueryKeepsSmoothedRunsAndTheExactGoal()
    {
        NavGrid lower = NavGrid.FromWalkable(4, 1, 1f, 0f, 0f, (_, _) => true, 0f, 1f);
        NavGrid upper = NavGrid.FromWalkable(4, 1, 1f, 0f, 0f, (_, _) => true, 2f, 3f);
        var space = new NavSpace(new[] { lower, upper }, new[] { new NavLink(0, 1, 0, 1, 1, 0) });
        NavPath path = new GridPathPlanner(space).FindPath(
            new Vector3(0.5f, 0f, 0.5f), new Vector3(3.2f, 2f, 0.8f), 0.2f);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[]
        {
            new NavWaypoint(new Vector2(1.5f, 0.5f), 0),
            new NavWaypoint(new Vector2(1.5f, 0.5f), 1),
            new NavWaypoint(new Vector2(3.2f, 0.8f), 1),
        }, path.Waypoints);
    }

    [Fact]
    public void PassableNodesWithARefusedEdgeNeverProduceACrossing()
    {
        NavSpace space = NavSpace.Single(NavGrid.FromWalkable(2, 1, 1f, 0f, 0f, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0.3f, 1.8f,
            new[] { new NavTraversalLayer(2, 1, new[] { true, true }, new byte[2]) }, Array.Empty<NavLink>());
        NavPath path = new GridPathPlanner(space, graph).FindPath(
            new Vector3(0.5f, 0f, 0.5f), new Vector3(1.5f, 0f, 0.5f), 0.3f, NoSnap);

        Assert.Equal(NavPathStatus.Unreachable, path.Status);
        Assert.Empty(path.Waypoints);
    }

    [Fact]
    public void GuardedDetourKeepsEveryValidatedCellCenter()
    {
        NavSpace space = NavSpace.Single(NavGrid.FromWalkable(3, 2, 1f, 0f, 0f, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0.3f, 1.8f,
            new[] { new NavTraversalLayer(3, 2, new[] { true, true, true, true, true, true },
                new byte[] { 4, 0, 0, 1, 1, 8 }) }, Array.Empty<NavLink>());
        NavPath path = new GridPathPlanner(space, graph).FindPath(
            new Vector3(0.5f, 0f, 0.5f), new Vector3(2.8f, 0f, 0.8f), 0.3f, NoSnap);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[]
        {
            new NavWaypoint(new Vector2(1.5f, 0.5f), 0),
        }, new GridPathPlanner(space).FindPath(
            new Vector3(0.5f, 0f, 0.5f), new Vector3(1.5f, 0f, 0.5f), 0.3f, NoSnap).Waypoints);
        Assert.Equal(new[]
        {
            new NavWaypoint(new Vector2(0.5f, 1.5f), 0),
            new NavWaypoint(new Vector2(1.5f, 1.5f), 0),
            new NavWaypoint(new Vector2(2.5f, 1.5f), 0),
            new NavWaypoint(new Vector2(2.5f, 0.5f), 0),
        }, path.Waypoints);
    }

    [Fact]
    public void AlreadyClearedNarrowCorridorIsNotErodedAgain()
    {
        NavGrid grid = NavGrid.FromWalkable(3, 1, 1f, 0f, 0f, (_, _) => true);
        NavSpace space = NavSpace.Single(grid);
        var graph = new NavTraversalGraph(space, 1.1f, 1.8f,
            new[] { new NavTraversalLayer(3, 1, new[] { true, true, true }, new byte[] { 1, 1, 0 }) },
            Array.Empty<NavLink>());
        NavPath path = new GridPathPlanner(space, graph).FindPath(
            new Vector3(0.5f, 0f, 0.5f), new Vector3(2.5f, 0f, 0.5f), 1.1f, NoSnap);

        Assert.False(grid.IsPassable(1, 0, 1.1f));
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[]
        {
            new NavWaypoint(new Vector2(1.5f, 0.5f), 0),
            new NavWaypoint(new Vector2(2.5f, 0.5f), 0),
        }, path.Waypoints);
    }

    [Fact]
    public void OnlyTheAcceptedStairDirectionCanBeExpanded()
    {
        NavGrid lower = NavGrid.FromWalkable(1, 1, 1f, 0f, 0f, (_, _) => true, 0f, 1f);
        NavGrid upper = NavGrid.FromWalkable(1, 1, 1f, 0f, 0f, (_, _) => true, 2f, 3f);
        var up = new NavLink(0, 0, 0, 1, 0, 0);
        var down = new NavLink(1, 0, 0, 0, 0, 0);
        var space = new NavSpace(new[] { lower, upper }, new[] { up, down });
        var graph = new NavTraversalGraph(space, 0.8f, 1.8f, new[]
        {
            new NavTraversalLayer(1, 1, new[] { true }, new byte[1]),
            new NavTraversalLayer(1, 1, new[] { true }, new byte[1]),
        }, new[] { up });
        var planner = new GridPathPlanner(space, graph);
        var bottom = new Vector3(0.5f, 0f, 0.5f);
        var top = new Vector3(0.5f, 2f, 0.5f);

        NavPath ascent = planner.FindPath(bottom, top, 0.8f, NoSnap);
        Assert.Equal(NavPathStatus.Complete, ascent.Status);
        Assert.Equal(new[]
        {
            new NavWaypoint(new Vector2(0.5f, 0.5f), 0),
            new NavWaypoint(new Vector2(0.5f, 0.5f), 1),
        }, ascent.Waypoints);
        Assert.Equal(NavPathStatus.Unreachable, planner.FindPath(top, bottom, 0.8f, NoSnap).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARejectedEndpointFailsToSnap(bool rejectStart)
    {
        NavSpace space = NavSpace.Single(NavGrid.FromWalkable(2, 1, 1f, 0f, 0f, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(2, 1, new[] { !rejectStart, rejectStart }, new byte[2]) },
            Array.Empty<NavLink>());
        NavPath path = new GridPathPlanner(space, graph).FindPath(
            new Vector3(0.5f, 0f, 0.5f), new Vector3(1.5f, 0f, 0.5f), 0f, NoSnap);

        Assert.Equal(NavPathStatus.Unreachable, path.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PositiveSnapRadiusCannotCrossRejectedCells(bool rejectStart)
    {
        NavSpace space = NavSpace.Single(NavGrid.FromWalkable(3, 1, 1f, 0f, 0f, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(3, 1, new[] { !rejectStart, false, rejectStart }, new byte[3]) },
            Array.Empty<NavLink>());
        var budget = new PathQueryBudget { MaxExpandedNodes = 100, SnapRadius = 3f };
        NavPath path = new GridPathPlanner(space, graph).FindPath(
            new Vector3(0.5f, 0f, 0.5f), new Vector3(2.5f, 0f, 0.5f), 0f, budget);

        Assert.Equal(NavPathStatus.Unreachable, path.Status);
        Assert.Empty(path.Waypoints);
    }

    [Fact]
    public void RawGridBlockingStillRejectsAnAcceptedNode()
    {
        NavSpace space = NavSpace.Single(NavGrid.FromWalkable(2, 1, 1f, 0f, 0f, (x, _) => x == 0));
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(2, 1, new[] { true, true }, new byte[] { 1, 0 }) },
            Array.Empty<NavLink>());
        NavPath path = new GridPathPlanner(space, graph).FindPath(
            new Vector3(0.5f, 0f, 0.5f), new Vector3(1.5f, 0f, 0.5f), 0f, NoSnap);

        Assert.Equal(NavPathStatus.Unreachable, path.Status);
    }

    [Fact]
    public void AcceptedDiagonalStillNeedsBothCompanionNodes()
    {
        NavSpace space = NavSpace.Single(NavGrid.FromWalkable(2, 2, 1f, 0f, 0f, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(2, 2, new[] { true, false, false, true },
                new byte[] { 16, 0, 0, 0 }) }, Array.Empty<NavLink>());
        NavPath path = new GridPathPlanner(space, graph).FindPath(
            new Vector3(0.5f, 0f, 0.5f), new Vector3(1.5f, 0f, 1.5f), 0f, NoSnap);

        Assert.Equal(NavPathStatus.Unreachable, path.Status);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.300001f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void QueryRadiusMustExactlyMatchTheProfile(float radius)
    {
        NavSpace space = NavSpace.Single(NavGrid.FromWalkable(1, 1, 1f, 0f, 0f, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0.3f, 1f,
            new[] { new NavTraversalLayer(1, 1, new[] { true }, new byte[1]) }, Array.Empty<NavLink>());

        Assert.Throws<ArgumentException>(() => new GridPathPlanner(space, graph).FindPath(
            Vector3.Zero, Vector3.Zero, radius, NoSnap));
    }

    [Fact]
    public void GuardedConstructorRejectsDifferentGridsAndLinkTopology()
    {
        NavGrid grid = NavGrid.FromWalkable(2, 1, 1f, 0f, 0f, (_, _) => true);
        var link = new NavLink(0, 0, 0, 0, 1, 0);
        var space = new NavSpace(new[] { grid }, new[] { link });
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(2, 1, new[] { true, true }, new byte[2]) }, new[] { link });

        Assert.Throws<ArgumentException>(() => new GridPathPlanner(
            NavSpace.Single(NavGrid.FromWalkable(2, 1, 1f, 0f, 0f, (_, _) => true)), graph));
        Assert.Throws<ArgumentException>(() => new GridPathPlanner(NavSpace.Single(grid), graph));
        Assert.Throws<ArgumentException>(() => new GridPathPlanner(
            new NavSpace(new[] { grid, grid }, new[] { link }), graph));
        Assert.Throws<ArgumentNullException>(() => new GridPathPlanner(space, null!));
        Assert.Throws<ArgumentNullException>(() => new GridPathPlanner(null!, graph));
    }

    [Fact]
    public void PlannerRetainsTheGraphSnapshotAfterCallerMutation()
    {
        NavGrid grid = NavGrid.FromWalkable(2, 1, 1f, 0f, 0f, (_, _) => true);
        var layers = new List<NavGrid> { grid };
        var links = new List<NavLink>();
        var space = new NavSpace(layers, links);
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(2, 1, new[] { true, true }, new byte[] { 1, 0 }) }, links);
        var planner = new GridPathPlanner(space, graph);
        layers.Clear();
        links.Add(new NavLink(0, 1, 0, 0, 0, 0));

        NavPath path = planner.FindPath(new Vector3(0.5f, 0f, 0.5f), new Vector3(1.5f, 0f, 0.5f), 0f, NoSnap);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[] { new NavWaypoint(new Vector2(1.5f, 0.5f), 0) }, path.Waypoints);
        Assert.Equal(NavPathStatus.Unreachable, planner.FindPath(
            new Vector3(1.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f), 0f, NoSnap).Status);
    }
}
