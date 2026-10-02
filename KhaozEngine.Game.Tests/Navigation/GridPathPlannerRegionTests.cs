using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class GridPathPlannerRegionTests
{
    static readonly PathQueryBudget Budget = new() { MaxExpandedNodes = 100, SnapRadius = 0f };

    [Fact]
    public void ReachableFarSideWinsWhenNearestRegionCellsAreBlocked()
    {
        NavGrid grid = SurfaceGrid(7, 5, (x, z) => !(x == 3 && z >= 1 && z <= 3));
        var region = new NavGoalRegion(new Vector3(3.5f, 0f, 2.5f), 1f,
            feet => feet.Z == 2.5f && (feet.X == 3.5f || feet.X == 4.5f));
        NavSpace space = NavSpace.Single(grid);
        IRegionPathPlanner planner = new GridPathPlanner(space);

        NavPath path = planner.FindPath(new Vector3(0.5f, 0f, 2.5f), region, 0f, Budget);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new Vector3(4.5f, 0f, 2.5f), EndpointFeet(space, path));
        Assert.True(region.Contains(EndpointFeet(space, path)));
        Assert.Contains(path.Waypoints, waypoint => waypoint.Position.Y is 0.5f or 4.5f);
    }

    [Fact]
    public void BlockedAnchorDoesNotReplaceTheOnlyFarFaceMember()
    {
        NavSpace space = NavSpace.Single(SurfaceGrid(5, 3, (x, z) => x != 2 || z != 1));
        var region = new NavGoalRegion(new Vector3(2.5f, 0f, 1.5f), 1f,
            feet => feet == new Vector3(3.5f, 0f, 1.5f));

        NavPath path = new GridPathPlanner(space).FindPath(new Vector3(0.5f, 0f, 1.5f), region, 0f, Budget);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new Vector3(3.5f, 0f, 1.5f), EndpointFeet(space, path));
        Assert.True(region.Contains(EndpointFeet(space, path)));
    }

    [Fact]
    public void StartMemberUsesItsCellCentreSurfaceHeight()
    {
        NavSpace space = NavSpace.Single(SurfaceGrid(1, 1, (_, _) => true, 2f));
        var region = new NavGoalRegion(new Vector3(0.5f, 2f, 0.5f), 0f,
            feet => feet == new Vector3(0.5f, 2f, 0.5f));

        NavPath path = new GridPathPlanner(space).FindPath(new Vector3(0.7f, 20f, 0.8f), region, 0f, Budget);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[] { new NavWaypoint(new Vector2(0.5f, 0.5f), 0) }, path.Waypoints);
        Assert.True(region.Contains(EndpointFeet(space, path)));
    }

    [Fact]
    public void NoMemberCellsReturnUnreachable()
    {
        NavSpace space = NavSpace.Single(SurfaceGrid(5, 1, (_, _) => true));
        var region = new NavGoalRegion(new Vector3(4.5f, 0f, 0.5f), 1f, _ => false);

        NavPath path = new GridPathPlanner(space).FindPath(new Vector3(0.5f, 0f, 0.5f), region, 0f, Budget);

        Assert.Equal(NavPathStatus.Unreachable, path.Status);
        Assert.Empty(path.Waypoints);
    }

    [Theory]
    [InlineData(2.5f, NavPathStatus.Complete)]
    [InlineData(2.6f, NavPathStatus.Unreachable)]
    public void ZeroExtentRequiresAnActualCellCentreMember(float anchorX, NavPathStatus expected)
    {
        NavSpace space = NavSpace.Single(SurfaceGrid(3, 1, (_, _) => true));
        var anchor = new Vector3(anchorX, 0f, 0.5f);
        var region = new NavGoalRegion(anchor, 0f, feet => feet == anchor);

        NavPath path = new GridPathPlanner(space).FindPath(new Vector3(0.5f, 0f, 0.5f), region, 0f, Budget);

        Assert.Equal(expected, path.Status);
        if (expected == NavPathStatus.Complete) Assert.True(region.Contains(EndpointFeet(space, path)));
        else Assert.Empty(path.Waypoints);
    }

    [Fact]
    public void VerticallySeparatedMemberRequiresTheDirectedLayerLink()
    {
        NavGrid lower = SurfaceGrid(1, 1, (_, _) => true);
        NavGrid upper = SurfaceGrid(1, 1, (_, _) => true, 4f);
        var space = new NavSpace(new[] { lower, upper }, new[] { new NavLink(0, 0, 0, 1, 0, 0) });
        var region = new NavGoalRegion(new Vector3(0.5f, 4f, 0.5f), 0f,
            feet => feet == new Vector3(0.5f, 4f, 0.5f));

        NavPath path = new GridPathPlanner(space).FindPath(new Vector3(0.5f, 0f, 0.5f), region, 0f, Budget);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[]
        {
            new NavWaypoint(new Vector2(0.5f, 0.5f), 0),
            new NavWaypoint(new Vector2(0.5f, 0.5f), 1),
        }, path.Waypoints);
        Assert.Equal(new Vector3(0.5f, 4f, 0.5f), EndpointFeet(space, path));
        Assert.True(region.Contains(EndpointFeet(space, path)));
        NavPath disconnected = new GridPathPlanner(new NavSpace(new[] { lower, upper })).FindPath(
            new Vector3(0.5f, 0f, 0.5f), region, 0f, Budget);
        Assert.Equal(NavPathStatus.Unreachable, disconnected.Status);
    }

    [Fact]
    public void PositiveBudgetMakesPartialProgressEvenInsideTheHorizontalExtent()
    {
        NavSpace space = NavSpace.Single(SurfaceGrid(5, 1, (_, _) => true));
        var region = new NavGoalRegion(new Vector3(2.5f, 0f, 0.5f), 3f,
            feet => feet.X == 4.5f);
        var budget = new PathQueryBudget { MaxExpandedNodes = 2, SnapRadius = 0f };

        NavPath path = new GridPathPlanner(space).FindPath(new Vector3(0.5f, 0f, 0.5f), region, 0f, budget);

        Assert.Equal(NavPathStatus.Partial, path.Status);
        Assert.Equal(new[] { new NavWaypoint(new Vector2(1.5f, 0.5f), 0) }, path.Waypoints);
        Assert.False(region.Contains(EndpointFeet(space, path)));
    }

    [Fact]
    public void CrossLayerZeroPriorityStillReturnsUsefulBudgetLimitedProgress()
    {
        var space = new NavSpace(new[]
        {
            SurfaceGrid(4, 1, (_, _) => true),
            SurfaceGrid(4, 1, (_, _) => true, 4f),
        }, new[] { new NavLink(0, 2, 0, 1, 2, 0) });
        var region = new NavGoalRegion(new Vector3(3.5f, 4f, 0.5f), 0f,
            feet => feet == new Vector3(3.5f, 4f, 0.5f));

        NavPath path = new GridPathPlanner(space).FindPath(new Vector3(0.5f, 0f, 0.5f), region, 0f,
            new PathQueryBudget { MaxExpandedNodes = 2, SnapRadius = 0f });

        Assert.Equal(NavPathStatus.Partial, path.Status);
        Assert.Equal(new[] { new NavWaypoint(new Vector2(1.5f, 0.5f), 0) }, path.Waypoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheapLinksCannotBeHiddenByASpatialPriority(bool crossLayer)
    {
        NavGrid lower = SurfaceGrid(10, 1, (_, _) => true);
        int farLayer = crossLayer ? 1 : 0;
        NavGrid[] grids = crossLayer ? new[] { lower, SurfaceGrid(10, 1, (_, _) => true, 4f) } : new[] { lower };
        var space = new NavSpace(grids, new[]
        {
            new NavLink(0, 0, 0, farLayer, 9, 0),
            new NavLink(farLayer, 9, 0, 0, 3, 0),
        });
        var region = new NavGoalRegion(new Vector3(3.5f, 0f, 0.5f), 0f,
            feet => feet == new Vector3(3.5f, 0f, 0.5f));

        NavPath path = new GridPathPlanner(space).FindPath(new Vector3(0.5f, 0f, 0.5f), region, 0f, Budget);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[]
        {
            new NavWaypoint(new Vector2(0.5f, 0.5f), 0),
            new NavWaypoint(new Vector2(9.5f, 0.5f), farLayer),
            new NavWaypoint(new Vector2(3.5f, 0.5f), 0),
        }, path.Waypoints);
        Assert.True(region.Contains(EndpointFeet(space, path)));
    }

    [Fact]
    public void PositiveSnapRadiusCannotSubstituteAnOutsideCellForARegionMember()
    {
        NavSpace space = NavSpace.Single(SurfaceGrid(3, 1, (x, _) => x != 2));
        var region = new NavGoalRegion(new Vector3(2.5f, 0f, 0.5f), 0f,
            feet => feet == new Vector3(2.5f, 0f, 0.5f));

        NavPath path = new GridPathPlanner(space).FindPath(new Vector3(0.5f, 0f, 0.5f), region, 0f,
            new PathQueryBudget { MaxExpandedNodes = 100, SnapRadius = 3f });

        Assert.Equal(NavPathStatus.Unreachable, path.Status);
        Assert.Empty(path.Waypoints);
    }

    [Fact]
    public void GuardedRegionKeepsRawClearanceAndEveryAdmittedCell()
    {
        NavSpace space = NavSpace.Single(SurfaceGrid(4, 1, (_, _) => true));
        var graph = new NavTraversalGraph(space, 1.1f, 1.8f,
            new[] { new NavTraversalLayer(4, 1, new[] { true, true, true, true }, new byte[] { 1, 1, 1, 0 }) },
            Array.Empty<NavLink>());
        var region = new NavGoalRegion(new Vector3(3.5f, 0f, 0.5f), 0f,
            feet => feet.X == 3.5f);

        NavPath path = new GridPathPlanner(space, graph).FindPath(new Vector3(0.5f, 0f, 0.5f), region, 1.1f, Budget);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[]
        {
            new NavWaypoint(new Vector2(1.5f, 0.5f), 0),
            new NavWaypoint(new Vector2(2.5f, 0.5f), 0),
            new NavWaypoint(new Vector2(3.5f, 0.5f), 0),
        }, path.Waypoints);
        Assert.True(region.Contains(EndpointFeet(space, path)));
        Assert.Throws<ArgumentException>(() => new GridPathPlanner(space, graph).FindPath(
            new Vector3(0.5f, 0f, 0.5f), region, 0f, Budget));
    }

    [Fact]
    public void GuardedStartCannotSnapAcrossARejectedOwnCell()
    {
        NavSpace space = NavSpace.Single(SurfaceGrid(3, 1, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(3, 1, new[] { false, true, true }, new byte[] { 0, 1, 0 }) },
            Array.Empty<NavLink>());
        var region = new NavGoalRegion(new Vector3(2.5f, 0f, 0.5f), 0f, feet => feet.X == 2.5f);

        NavPath path = new GridPathPlanner(space, graph).FindPath(new Vector3(0.5f, 0f, 0.5f), region, 0f,
            new PathQueryBudget { MaxExpandedNodes = 100, SnapRadius = 3f });

        Assert.Equal(NavPathStatus.Unreachable, path.Status);
        Assert.Empty(path.Waypoints);
    }

    [Fact]
    public void GuardedRegionCannotCrossARefusedEdge()
    {
        NavSpace space = NavSpace.Single(SurfaceGrid(2, 1, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(2, 1, new[] { true, true }, new byte[2]) }, Array.Empty<NavLink>());
        var region = new NavGoalRegion(new Vector3(1.5f, 0f, 0.5f), 0f, feet => feet.X == 1.5f);

        NavPath path = new GridPathPlanner(space, graph).FindPath(new Vector3(0.5f, 0f, 0.5f), region, 0f, Budget);

        Assert.Equal(NavPathStatus.Unreachable, path.Status);
        Assert.Empty(path.Waypoints);
    }

    [Fact]
    public void HeightlessGridsRejectRegionQueries()
    {
        var planner = new GridPathPlanner(NavSpace.Single(NavGrid.FromWalkable(1, 1, 1f, 0f, 0f, (_, _) => true)));
        var region = new NavGoalRegion(Vector3.Zero, 0f, _ => true);

        Assert.Throws<ArgumentException>(() => planner.FindPath(Vector3.Zero, region, 0f, Budget));
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void RegionRejectsAnInvalidExtent(float extent)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new NavGoalRegion(Vector3.Zero, extent, _ => true));

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f)]
    [InlineData(0f, 0f, float.NegativeInfinity)]
    public void RegionRejectsANonfiniteAnchor(float x, float y, float z)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new NavGoalRegion(new Vector3(x, y, z), 0f, _ => true));

    [Fact]
    public void RegionRejectsANullPredicate()
        => Assert.Throws<ArgumentNullException>(() => new NavGoalRegion(Vector3.Zero, 0f, null!));

    static NavGrid SurfaceGrid(int width, int height, Func<int, int, bool> standable, float y = 0f)
        => NavGrid.FromSurfaces(width, height, 1f, 0f, 0f,
            (x, z) => new NavSurfaceSample(standable(x, z), y, 2f), 0.5f, 1f, y, y + 1f);

    static Vector3 EndpointFeet(NavSpace space, NavPath path)
    {
        NavWaypoint endpoint = path.Waypoints[^1];
        NavGrid grid = space.Layers[endpoint.Layer];
        (int x, int z) = grid.CellOf(endpoint.Position.X, endpoint.Position.Y);
        float? surface = grid.SurfaceHeightAt(x, z);
        Assert.NotNull(surface);
        return new Vector3(endpoint.Position.X, surface.Value, endpoint.Position.Y);
    }
}
