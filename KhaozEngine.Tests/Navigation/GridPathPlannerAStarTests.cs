using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class GridPathPlannerAStarTests
{
    const float CellSize = 0.5f;
    const float AgentRadius = 0.2f;
    static readonly float Sqrt2 = MathF.Sqrt(2f);

    // Summed length of the returned cell-center chain, prepending the start point the search began at
    // (waypoints exclude the start cell, matching the line-of-sight fast path).
    static float ChainLength(Vector2 start, IReadOnlyList<NavWaypoint> waypoints)
    {
        float sum = 0f;
        Vector2 prev = start;
        foreach (NavWaypoint w in waypoints)
        {
            sum += Vector2.Distance(prev, w.Position);
            prev = w.Position;
        }
        return sum;
    }

    static float Octile(int dxCells, int dzCells)
    {
        int min = Math.Min(dxCells, dzCells);
        int max = Math.Max(dxCells, dzCells);
        return CellSize * (Sqrt2 * min + (max - min));
    }

    [Fact]
    public void FindPath_WallWithTopGap_RoutesAroundThroughTheGap()
    {
        // Wall column x == 15 blocks z 0..25, leaving a gap at the top (z 26..29). Start sits left of
        // the wall, goal to its right, so the only route runs up through the gap and back down.
        NavGrid grid = NavGrid.FromWalkable(
            30, 30, CellSize, 0f, 0f,
            (x, z) => !(x == 15 && z <= 25));
        var planner = new GridPathPlanner(NavSpace.Single(grid));

        NavPath path = planner.FindPath(new Vector3(3f, 0f, 3f), new Vector3(12f, 0f, 3f), AgentRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.NotEmpty(path.Waypoints);
        foreach (NavWaypoint w in path.Waypoints)
        {
            (int cx, int cz) = grid.CellOf(w.Position.X, w.Position.Y);
            Assert.True(grid.IsPassable(cx, cz, AgentRadius), $"waypoint cell ({cx}, {cz}) is not passable");
        }
        // Some waypoint must climb into the gap region (world z > 12.5) to clear the wall.
        Assert.Contains(path.Waypoints, w => w.Position.Y > 12.5f);
    }

    [Fact]
    public void FindPath_NearOpenGrid_PulledCostBetweenStraightLineAndOctile()
    {
        // A single blocked cell (6, 4) sits exactly on the straight start -> goal line, so the A* search
        // must run and the string pull cannot collapse the route to one straight segment: it bends
        // around the block. Before string-pulling, the returned chain summed to the octile grid distance
        // (raw cell centers). The pull now emits Euclidean shortcuts, so the length lands strictly
        // between two derivable bounds: it can never beat the straight-line distance, and never exceed
        // the octile grid cost of the optimal underlying A* path. Re-derived from the old exact-octile
        // assertion because smoothing legitimately changes the waypoint chain (and its summed length).
        NavGrid grid = NavGrid.FromWalkable(20, 20, CellSize, 0f, 0f, (x, z) => !(x == 6 && z == 4));
        var planner = new GridPathPlanner(NavSpace.Single(grid));

        Vector2 startCenter = grid.CellCenter(2, 2);
        Vector2 goalCenter = grid.CellCenter(10, 6);
        NavPath path = planner.FindPath(
            new Vector3(startCenter.X, 0f, startCenter.Y),
            new Vector3(goalCenter.X, 0f, goalCenter.Y),
            AgentRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.True(path.Waypoints.Count > 1, "expected the pull to bend around the block, not a single straight segment");

        float cost = ChainLength(startCenter, path.Waypoints);
        float straight = Vector2.Distance(startCenter, goalCenter);
        float octile = Octile(10 - 2, 6 - 2);
        Assert.True(cost >= straight - 1e-4f, $"pulled cost {cost} beat the straight-line lower bound {straight}");
        Assert.True(cost <= octile + 1e-4f, $"pulled cost {cost} exceeded the octile grid cost {octile}");
        Assert.True(cost < octile, $"expected the pull to shorten the octile grid path, cost {cost} vs octile {octile}");
    }

    [Fact]
    public void FindPath_CornerObstacle_NeverCutsCorners()
    {
        // Two blocked cells (6, 5) and (5, 6) share only the corner at the (5, 5) -> (6, 6) diagonal,
        // straddling the straight line between start and goal. Before string-pulling this checked each
        // raw diagonal step's orthogonal companions. The pull now emits sparse waypoints that are no
        // longer adjacent cells, so the pinned corner-safety guarantee becomes the line-of-sight
        // invariant: every emitted segment (start prepended) must clear both blocked cells. GridRay
        // threads the edge-adjacent cell on an exact corner crossing, so a segment grazing the
        // (5, 5) -> (6, 6) diagonal between the two blocks reads as blocked and is never emitted.
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

        var points = new List<Vector2> { startCenter };
        foreach (NavWaypoint w in path.Waypoints)
        {
            // No waypoint may land on either blocked cell.
            (int cx, int cz) = grid.CellOf(w.Position.X, w.Position.Y);
            Assert.True(grid.IsPassable(cx, cz, AgentRadius), $"waypoint cell ({cx}, {cz}) is not passable");
            points.Add(w.Position);
        }

        // The grid is baked at origin (0, 0), so world coordinates are already grid-local for GridRay.
        for (int i = 1; i < points.Count; i++)
        {
            bool clear = GridRay.IsClear(
                points[i - 1], points[i], CellSize,
                (x, z) => !grid.IsPassable(x, z, AgentRadius),
                includeEndpointCells: false);
            Assert.True(clear, $"segment {i - 1} -> {i} cut the corner between the two blocked cells");
        }
    }

    static NavGrid SealedBoxGrid()
        // A box with 2-cell-thick walls (x/z 11..12 and 18..19) around an open 5x5 interior (13..17),
        // fully enclosing the goal so no route reaches it. Interior wider than a 3-unit snap radius so
        // an outside endpoint never snaps through the wall.
        => NavGrid.FromWalkable(
            30, 30, CellSize, 0f, 0f,
            (x, z) => !((x >= 11 && x <= 19 && z >= 11 && z <= 19) && !(x >= 13 && x <= 17 && z >= 13 && z <= 17)));

    [Fact]
    public void FindPath_GoalSealedInBox_ReturnsPartialToward()
    {
        var planner = new GridPathPlanner(NavSpace.Single(SealedBoxGrid()));

        var start = new Vector3(2f, 0f, 2f);
        var goal = new Vector3(7.75f, 0f, 7.75f); // center of interior cell (15, 15)
        NavPath path = planner.FindPath(start, goal, AgentRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Partial, path.Status);
        Assert.NotEmpty(path.Waypoints);

        var startXz = new Vector2(start.X, start.Z);
        var goalXz = new Vector2(goal.X, goal.Z);
        float lastToGoal = Vector2.Distance(path.Waypoints[^1].Position, goalXz);
        float startToGoal = Vector2.Distance(startXz, goalXz);
        Assert.True(lastToGoal < startToGoal, "partial path did not make progress toward the goal");
    }

    [Fact]
    public void FindPath_TinyBudget_ReturnsWithoutHang()
    {
        var planner = new GridPathPlanner(NavSpace.Single(SealedBoxGrid()));

        var budget = new PathQueryBudget { MaxExpandedNodes = 8, SnapRadius = 3f };
        NavPath path = planner.FindPath(new Vector3(2f, 0f, 2f), new Vector3(7.75f, 0f, 7.75f), AgentRadius, budget);

        // The goal is unreachable, so a tiny node budget must still terminate promptly with a
        // truncated result rather than searching the whole outside.
        Assert.True(
            path.Status is NavPathStatus.Partial or NavPathStatus.Unreachable,
            $"expected Partial or Unreachable under a tiny budget, got {path.Status}");
    }

    [Fact]
    public void FindPath_SameQueryTwice_IsDeterministic()
    {
        NavGrid grid = NavGrid.FromWalkable(
            30, 30, CellSize, 0f, 0f,
            (x, z) => !(x == 15 && z <= 25));
        var planner = new GridPathPlanner(NavSpace.Single(grid));

        NavPath first = planner.FindPath(new Vector3(3f, 0f, 3f), new Vector3(12f, 0f, 3f), AgentRadius, PathQueryBudget.Default);
        NavPath second = planner.FindPath(new Vector3(3f, 0f, 3f), new Vector3(12f, 0f, 3f), AgentRadius, PathQueryBudget.Default);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Waypoints.Count, second.Waypoints.Count);
        for (int i = 0; i < first.Waypoints.Count; i++)
        {
            Assert.Equal(first.Waypoints[i].Position, second.Waypoints[i].Position);
            Assert.Equal(first.Waypoints[i].Layer, second.Waypoints[i].Layer);
        }
    }
}
