using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class GridPathPlannerSmoothingTests
{
    const float CellSize = 0.5f;
    const float AgentRadius = 0.2f;

    // Prepends the snapped start cell center, then asserts every consecutive pair has a clear
    // grid-local line of sight for the agent radius. This is the corner-safety invariant the string
    // pull must preserve: no emitted segment may cut across a blocked cell. Single-layer only (one grid).
    static void AssertConsecutiveClear(NavGrid grid, Vector2 startCenter, IReadOnlyList<NavWaypoint> waypoints)
    {
        var points = new List<Vector2> { startCenter };
        foreach (NavWaypoint w in waypoints)
        {
            points.Add(w.Position);
        }

        var origin = new Vector2(grid.OriginX, grid.OriginZ);
        for (int i = 1; i < points.Count; i++)
        {
            bool clear = GridRay.IsClear(
                points[i - 1] - origin, points[i] - origin, grid.CellSize,
                (x, z) => !grid.IsPassable(x, z, AgentRadius),
                includeEndpointCells: false);
            Assert.True(clear, $"segment {i - 1} -> {i} cuts through a blocked cell");
        }
    }

    [Fact]
    public void FindPath_WallScene_PullCollapsesCollinearRunsToFewWaypoints()
    {
        // The Task 7 wall scene: column x == 15 blocks z 0..25, gap at the top (z 26..29). The raw A*
        // chain climbs dozens of cells up and back down. String-pulling collapses every clear run to
        // its endpoints, so only a handful of turn waypoints survive.
        NavGrid grid = NavGrid.FromWalkable(30, 30, CellSize, 0f, 0f, (x, z) => !(x == 15 && z <= 25));
        var planner = new GridPathPlanner(NavSpace.Single(grid));

        var start = new Vector3(3f, 0f, 3f);
        NavPath path = planner.FindPath(start, new Vector3(12f, 0f, 3f), AgentRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.True(path.Waypoints.Count <= 6, $"expected the pull to collapse to <= 6 waypoints, got {path.Waypoints.Count}");

        (int sx, int sz) = grid.CellOf(start.X, start.Z);
        AssertConsecutiveClear(grid, grid.CellCenter(sx, sz), path.Waypoints);
    }

    [Fact]
    public void FindPath_CornerScene_PullNeverCutsACorner()
    {
        // Two blocked cells (6, 5) and (5, 6) share only the corner on the (5, 5) -> (6, 6) diagonal.
        // A naive straight pull from one side to the other would graze that corner. GridRay threads the
        // edge-adjacent (blocked) cell on an exact corner crossing, so every emitted segment stays clear.
        NavGrid grid = NavGrid.FromWalkable(
            20, 20, CellSize, 0f, 0f,
            (x, z) => !((x == 6 && z == 5) || (x == 5 && z == 6)));
        var planner = new GridPathPlanner(NavSpace.Single(grid));

        Vector2 startCenter = grid.CellCenter(2, 2);
        Vector2 goalCenter = grid.CellCenter(10, 10);
        NavPath path = planner.FindPath(
            new Vector3(startCenter.X, 0f, startCenter.Y),
            new Vector3(goalCenter.X, 0f, goalCenter.Y),
            AgentRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        // Chebyshev span is 8 cells, so the raw grid chain is at least 8 waypoints. The pull must reduce it.
        Assert.True(path.Waypoints.Count < 8, $"expected the pull to shorten the chain below 8 waypoints, got {path.Waypoints.Count}");
        AssertConsecutiveClear(grid, startCenter, path.Waypoints);
    }

    static (NavGrid Layer0, NavGrid Layer1) TwoLayerGrids(Func<int, int, bool> layer1Walkable)
    {
        NavGrid layer0 = NavGrid.FromWalkable(10, 10, 1f, 0f, 0f, (_, _) => true, yMin: 0f, yMax: 4f);
        NavGrid layer1 = NavGrid.FromWalkable(10, 10, 1f, 0f, 0f, layer1Walkable, yMin: 4f, yMax: 8f);
        return (layer0, layer1);
    }

    static NavSpace TwoLayerSpace(NavGrid layer0, NavGrid layer1)
    {
        // A bidirectional link pair between cell (5, 5) on layer 0 and cell (5, 6) on layer 1.
        var links = new[]
        {
            new NavLink(0, 5, 5, 1, 5, 6),
            new NavLink(1, 5, 6, 0, 5, 5),
        };
        return new NavSpace(new[] { layer0, layer1 }, links);
    }

    [Fact]
    public void FindPath_CrossLayer_RoutesThroughLinkWithBothEndpointsConsecutive()
    {
        (NavGrid layer0, NavGrid layer1) = TwoLayerGrids((_, _) => true);
        var planner = new GridPathPlanner(TwoLayerSpace(layer0, layer1));

        // Start on layer 0 (y 1), goal on layer 1 (y 5). The only crossing is the (5, 5) <-> (5, 6) link.
        NavPath path = planner.FindPath(
            new Vector3(1.5f, 1f, 1.5f),
            new Vector3(8.5f, 5f, 8.5f),
            AgentRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);

        Vector2 fromCenter = layer0.CellCenter(5, 5);
        Vector2 toCenter = layer1.CellCenter(5, 6);

        int linkIndex = -1;
        for (int i = 0; i < path.Waypoints.Count - 1; i++)
        {
            NavWaypoint a = path.Waypoints[i];
            NavWaypoint b = path.Waypoints[i + 1];
            if (a.Layer == 0 && a.Position == fromCenter && b.Layer == 1 && b.Position == toCenter)
            {
                linkIndex = i;
                break;
            }
        }

        Assert.True(linkIndex >= 0,
            "expected the two link endpoint centers to appear consecutively with layers 0 then 1");

        // The final waypoint reaches the goal on layer 1.
        Assert.Equal(1, path.Waypoints[^1].Layer);
    }

    [Fact]
    public void FindPath_CrossLayer_BlockedLinkExit_IsNotTraversed()
    {
        // Same scene, but the link's exit cell (5, 6) on layer 1 is blocked, so the edge is skipped and
        // layer 1 is unreachable from layer 0.
        (NavGrid layer0, NavGrid layer1) = TwoLayerGrids((x, z) => !(x == 5 && z == 6));
        var planner = new GridPathPlanner(TwoLayerSpace(layer0, layer1));

        NavPath path = planner.FindPath(
            new Vector3(1.5f, 1f, 1.5f),
            new Vector3(8.5f, 5f, 8.5f),
            AgentRadius, PathQueryBudget.Default);

        Assert.True(
            path.Status is NavPathStatus.Partial or NavPathStatus.Unreachable,
            $"expected Partial or Unreachable across a blocked link, got {path.Status}");
        Assert.DoesNotContain(path.Waypoints, w => w.Layer == 1);
    }

    [Fact]
    public void FindPath_CrossLayer_SameQueryTwice_IsDeterministic()
    {
        (NavGrid layer0, NavGrid layer1) = TwoLayerGrids((_, _) => true);
        var planner = new GridPathPlanner(TwoLayerSpace(layer0, layer1));

        var start = new Vector3(1.5f, 1f, 1.5f);
        var goal = new Vector3(8.5f, 5f, 8.5f);
        NavPath first = planner.FindPath(start, goal, AgentRadius, PathQueryBudget.Default);
        NavPath second = planner.FindPath(start, goal, AgentRadius, PathQueryBudget.Default);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Waypoints.Count, second.Waypoints.Count);
        for (int i = 0; i < first.Waypoints.Count; i++)
        {
            Assert.Equal(first.Waypoints[i].Position, second.Waypoints[i].Position);
            Assert.Equal(first.Waypoints[i].Layer, second.Waypoints[i].Layer);
        }
    }
}
