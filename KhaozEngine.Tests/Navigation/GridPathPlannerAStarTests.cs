using System;
using System.Collections.Generic;
using System.Numerics;
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
    public void FindPath_NearOpenGrid_PathCostEqualsOctileDistance()
    {
        // A single blocked cell on the straight line forces the A* search to run (otherwise the
        // line-of-sight fast path returns a trivial one-waypoint straight line with no cell-center
        // chain to sum). The cell is placed so an octile-optimal, corner-cut-legal detour still
        // exists, so the shortest route costs exactly the octile distance between the two cells.
        NavGrid grid = NavGrid.FromWalkable(20, 20, CellSize, 0f, 0f, (x, z) => !(x == 6 && z == 4));
        var planner = new GridPathPlanner(NavSpace.Single(grid));

        Vector2 startCenter = grid.CellCenter(2, 2);
        Vector2 goalCenter = grid.CellCenter(10, 6);
        NavPath path = planner.FindPath(
            new Vector3(startCenter.X, 0f, startCenter.Y),
            new Vector3(goalCenter.X, 0f, goalCenter.Y),
            AgentRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.True(path.Waypoints.Count > 1, "expected a multi-cell chain, not the fast-path single waypoint");

        float cost = ChainLength(startCenter, path.Waypoints);
        Assert.Equal(Octile(10 - 2, 6 - 2), cost, 3);
    }

    [Fact]
    public void FindPath_CornerObstacle_NeverCutsCorners()
    {
        // Two blocked cells (6, 5) and (5, 6) share only the corner at the (5, 5) -> (6, 6) diagonal,
        // straddling the straight line between start and goal. The path must not squeeze diagonally
        // between them: every diagonal step it takes must have both orthogonal companions passable.
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

        // Walk the cell chain (start prepended) and check every diagonal step's companions.
        var cells = new List<(int X, int Z)> { grid.CellOf(startCenter.X, startCenter.Y) };
        foreach (NavWaypoint w in path.Waypoints)
        {
            cells.Add(grid.CellOf(w.Position.X, w.Position.Y));
        }

        int diagonalSteps = 0;
        for (int i = 1; i < cells.Count; i++)
        {
            int dx = cells[i].X - cells[i - 1].X;
            int dz = cells[i].Z - cells[i - 1].Z;
            if (Math.Abs(dx) == 1 && Math.Abs(dz) == 1)
            {
                diagonalSteps++;
                Assert.True(grid.IsPassable(cells[i - 1].X + dx, cells[i - 1].Z, AgentRadius),
                    $"diagonal step {i} cut the corner at companion ({cells[i - 1].X + dx}, {cells[i - 1].Z})");
                Assert.True(grid.IsPassable(cells[i - 1].X, cells[i - 1].Z + dz, AgentRadius),
                    $"diagonal step {i} cut the corner at companion ({cells[i - 1].X}, {cells[i - 1].Z + dz})");
            }
        }

        Assert.True(diagonalSteps > 0, "expected the route to contain diagonal steps");
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
