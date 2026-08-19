using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class PathFollowerTests
{
    const float AgentRadius = 0.3f;

    /// <summary>Scripted <see cref="IPathPlanner"/> that records every call and replays queued
    /// <see cref="NavPath"/> results in order, so each replan trigger can be driven frame by frame
    /// without a real grid. A call past the queue returns <see cref="NavPath.Unreachable"/>.</summary>
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
    static NavPath Partial(params NavWaypoint[] waypoints) => new NavPath(NavPathStatus.Partial, waypoints);

    static NavWaypoint Hop(float x, float y) => new(new Vector2(x, y), 0) { Kind = NavWaypointKind.Hop };

    [Fact]
    public void Tick_FirstCall_PlansOnceAndFollowsWaypointZero()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), new NavWaypoint(new Vector2(10f, 10f), 0)));
        var follower = new PathFollower(planner);

        PathFollowOutput output = follower.Tick(new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 10f), AgentRadius, 0.016f);

        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Following, output.State);
        Assert.Equal(new Vector2(5f, 5f), output.ActiveWaypoint);
        Assert.Equal(Vector2.Normalize(new Vector2(5f, 5f)), output.WorldDir);
        // The follower must forward the exact agentRadius it was ticked with and the config's Budget.
        Assert.Equal(AgentRadius, planner.Calls[0].AgentRadius);
        Assert.Equal(PathFollowConfig.Default.Budget, planner.Calls[0].Budget);
    }

    [Fact]
    public void Tick_WithinAcceptRadiusOfWaypointZero_AdvancesToWaypointOneWithoutReplanning()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), new NavWaypoint(new Vector2(20f, 20f), 0)));
        var follower = new PathFollower(planner);
        Vector3 goal = new Vector3(20f, 0f, 20f);

        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.016f);
        PathFollowOutput output = follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.016f);

        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Following, output.State);
        Assert.Equal(new Vector2(20f, 20f), output.ActiveWaypoint);
    }

    [Fact]
    public void Tick_ConsumingFinalWaypointOfCompletePath_ReturnsArrived()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), new NavWaypoint(new Vector2(19f, 19f), 0)));
        var follower = new PathFollower(planner);
        // Goal sits beyond AcceptRadius of the last waypoint so this exercises the step-6 "consumed
        // waypoint" arrival, not the step-2 raw-goal-distance arrival.
        Vector3 goal = new Vector3(20f, 0f, 20f);

        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.016f);
        follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.016f);
        PathFollowOutput output = follower.Tick(new Vector3(19f, 0f, 19f), goal, AgentRadius, 0.016f);

        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Arrived, output.State);
        Assert.Equal(Vector2.Zero, output.WorldDir);
        Assert.Equal(Vector2.Zero, output.ActiveWaypoint);
    }

    [Fact]
    public void Tick_GoalMovesBeyondRetargetTolerance_RepansOnlyAfterCooldownElapses()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0)));
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(30f, 30f), 0)));
        var config = new PathFollowConfig { GoalRetargetTolerance = 1.5f, ReplanCooldownSeconds = 0.5f };
        var follower = new PathFollower(planner, config);
        Vector3 position = new Vector3(0f, 0f, 0f);

        follower.Tick(position, new Vector3(10f, 0f, 10f), AgentRadius, 0.1f);
        Assert.Equal(1, planner.CallCount);

        // Goal drifts well past GoalRetargetTolerance, but cooldown has not elapsed yet.
        PathFollowOutput stillOld = follower.Tick(position, new Vector3(30f, 0f, 30f), AgentRadius, 0.1f);
        Assert.Equal(1, planner.CallCount);
        Assert.Equal(new Vector2(5f, 5f), stillOld.ActiveWaypoint);

        // Remaining cooldown drains. The drifted goal still needs a plan, so this tick replans.
        PathFollowOutput replanned = follower.Tick(position, new Vector3(30f, 0f, 30f), AgentRadius, 0.4f);
        Assert.Equal(2, planner.CallCount);
        Assert.Equal(new Vector2(30f, 30f), replanned.ActiveWaypoint);
    }

    [Fact]
    public void Tick_PositionTeleportsBeyondCorridorTolerance_ReplansOnlyAfterCooldownElapses()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(10f, 0f), 0)));
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(10f, 0f), 0)));
        var config = new PathFollowConfig { CorridorTolerance = 2.5f, ReplanCooldownSeconds = 0.5f };
        var follower = new PathFollower(planner, config);
        Vector3 goal = new Vector3(30f, 0f, 0f);

        // Plan origin (0,0) -> waypoint (10,0): the corridor is the segment along the x axis.
        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.1f);
        Assert.Equal(1, planner.CallCount);

        // Teleport far off that corridor (perpendicular distance 10 > CorridorTolerance), but cooldown
        // has not elapsed yet.
        PathFollowOutput stillOld = follower.Tick(new Vector3(5f, 0f, 10f), goal, AgentRadius, 0.1f);
        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Following, stillOld.State);

        // Cooldown drains. The corridor breach still stands, so this tick replans.
        PathFollowOutput replanned = follower.Tick(new Vector3(5f, 0f, 10f), goal, AgentRadius, 0.4f);
        Assert.Equal(2, planner.CallCount);
    }

    [Fact]
    public void Tick_PlannerReturnsUnreachable_YieldsUnreachableAndRetriesOnlyAfterCooldown()
    {
        var planner = new FakePlanner();
        planner.Enqueue(NavPath.Unreachable);
        planner.Enqueue(NavPath.Unreachable);
        var config = new PathFollowConfig { ReplanCooldownSeconds = 0.5f };
        var follower = new PathFollower(planner, config);
        Vector3 position = new Vector3(0f, 0f, 0f);
        Vector3 goal = new Vector3(10f, 0f, 10f);

        PathFollowOutput first = follower.Tick(position, goal, AgentRadius, 0.1f);
        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Unreachable, first.State);
        Assert.Equal(Vector2.Zero, first.WorldDir);
        Assert.Equal(Vector2.Zero, first.ActiveWaypoint);

        // Still within cooldown: no retry.
        PathFollowOutput second = follower.Tick(position, goal, AgentRadius, 0.1f);
        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Unreachable, second.State);

        // Cooldown drains: retries.
        PathFollowOutput third = follower.Tick(position, goal, AgentRadius, 0.4f);
        Assert.Equal(2, planner.CallCount);
        Assert.Equal(PathFollowState.Unreachable, third.State);
    }

    [Fact]
    public void Tick_PartialPathConsumed_FollowsRawGoalForOneTickThenReplans()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Partial(new NavWaypoint(new Vector2(5f, 5f), 0)));
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(20f, 20f), 0)));
        var config = new PathFollowConfig { ReplanCooldownSeconds = 0.5f };
        var follower = new PathFollower(planner, config);
        Vector3 goal = new Vector3(20f, 0f, 20f);

        // dt drains any cooldown to exactly zero by the time the partial waypoint is consumed.
        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.5f);
        Assert.Equal(1, planner.CallCount);

        PathFollowOutput consumed = follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.5f);
        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Following, consumed.State);
        Assert.Equal(Vector2.Zero, consumed.ActiveWaypoint);
        Assert.Equal(Vector2.Normalize(new Vector2(15f, 15f)), consumed.WorldDir);

        PathFollowOutput replanned = follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.016f);
        Assert.Equal(2, planner.CallCount);
        Assert.Equal(PathFollowState.Following, replanned.State);
        Assert.Equal(new Vector2(20f, 20f), replanned.ActiveWaypoint);
    }

    [Fact]
    public void Tick_GoalWithinAcceptRadius_ReturnsArrivedImmediatelyWithNoPlanCall()
    {
        var planner = new FakePlanner();
        var follower = new PathFollower(planner);

        PathFollowOutput output = follower.Tick(new Vector3(10f, 0f, 10f), new Vector3(10.2f, 0f, 10.1f), AgentRadius, 0.016f);

        Assert.Equal(0, planner.CallCount);
        Assert.Equal(PathFollowState.Arrived, output.State);
        Assert.Equal(Vector2.Zero, output.WorldDir);
        Assert.Equal(Vector2.Zero, output.ActiveWaypoint);
    }

    [Fact]
    public void Tick_GoalOnTheFloorAbove_DoesNotArriveAndReachesThePlanner()
    {
        // The goal shares the agent's XZ (well inside AcceptRadius) but sits a dungeon floor above
        // (DungeonConfig.FloorHeightMeters defaults to 4). An XZ-only arrival check reported Arrived here
        // and returned before the replan decision and the planner call, so the agent never took the stairs.
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(12f, 10f), 0)));
        var follower = new PathFollower(planner);
        Vector3 goal = new Vector3(10.2f, 4f, 10.1f);

        PathFollowOutput output = follower.Tick(new Vector3(10f, 0f, 10f), goal, AgentRadius, 0.016f);

        Assert.NotEqual(PathFollowState.Arrived, output.State);
        Assert.Equal(PathFollowState.Following, output.State);
        Assert.Equal(1, planner.CallCount);
        Assert.Equal(new Vector2(12f, 10f), output.ActiveWaypoint);
        // The planner is handed the full 3D goal, so it can resolve the upper floor's layer itself.
        Assert.Equal(goal, planner.Calls[0].Goal);
    }

    [Fact]
    public void Tick_GoalWithinAcceptRadiusAndVerticalTolerance_StillArrivesWithNoPlanCall()
    {
        // The companion to the floor-above case. A goal one climbable step above the agent (0.4, inside
        // the 0.8 default vertical tolerance) is the same walkable surface, so arrival short-circuits.
        var planner = new FakePlanner();
        var follower = new PathFollower(planner);

        PathFollowOutput output = follower.Tick(
            new Vector3(10f, 0f, 10f), new Vector3(10.2f, 0.4f, 10.1f), AgentRadius, 0.016f);

        Assert.Equal(0, planner.CallCount);
        Assert.Equal(PathFollowState.Arrived, output.State);
        Assert.Equal(Vector2.Zero, output.WorldDir);
    }

    [Fact]
    public void Tick_VerticalToleranceBoundary_ArrivesAtItAndPlansBeyondIt()
    {
        var config = new PathFollowConfig { VerticalAcceptTolerance = 0.5f };

        var atPlanner = new FakePlanner();
        PathFollowOutput at = new PathFollower(atPlanner, config)
            .Tick(new Vector3(10f, 0f, 10f), new Vector3(10f, 0.5f, 10f), AgentRadius, 0.016f);
        Assert.Equal(PathFollowState.Arrived, at.State);
        Assert.Equal(0, atPlanner.CallCount);

        var beyondPlanner = new FakePlanner();
        beyondPlanner.Enqueue(Complete(new NavWaypoint(new Vector2(20f, 20f), 0)));
        PathFollowOutput beyond = new PathFollower(beyondPlanner, config)
            .Tick(new Vector3(10f, 0f, 10f), new Vector3(10f, 0.51f, 10f), AgentRadius, 0.016f);
        Assert.NotEqual(PathFollowState.Arrived, beyond.State);
        Assert.Equal(1, beyondPlanner.CallCount);
    }

    [Fact]
    public void Tick_InfiniteVerticalTolerance_RestoresTheHorizontalOnlyArrivalCheck()
    {
        var planner = new FakePlanner();
        var config = new PathFollowConfig { VerticalAcceptTolerance = float.PositiveInfinity };
        var follower = new PathFollower(planner, config);

        PathFollowOutput output = follower.Tick(
            new Vector3(10f, 0f, 10f), new Vector3(10.2f, 4f, 10.1f), AgentRadius, 0.016f);

        Assert.Equal(0, planner.CallCount);
        Assert.Equal(PathFollowState.Arrived, output.State);
    }

    [Fact]
    public void Reset_ClearsPathAndCooldown_NextTickPlansImmediately()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), new NavWaypoint(new Vector2(10f, 10f), 0)));
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), new NavWaypoint(new Vector2(10f, 10f), 0)));
        var config = new PathFollowConfig { ReplanCooldownSeconds = 0.5f };
        var follower = new PathFollower(planner, config);
        Vector3 position = new Vector3(0f, 0f, 0f);
        Vector3 goal = new Vector3(10f, 0f, 10f);

        // Tick 1: plan once.
        follower.Tick(position, goal, AgentRadius, 0.1f);
        Assert.Equal(1, planner.CallCount);

        // Tick 2: cooldown is still hot (0.4 remaining), same goal, no replan.
        PathFollowOutput beforeReset = follower.Tick(position, goal, AgentRadius, 0.1f);
        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Following, beforeReset.State);

        // Act: clear path and cooldown.
        follower.Reset();

        // Assert: next tick plans immediately (cooldown is 0), CallCount becomes 2.
        PathFollowOutput afterReset = follower.Tick(position, goal, AgentRadius, 0f);
        Assert.Equal(2, planner.CallCount);
        Assert.Equal(PathFollowState.Following, afterReset.State);
        Assert.Equal(new Vector2(5f, 5f), afterReset.ActiveWaypoint);
    }

    [Fact]
    public void ActivePath_BeforeAnyTick_IsNullWithIndexZero()
    {
        var follower = new PathFollower(new FakePlanner());

        Assert.Null(follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void ActivePath_AfterTickThatPlans_ExposesTheCommittedCorridorReadOnly()
    {
        var planner = new FakePlanner();
        NavPath committed = Complete(new NavWaypoint(new Vector2(5f, 5f), 0), new NavWaypoint(new Vector2(10f, 10f), 0));
        planner.Enqueue(committed);
        var follower = new PathFollower(planner);

        PathFollowOutput output = follower.Tick(new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 10f), AgentRadius, 0.016f);

        // The accessor hands back the exact NavPath the planner committed - no copy, no re-run of the
        // planner (CallCount stays 1 no matter how often the corridor is read).
        Assert.Same(committed, follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);
        Assert.Equal(1, planner.CallCount);
        _ = follower.ActivePath;
        _ = follower.ActiveWaypointIndex;
        Assert.Equal(1, planner.CallCount);
        // The waypoint at ActiveWaypointIndex agrees with the tick's ActiveWaypoint output.
        Assert.Equal(output.ActiveWaypoint, follower.ActivePath!.Waypoints[follower.ActiveWaypointIndex].Position);
    }

    [Fact]
    public void ActiveWaypointIndex_AdvancesAsWaypointsAreConsumed()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), new NavWaypoint(new Vector2(20f, 20f), 0)));
        var follower = new PathFollower(planner);
        Vector3 goal = new Vector3(20f, 0f, 20f);

        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.016f);
        Assert.Equal(0, follower.ActiveWaypointIndex);
        Assert.Equal(new Vector2(5f, 5f), follower.ActivePath!.Waypoints[follower.ActiveWaypointIndex].Position);

        // Step within AcceptRadius of waypoint 0: the follower advances to waypoint 1 without replanning.
        follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.016f);
        Assert.Equal(1, planner.CallCount);
        Assert.Equal(1, follower.ActiveWaypointIndex);
        Assert.Equal(new Vector2(20f, 20f), follower.ActivePath!.Waypoints[follower.ActiveWaypointIndex].Position);
    }

    [Fact]
    public void ActivePath_AfterReset_IsNullWithIndexZero()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), new NavWaypoint(new Vector2(20f, 20f), 0)));
        var follower = new PathFollower(planner);

        follower.Tick(new Vector3(0f, 0f, 0f), new Vector3(20f, 0f, 20f), AgentRadius, 0.016f);
        Assert.NotNull(follower.ActivePath);

        follower.Reset();

        Assert.Null(follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void ActivePath_WhileReplanDueButCooldownGating_StaysTheCommittedCorridor()
    {
        var planner = new FakePlanner();
        NavPath first = Complete(new NavWaypoint(new Vector2(5f, 5f), 0));
        planner.Enqueue(first);
        var config = new PathFollowConfig { GoalRetargetTolerance = 1.5f, ReplanCooldownSeconds = 0.5f };
        var follower = new PathFollower(planner, config);
        Vector3 position = new Vector3(0f, 0f, 0f);

        follower.Tick(position, new Vector3(10f, 0f, 10f), AgentRadius, 0.1f);
        Assert.Same(first, follower.ActivePath);

        // Goal drifts well past GoalRetargetTolerance so a replan is due, but the cooldown is still hot.
        // The corridor read stays the committed path, never a re-run of the planner mid-cooldown.
        follower.Tick(position, new Vector3(30f, 0f, 30f), AgentRadius, 0.1f);
        Assert.Equal(1, planner.CallCount);
        Assert.Same(first, follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void ActivePath_ReflectsAReplanWhenGoalMovesBeyondRetargetTolerance()
    {
        var planner = new FakePlanner();
        NavPath first = Complete(new NavWaypoint(new Vector2(5f, 5f), 0), new NavWaypoint(new Vector2(10f, 10f), 0));
        NavPath second = Complete(new NavWaypoint(new Vector2(28f, 28f), 0), new NavWaypoint(new Vector2(30f, 30f), 0));
        planner.Enqueue(first);
        planner.Enqueue(second);
        var config = new PathFollowConfig { GoalRetargetTolerance = 1.5f, ReplanCooldownSeconds = 0.5f };
        var follower = new PathFollower(planner, config);
        Vector3 goal = new Vector3(10f, 0f, 10f);

        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.1f);
        Assert.Same(first, follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);

        // Reach waypoint 0 so progress sits at a nonzero index before the replan. This is what proves
        // the replan RESETS the index, rather than it never having left zero.
        follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.1f);
        Assert.Equal(1, planner.CallCount);
        Assert.Same(first, follower.ActivePath);
        Assert.Equal(1, follower.ActiveWaypointIndex);

        // Remaining cooldown (0.4) drains fully and the drifted goal forces a replan: the corridor
        // accessor swaps to the fresh committed path and the index returns to zero.
        follower.Tick(new Vector3(5f, 0f, 5f), goal: new Vector3(30f, 0f, 30f), AgentRadius, 0.4f);
        Assert.Equal(2, planner.CallCount);
        Assert.Same(second, follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void ActivePath_DuringPartialExhaustionGapTick_IsNull()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Partial(new NavWaypoint(new Vector2(5f, 5f), 0)));
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(20f, 20f), 0)));
        var config = new PathFollowConfig { ReplanCooldownSeconds = 0.5f };
        var follower = new PathFollower(planner, config);
        Vector3 goal = new Vector3(20f, 0f, 20f);

        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.5f);
        Assert.NotNull(follower.ActivePath);

        // Consuming the partial path's last waypoint clears it: for this one gap tick the follower
        // steers straight at the raw goal while State stays Following, holding no corridor at all.
        PathFollowOutput gap = follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.5f);
        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Following, gap.State);
        Assert.Equal(Vector2.Zero, gap.ActiveWaypoint);
        Assert.Null(follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);

        // The next tick replans and the corridor accessor comes back.
        follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.016f);
        Assert.Equal(2, planner.CallCount);
        Assert.NotNull(follower.ActivePath);
    }

    [Fact]
    public void ActivePath_Waypoints_CannotBeDowncastToTheBackingList()
    {
        var planner = new FakePlanner();
        var backing = new List<NavWaypoint> { new(new Vector2(5f, 5f), 0), new(new Vector2(10f, 10f), 0) };
        planner.Enqueue(new NavPath(NavPathStatus.Complete, backing));
        var follower = new PathFollower(planner);

        follower.Tick(new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 10f), AgentRadius, 0.016f);

        // The read-only wrap at NavPath construction blocks the downcast attack: a reader can never
        // reach the live committed corridor's mutable storage through ActivePath.
        Assert.NotNull(follower.ActivePath);
        Assert.Throws<InvalidCastException>(() => { _ = (List<NavWaypoint>)follower.ActivePath!.Waypoints; });
        Assert.Throws<InvalidCastException>(() => { _ = (NavWaypoint[])follower.ActivePath!.Waypoints; });
    }

    [Fact]
    public void ActivePath_Waypoints_FromPlannerStraightShotFastPath_CannotBeDowncastToTheBackingArray()
    {
        // The straight-shot fast path in GridPathPlanner builds its NavPath over a raw array, so this
        // proves the construction-time wrap covers real planner-produced paths end to end.
        NavGrid grid = NavGrid.FromWalkable(20, 20, 1f, 0f, 0f, (_, _) => true);
        var planner = new GridPathPlanner(NavSpace.Single(grid));
        var follower = new PathFollower(planner);

        follower.Tick(new Vector3(5f, 0f, 5f), new Vector3(10f, 0f, 10f), AgentRadius, 0.016f);

        Assert.NotNull(follower.ActivePath);
        Assert.Throws<InvalidCastException>(() => { _ = (NavWaypoint[])follower.ActivePath!.Waypoints; });
        Assert.Throws<InvalidCastException>(() => { _ = (List<NavWaypoint>)follower.ActivePath!.Waypoints; });
    }

    [Fact]
    public void ActivePath_WhenPlannerReturnsUnreachable_IsNull()
    {
        var planner = new FakePlanner();
        planner.Enqueue(NavPath.Unreachable);
        var follower = new PathFollower(planner);

        PathFollowOutput output = follower.Tick(new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 10f), AgentRadius, 0.016f);

        Assert.Equal(PathFollowState.Unreachable, output.State);
        Assert.Null(follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void ActivePath_AfterArrival_IsNull()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), new NavWaypoint(new Vector2(19f, 19f), 0)));
        var follower = new PathFollower(planner);
        Vector3 goal = new Vector3(20f, 0f, 20f);

        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.016f);
        follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.016f);
        PathFollowOutput arrived = follower.Tick(new Vector3(19f, 0f, 19f), goal, AgentRadius, 0.016f);

        Assert.Equal(PathFollowState.Arrived, arrived.State);
        Assert.Null(follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void Tick_SteeringTowardHopWaypoint_ReturnsHoppingWithSuspendedSteering()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), Hop(10f, 10f)));
        var follower = new PathFollower(planner);
        Vector3 goal = new Vector3(10f, 0f, 10f);

        // Tick 1 plans and steers at the takeoff waypoint (5,5).
        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.016f);
        // Tick 2 reaches the takeoff, so the advance-past-reached step moves the index onto the Hop landing.
        PathFollowOutput hopping = follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.016f);

        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Hopping, hopping.State);
        Assert.Equal(Vector2.Zero, hopping.WorldDir);
        Assert.Equal(new Vector2(5f, 5f), hopping.HopStart);
        Assert.Equal(new Vector2(10f, 10f), hopping.ActiveWaypoint);
        // The committed corridor and active index read normally throughout the hop.
        Assert.NotNull(follower.ActivePath);
        Assert.Equal(1, follower.ActiveWaypointIndex);
        Assert.Equal(NavWaypointKind.Hop, follower.ActivePath!.Waypoints[follower.ActiveWaypointIndex].Kind);
    }

    [Fact]
    public void Tick_HopFirstSegment_HopStartIsPlanOrigin()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(Hop(10f, 10f)));
        var follower = new PathFollower(planner);

        // Landing is index 0, so the takeoff is the plan origin (the agent's position when it planned).
        PathFollowOutput hopping = follower.Tick(new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 10f), AgentRadius, 0.016f);

        Assert.Equal(PathFollowState.Hopping, hopping.State);
        Assert.Equal(Vector2.Zero, hopping.WorldDir);
        Assert.Equal(new Vector2(0f, 0f), hopping.HopStart);
        Assert.Equal(new Vector2(10f, 10f), hopping.ActiveWaypoint);
    }

    [Fact]
    public void Tick_ReachingHopLanding_ResumesFollowingOrArrived()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), Hop(10f, 10f), new NavWaypoint(new Vector2(15f, 15f), 0)));
        var follower = new PathFollower(planner);
        Vector3 goal = new Vector3(15f, 0f, 15f);

        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.016f);
        PathFollowOutput hopping = follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.016f);
        Assert.Equal(PathFollowState.Hopping, hopping.State);

        // Consumer has lunged the agent onto the landing: the follower advances past it and resumes normal
        // ground steering toward the next waypoint, with HopStart back to zero.
        PathFollowOutput resumed = follower.Tick(new Vector3(10f, 0f, 10f), goal, AgentRadius, 0.016f);

        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Following, resumed.State);
        Assert.Equal(new Vector2(15f, 15f), resumed.ActiveWaypoint);
        Assert.Equal(Vector2.Zero, resumed.HopStart);
        Assert.Equal(Vector2.Normalize(new Vector2(5f, 5f)), resumed.WorldDir);
        Assert.Equal(2, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void Tick_HopLandingIsGoal_ArrivesOnReachingLanding()
    {
        // The composed case: the planner marked the final waypoint Hop with its position adjusted to the
        // exact goal. The follower emits Hopping on approach, then Arrived when the landing (the goal) is
        // reached, caught by the step-2 goal-distance check.
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), Hop(10f, 10f)));
        var follower = new PathFollower(planner);
        Vector3 goal = new Vector3(10f, 0f, 10f);

        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.016f);
        PathFollowOutput hopping = follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.016f);
        Assert.Equal(PathFollowState.Hopping, hopping.State);
        Assert.Equal(new Vector2(10f, 10f), hopping.ActiveWaypoint);

        PathFollowOutput arrived = follower.Tick(new Vector3(10f, 0f, 10f), goal, AgentRadius, 0.016f);

        Assert.Equal(1, planner.CallCount);
        Assert.Equal(PathFollowState.Arrived, arrived.State);
        Assert.Equal(Vector2.Zero, arrived.WorldDir);
        Assert.Equal(Vector2.Zero, arrived.ActiveWaypoint);
        Assert.Equal(Vector2.Zero, arrived.HopStart);
        Assert.Null(follower.ActivePath);
    }

    [Fact]
    public void Tick_HopNotCompleted_ReEmitsHoppingEachTick()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), Hop(10f, 10f)));
        var follower = new PathFollower(planner);
        Vector3 goal = new Vector3(10f, 0f, 10f);

        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.016f);

        // The consumer never moves the agent off the takeoff. The goal is static so no replan is due, and the
        // follower owns no hop timer, so it honestly re-emits the same Hopping every tick.
        for (int i = 0; i < 5; i++)
        {
            PathFollowOutput hopping = follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.016f);
            Assert.Equal(PathFollowState.Hopping, hopping.State);
            Assert.Equal(Vector2.Zero, hopping.WorldDir);
            Assert.Equal(new Vector2(5f, 5f), hopping.HopStart);
            Assert.Equal(new Vector2(10f, 10f), hopping.ActiveWaypoint);
        }

        // No replan fired across the stuck ticks.
        Assert.Equal(1, planner.CallCount);
    }

    [Fact]
    public void Tick_ReplanDuringHop_Supersedes()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), Hop(10f, 10f)));
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(25f, 25f), 0)));
        var config = new PathFollowConfig { GoalRetargetTolerance = 1.5f, ReplanCooldownSeconds = 0.5f };
        var follower = new PathFollower(planner, config);

        follower.Tick(new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 10f), AgentRadius, 0.5f);
        PathFollowOutput hopping = follower.Tick(new Vector3(5f, 0f, 5f), new Vector3(10f, 0f, 10f), AgentRadius, 0.5f);
        Assert.Equal(PathFollowState.Hopping, hopping.State);

        // The goal drifts far past GoalRetargetTolerance and the cooldown has drained, so the normal replan
        // trigger fires and supersedes the hop: the fresh route steers on the ground with HopStart zero.
        PathFollowOutput replanned = follower.Tick(new Vector3(5f, 0f, 5f), new Vector3(25f, 0f, 25f), AgentRadius, 0.5f);

        Assert.Equal(2, planner.CallCount);
        Assert.Equal(PathFollowState.Following, replanned.State);
        Assert.Equal(new Vector2(25f, 25f), replanned.ActiveWaypoint);
        Assert.Equal(Vector2.Zero, replanned.HopStart);
    }

    [Fact]
    public void Tick_NonHopWaypoint_HopStartIsZero()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), new NavWaypoint(new Vector2(10f, 10f), 0)));
        var follower = new PathFollower(planner);

        PathFollowOutput following = follower.Tick(new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 10f), AgentRadius, 0.016f);

        Assert.Equal(PathFollowState.Following, following.State);
        Assert.Equal(Vector2.Zero, following.HopStart);
    }

    [Fact]
    public void Tick_PathWithoutHopWaypoints_NeverEmitsHopping()
    {
        // A plain corridor with no Hop waypoint behaves exactly as before: never Hopping, HopStart always
        // zero, across the full walk from first waypoint to arrival.
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), new NavWaypoint(new Vector2(19f, 19f), 0)));
        var follower = new PathFollower(planner);
        Vector3 goal = new Vector3(20f, 0f, 20f);

        PathFollowOutput a = follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.016f);
        PathFollowOutput b = follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.016f);
        PathFollowOutput c = follower.Tick(new Vector3(19f, 0f, 19f), goal, AgentRadius, 0.016f);

        foreach (PathFollowOutput output in new[] { a, b, c })
        {
            Assert.NotEqual(PathFollowState.Hopping, output.State);
            Assert.Equal(Vector2.Zero, output.HopStart);
        }
        Assert.Equal(PathFollowState.Arrived, c.State);
    }

    [Fact]
    public void Reset_DuringHop_ClearsState()
    {
        var planner = new FakePlanner();
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), Hop(10f, 10f)));
        planner.Enqueue(Complete(new NavWaypoint(new Vector2(5f, 5f), 0), Hop(10f, 10f)));
        var follower = new PathFollower(planner);
        Vector3 goal = new Vector3(10f, 0f, 10f);

        follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.016f);
        PathFollowOutput hopping = follower.Tick(new Vector3(5f, 0f, 5f), goal, AgentRadius, 0.016f);
        Assert.Equal(PathFollowState.Hopping, hopping.State);
        Assert.NotNull(follower.ActivePath);

        follower.Reset();

        // Reset clears everything: no committed path, index back to zero, and the next tick plans fresh.
        Assert.Null(follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);

        PathFollowOutput afterReset = follower.Tick(new Vector3(0f, 0f, 0f), goal, AgentRadius, 0.016f);
        Assert.Equal(2, planner.CallCount);
        Assert.Equal(PathFollowState.Following, afterReset.State);
        Assert.Equal(new Vector2(5f, 5f), afterReset.ActiveWaypoint);
    }

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
}
