using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

/// <summary>
/// The follower's VERTICAL behaviour, split out of <c>PathFollowerTests</c>: the layer-aware waypoint
/// advance (#316) and the vertical half of the goal-retarget trigger (#317). Both are about a world where
/// two points can share an XZ position and sit a floor apart, which every other follower test deliberately
/// ignores by working on one flat layer.
/// </summary>
public class PathFollowerVerticalTests
{
    const float AgentRadius = 0.3f;

    /// <summary>Replays queued <see cref="NavPath"/> results in order and records every call, so a replan
    /// trigger can be driven frame by frame without a real grid.</summary>
    sealed class FakePlanner : IPathPlanner
    {
        readonly Queue<NavPath> _results = new();

        public int CallCount { get; private set; }
        public List<(Vector3 Start, Vector3 Goal, float AgentRadius, PathQueryBudget Budget)> Calls { get; } = new();

        public void Enqueue(NavPath path) => _results.Enqueue(path);

        public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget)
        {
            CallCount++;
            Calls.Add((start, goal, agentRadius, budget));
            return _results.Count > 0 ? _results.Dequeue() : NavPath.Unreachable;
        }
    }

    static NavPath Complete(params NavWaypoint[] waypoints) => new NavPath(NavPathStatus.Complete, waypoints);

    // --- Layer-aware waypoint advance (#316). Two grids stacked on non-overlapping Y bands, neither carrying
    // surface heights, so NavSpace.LayerAt falls back to the Y-band lookup: Y 0.5 is layer 0, Y 4 is layer 1.
    static NavSpace TwoFloors() => new(new[]
    {
        NavGrid.FromWalkable(32, 32, 1f, 0f, 0f, (_, _) => true, yMin: 0f, yMax: 2f),
        NavGrid.FromWalkable(32, 32, 1f, 0f, 0f, (_, _) => true, yMin: 2.5f, yMax: 6f),
    });

    // A stair link: its two ends are half a unit apart in XZ, well inside the 0.6 default accept radius, but a
    // whole floor apart vertically.
    static NavPath StairPath() => Complete(
        new NavWaypoint(new Vector2(10f, 0f), 0),
        new NavWaypoint(new Vector2(10.5f, 0f), 1),
        new NavWaypoint(new Vector2(20f, 0f), 1));

    [Fact]
    public void Tick_AtTheFootOfAStair_DoesNotAdvancePastTheWaypointOnTheFloorAbove()
    {
        var planner = new FakePlanner();
        planner.Enqueue(StairPath());
        var follower = new PathFollower(planner, config: null, space: TwoFloors());
        Vector3 goal = new Vector3(20f, 4f, 0f);

        // Standing on the lower waypoint, on layer 0. The upper waypoint is 0.5 away in XZ, inside the accept
        // radius, so an XZ-only advance would consume it too and steer straight at (20, 0), skipping the climb.
        PathFollowOutput output = follower.Tick(new Vector3(10f, 0.5f, 0f), goal, AgentRadius, 0.016f);

        Assert.Equal(PathFollowState.Following, output.State);
        Assert.Equal(new Vector2(10.5f, 0f), output.ActiveWaypoint);
        Assert.Equal(1, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void Tick_OnceTheAgentIsOnTheUpperLayer_AdvancesPastTheStairWaypoint()
    {
        var planner = new FakePlanner();
        planner.Enqueue(StairPath());
        var follower = new PathFollower(planner, config: null, space: TwoFloors());
        Vector3 goal = new Vector3(20f, 4f, 0f);

        follower.Tick(new Vector3(10f, 0.5f, 0f), goal, AgentRadius, 0.016f);
        // Same XZ, one floor up: the agent has climbed, so the upper waypoint is genuinely reached now.
        PathFollowOutput output = follower.Tick(new Vector3(10.5f, 4f, 0f), goal, AgentRadius, 0.016f);

        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Following, output.State);
        Assert.Equal(new Vector2(20f, 0f), output.ActiveWaypoint);
        Assert.Equal(2, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void Tick_WithNoNavSpace_KeepsTheXzOnlyAdvance()
    {
        var planner = new FakePlanner();
        planner.Enqueue(StairPath());
        var follower = new PathFollower(planner);
        Vector3 goal = new Vector3(20f, 4f, 0f);

        // No space to resolve the agent's layer with, so the advance stays exactly what it always was. This is
        // the single-layer contract, kept deliberately: a consumer that never built a NavSpace is unaffected.
        PathFollowOutput output = follower.Tick(new Vector3(10f, 0.5f, 0f), goal, AgentRadius, 0.016f);

        Assert.Equal(new Vector2(20f, 0f), output.ActiveWaypoint);
        Assert.Equal(2, follower.ActiveWaypointIndex);
    }

    // --- Vertical goal retarget (#317). ---

    [Fact]
    public void Tick_GoalRisesAWholeFloorWithoutMovingInXz_Replans()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(20f, 20f), 0)));
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(20f, 20f), 1)));
        var follower = new PathFollower(planner);
        Vector3 position = new Vector3(0f, 0f, 0f);

        follower.Tick(position, new Vector3(20f, 0f, 20f), AgentRadius, 0.016f);
        // Same XZ, one floor up. The horizontal drift is exactly zero, so before the vertical term existed
        // no replan was ever due and the follower kept steering the route it planned to the old floor. The
        // dt drains the cooldown so the due replan can actually reach the planner.
        PathFollowOutput output = follower.Tick(position, new Vector3(20f, 4f, 20f), AgentRadius, 0.5f);

        Assert.Equal(2, planner.CallCount);
        Assert.Equal(4f, planner.Calls[1].Goal.Y);
        Assert.Equal(PathFollowState.Following, output.State);
    }

    [Fact]
    public void Tick_GoalDriftsVerticallyWithinTolerance_DoesNotReplan()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(20f, 20f), 0)));
        var follower = new PathFollower(planner);
        Vector3 position = new Vector3(0f, 0f, 0f);

        follower.Tick(position, new Vector3(20f, 0f, 20f), AgentRadius, 0.016f);
        // 0.5 up, inside the 0.8 default: ordinary ground variation under a goal walking level ground.
        follower.Tick(position, new Vector3(20f, 0.5f, 20f), AgentRadius, 0.5f);

        Assert.Equal(1, planner.CallCount);
    }

    [Fact]
    public void Tick_InfiniteVerticalRetargetTolerance_KeepsThePurelyHorizontalTrigger()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(20f, 20f), 0)));
        var config = new PathFollowConfig { GoalRetargetVerticalTolerance = float.PositiveInfinity };
        var follower = new PathFollower(planner, config);
        Vector3 position = new Vector3(0f, 0f, 0f);

        follower.Tick(position, new Vector3(20f, 0f, 20f), AgentRadius, 0.016f);
        follower.Tick(position, new Vector3(20f, 40f, 20f), AgentRadius, 0.5f);

        Assert.Equal(1, planner.CallCount);
    }
}
