using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class GridPathPlannerBasicsTests
{
    const float CellSize = 0.5f;
    const float AgentRadius = 0.2f;

    static NavGrid OpenGrid()
        => NavGrid.FromWalkable(20, 20, CellSize, 0f, 0f, (_, _) => true);

    [Fact]
    public void FindPath_OpenGridClearLine_ReturnsCompleteWithGoalWaypoint()
    {
        var planner = new GridPathPlanner(NavSpace.Single(OpenGrid()));

        NavPath path = planner.FindPath(new Vector3(1f, 0f, 1f), new Vector3(8f, 0f, 8f), AgentRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        NavWaypoint waypoint = Assert.Single(path.Waypoints);
        Assert.Equal(new Vector2(8f, 8f), waypoint.Position);
        Assert.Equal(0, waypoint.Layer);
    }

    [Fact]
    public void FindPath_GoalInBlockedPocket_SnapsToNearbyPassableCenter()
    {
        // Blocks only the single cell the goal query point falls in. Every other cell stays open.
        NavGrid grid = NavGrid.FromWalkable(20, 20, CellSize, 0f, 0f, (x, z) => !(x == 16 && z == 16));
        var planner = new GridPathPlanner(NavSpace.Single(grid));

        NavPath path = planner.FindPath(new Vector3(1f, 0f, 1f), new Vector3(8f, 0f, 8f), AgentRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        NavWaypoint waypoint = Assert.Single(path.Waypoints);
        // Not the exact goal (8, 8): its own cell (16, 16) is blocked, so the waypoint is the nearest
        // ring-1 passable cell center. (15, 15), (16, 15) and (15, 16) tie on squared distance, and the
        // documented scan order (z low to high, then x low to high) picks (15, 15) first.
        Assert.Equal(new Vector2(7.75f, 7.75f), waypoint.Position);
    }

    [Fact]
    public void FindPath_GoalDeepInBigBlockedRegion_ReturnsUnreachable()
    {
        // Only a 2x2 patch at the low corner is passable. The goal's cell is far outside SnapRadius of it.
        NavGrid grid = NavGrid.FromWalkable(20, 20, CellSize, 0f, 0f, (x, z) => x <= 1 && z <= 1);
        var planner = new GridPathPlanner(NavSpace.Single(grid));

        NavPath path = planner.FindPath(new Vector3(0.25f, 0f, 0.25f), new Vector3(8f, 0f, 8f), AgentRadius, PathQueryBudget.Default);

        Assert.Same(NavPath.Unreachable, path);
    }

    [Fact]
    public void FindPath_StartOffGrid_SnapsIn()
    {
        var planner = new GridPathPlanner(NavSpace.Single(OpenGrid()));

        NavPath path = planner.FindPath(new Vector3(-1f, 0f, 5f), new Vector3(5f, 0f, 5f), AgentRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        NavWaypoint waypoint = Assert.Single(path.Waypoints);
        Assert.Equal(new Vector2(5f, 5f), waypoint.Position);
    }

    [Fact]
    public void FindPath_LineOfSightBlocked_ByFullHeightWall_ReturnsPartialToward()
    {
        // A solid wall column spans every row, fully dividing the grid, so no route reaches the goal on
        // the far side. Before A* landed (Task 7) the non-line-of-sight branch fell back to Unreachable.
        // A* now routes as far as it can and returns a Partial path that stops against the near face of
        // the wall, closer to the goal than the start was.
        NavGrid grid = NavGrid.FromWalkable(20, 20, CellSize, 0f, 0f, (x, _) => x != 5);
        var planner = new GridPathPlanner(NavSpace.Single(grid));

        var start = new Vector3(1f, 0f, 5f);
        var goal = new Vector3(8f, 0f, 5f);
        NavPath path = planner.FindPath(start, goal, AgentRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Partial, path.Status);
        Assert.NotEmpty(path.Waypoints);
        var startXz = new Vector2(start.X, start.Z);
        var goalXz = new Vector2(goal.X, goal.Z);
        Assert.True(
            Vector2.Distance(path.Waypoints[^1].Position, goalXz) < Vector2.Distance(startXz, goalXz),
            "partial path did not make progress toward the goal");
    }

    [Fact]
    public void Default_MatchesDocumentedBudget()
    {
        Assert.Equal(4096, PathQueryBudget.Default.MaxExpandedNodes);
        Assert.Equal(3f, PathQueryBudget.Default.SnapRadius);
    }

    [Fact]
    public void Unreachable_IsCachedAndEmpty()
    {
        Assert.Same(NavPath.Unreachable, NavPath.Unreachable);
        Assert.Equal(NavPathStatus.Unreachable, NavPath.Unreachable.Status);
        Assert.Empty(NavPath.Unreachable.Waypoints);
    }

    [Fact]
    public void FindPath_FastPath_NonZeroOriginGrid_TranslatesToGridLocalForLineOfSight()
    {
        // Grid baked at world origin (100, -50), covering (100,-50)..(110,-40).
        // Without the grid-local translation in HasLineOfSight, GridRay would walk
        // cells at floor(world / cellSize) = floor((start / 0.5), (goal / 0.5)),
        // which lands far outside the 20x20 grid and reads as blocked.
        NavGrid grid = NavGrid.FromWalkable(20, 20, CellSize, 100f, -50f, (_, _) => true);
        var planner = new GridPathPlanner(NavSpace.Single(grid));

        NavPath path = planner.FindPath(
            new Vector3(101f, 0f, -48f),
            new Vector3(108f, 0f, -48f),
            AgentRadius,
            PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        NavWaypoint waypoint = Assert.Single(path.Waypoints);
        Assert.Equal(new Vector2(108f, -48f), waypoint.Position);
        Assert.Equal(0, waypoint.Layer);
    }
}
