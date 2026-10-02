using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class PathFollowerRegionTests
{
    const float AgentRadius = 0.3f;

    sealed class ScriptedPlanner : IRegionPathPlanner
    {
        readonly Queue<NavPath> _results = new();
        public int Calls { get; private set; }
        public bool LastCallWasRegion { get; private set; }
        public Vector3 LastStart { get; private set; }
        public NavGoalRegion? LastRegion { get; private set; }
        public float LastRadius { get; private set; }
        public PathQueryBudget LastBudget { get; private set; }

        public void Enqueue(NavPath path) => _results.Enqueue(path);

        public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget)
        {
            LastCallWasRegion = false;
            return Next(start, agentRadius, budget);
        }

        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)
        {
            LastCallWasRegion = true;
            LastRegion = goal;
            return Next(start, agentRadius, budget);
        }

        NavPath Next(Vector3 start, float agentRadius, PathQueryBudget budget)
        {
            Calls++;
            LastStart = start;
            LastRadius = agentRadius;
            LastBudget = budget;
            return _results.Count > 0 ? _results.Dequeue() : NavPath.Unreachable;
        }
    }

    sealed class PointPlanner : IPathPlanner
    {
        public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget)
            => throw new InvalidOperationException("Point fallback must not run.");
    }

    static NavWaypoint Walk(float x, float z, int layer = 0) => new(new Vector2(x, z), layer);
    static NavPath Complete(params NavWaypoint[] waypoints) => new(NavPathStatus.Complete, waypoints);
    static NavGoalRegion SmallRegion(Vector3 anchor) => new(anchor, 0.1f,
        feet => Vector3.Distance(feet, anchor) <= 0.1f);

    static NavSpace TwoFloors() => new(new[]
    {
        NavGrid.FromWalkable(32, 32, 1f, 0f, 0f, (_, _) => true, yMin: 0f, yMax: 2f),
        NavGrid.FromWalkable(32, 32, 1f, 0f, 0f, (_, _) => true, yMin: 2.5f, yMax: 6f),
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedPlannerThrowsEvenWhenTheStartIsAMember(bool containsStart)
    {
        var follower = new PathFollower(new PointPlanner());
        var region = new NavGoalRegion(Vector3.Zero, 0f, _ => containsStart);

        Assert.Throws<NotSupportedException>(() => follower.Tick(Vector3.Zero, region, AgentRadius, 0.016f));
    }

    [Fact]
    public void ActualMembershipArrivesWithoutPlanningToTheAnchor()
    {
        var planner = new ScriptedPlanner();
        var follower = new PathFollower(planner);
        var feet = new Vector3(8.5f, 0f, 0f);
        var region = new NavGoalRegion(new Vector3(10f, 4f, 0f), 2f, point => point == feet);

        PathFollowOutput output = follower.Tick(feet, region, AgentRadius, 0.016f);

        Assert.True(region.Contains(feet));
        Assert.Equal(PathFollowState.Arrived, output.State);
        Assert.Equal(Vector2.Zero, output.WorldDir);
        Assert.Null(follower.ActivePath);
        Assert.Equal(0, planner.Calls);
    }

    [Fact]
    public void FinalWaypointIsNotConsumedUntilTheBodyEntersTheRegion()
    {
        var planner = new ScriptedPlanner();
        planner.Enqueue(Complete(Walk(10f, 0f)));
        var follower = new PathFollower(planner);
        NavGoalRegion region = SmallRegion(new Vector3(10f, 0f, 0f));
        follower.Tick(Vector3.Zero, region, AgentRadius, 0.016f);
        var outsideFeet = new Vector3(9.5f, 0f, 0f);

        PathFollowOutput outsideFinalRegion = follower.Tick(outsideFeet, region, AgentRadius, 0.1f);

        Assert.False(region.Contains(outsideFeet));
        Assert.Equal(PathFollowState.Following, outsideFinalRegion.State);
        Assert.Equal(Vector2.UnitX, outsideFinalRegion.WorldDir);
        Assert.Equal(new Vector2(10f, 0f), outsideFinalRegion.ActiveWaypoint);
        Assert.NotNull(follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);

        var memberFeet = new Vector3(10f, 0f, 0f);
        PathFollowOutput actualMember = follower.Tick(memberFeet, region, AgentRadius, 0.016f);

        Assert.True(region.Contains(memberFeet));
        Assert.Equal(PathFollowState.Arrived, actualMember.State);
        Assert.Equal(Vector2.Zero, actualMember.WorldDir);
        Assert.Null(follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);
        Assert.Equal(1, planner.Calls);
    }

    [Fact]
    public void PartialEndWaitsForTheCooldownWithoutRawGoalMotion()
    {
        var planner = new ScriptedPlanner();
        planner.Enqueue(new NavPath(NavPathStatus.Partial, new[] { Walk(2f, 0f) }));
        planner.Enqueue(Complete(Walk(10f, 0f)));
        var follower = new PathFollower(planner);
        NavGoalRegion region = SmallRegion(new Vector3(10f, 0f, 0f));
        follower.Tick(Vector3.Zero, region, AgentRadius, 0.016f);
        var partialEnd = new Vector3(2f, 0f, 0f);

        PathFollowOutput exhaustedPartial = follower.Tick(partialEnd, region, AgentRadius, 0.1f);

        Assert.False(region.Contains(partialEnd));
        Assert.Equal(PathFollowState.WaitingForPath, exhaustedPartial.State);
        Assert.Equal(Vector2.Zero, exhaustedPartial.WorldDir);
        Assert.Equal(Vector2.Zero, exhaustedPartial.ActiveWaypoint);
        Assert.Null(follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);
        Assert.Equal(1, planner.Calls);

        PathFollowOutput stillWaiting = follower.Tick(partialEnd, region, AgentRadius, 0.1f);
        Assert.Equal(PathFollowState.WaitingForPath, stillWaiting.State);
        Assert.Equal(Vector2.Zero, stillWaiting.WorldDir);
        Assert.Equal(1, planner.Calls);

        PathFollowOutput freshRoute = follower.Tick(partialEnd, region, AgentRadius, 0.4f);
        Assert.Equal(2, planner.Calls);
        Assert.Equal(PathFollowState.Following, freshRoute.State);
        Assert.Equal(new Vector2(10f, 0f), freshRoute.ActiveWaypoint);
        Assert.NotNull(follower.ActivePath);
    }

    [Fact]
    public void WorldZeroWaypointIsFollowedAndConsumedAsARealWaypoint()
    {
        var planner = new ScriptedPlanner();
        planner.Enqueue(Complete(Walk(0f, 0f), Walk(2f, 0f)));
        var follower = new PathFollower(planner);
        NavGoalRegion region = SmallRegion(new Vector3(2f, 0f, 0f));

        PathFollowOutput towardZero = follower.Tick(new Vector3(-2f, 0f, 0f), region, AgentRadius, 0.016f);

        Assert.Equal(PathFollowState.Following, towardZero.State);
        Assert.Equal(Vector2.UnitX, towardZero.WorldDir);
        Assert.Equal(Vector2.Zero, towardZero.ActiveWaypoint);
        Assert.NotNull(follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);

        PathFollowOutput pastZero = follower.Tick(Vector3.Zero, region, AgentRadius, 0.016f);
        Assert.Equal(PathFollowState.Following, pastZero.State);
        Assert.Equal(new Vector2(2f, 0f), pastZero.ActiveWaypoint);
        Assert.Equal(1, follower.ActiveWaypointIndex);
        Assert.Equal(1, planner.Calls);
    }

    [Fact]
    public void IntermediateWaypointsRequireTheAgentsLayerBeforeAdvancing()
    {
        var planner = new ScriptedPlanner();
        planner.Enqueue(Complete(Walk(10f, 0f), Walk(10.5f, 0f, 1), Walk(20f, 0f, 1)));
        var follower = new PathFollower(planner, space: TwoFloors());
        NavGoalRegion region = SmallRegion(new Vector3(20f, 4f, 0f));

        PathFollowOutput belowStair = follower.Tick(new Vector3(10f, 0.5f, 0f), region, AgentRadius, 0.016f);

        Assert.Equal(PathFollowState.Following, belowStair.State);
        Assert.Equal(new Vector2(10.5f, 0f), belowStair.ActiveWaypoint);
        Assert.Equal(1, follower.ActiveWaypointIndex);

        PathFollowOutput upstairs = follower.Tick(new Vector3(10.5f, 4f, 0f), region, AgentRadius, 0.016f);
        Assert.Equal(new Vector2(20f, 0f), upstairs.ActiveWaypoint);
        Assert.Equal(2, follower.ActiveWaypointIndex);
        Assert.Equal(1, planner.Calls);
    }

    [Fact]
    public void FinalWaypointOnAnotherFloorDoesNotArriveAtTheSameXz()
    {
        var planner = new ScriptedPlanner();
        planner.Enqueue(Complete(Walk(10f, 0f, 1)));
        var follower = new PathFollower(planner, space: TwoFloors());
        NavGoalRegion region = SmallRegion(new Vector3(10f, 4f, 0f));
        var below = new Vector3(10f, 0.5f, 0f);

        PathFollowOutput output = follower.Tick(below, region, AgentRadius, 0.016f);

        Assert.False(region.Contains(below));
        Assert.Equal(PathFollowState.Following, output.State);
        Assert.Equal(Vector2.Zero, output.WorldDir);
        Assert.Equal(new Vector2(10f, 0f), output.ActiveWaypoint);
        Assert.NotNull(follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);
        Assert.Equal(1, planner.Calls);
    }

    [Theory]
    [InlineData(1.4f, 0.7f, false)]
    [InlineData(1.5f, 0.8f, false)]
    [InlineData(1.6f, 0f, true)]
    [InlineData(0f, 0.81f, true)]
    [InlineData(0f, 4f, true)]
    public void AnchorDriftUsesHorizontalAndVerticalRetargetTolerances(float xDrift, float yDrift, bool replan)
    {
        var planner = new ScriptedPlanner();
        planner.Enqueue(Complete(Walk(5f, 0f), Walk(20f, 0f)));
        planner.Enqueue(Complete(Walk(0f, 5f), Walk(20f, 0f)));
        var follower = new PathFollower(planner);
        follower.Tick(Vector3.Zero, SmallRegion(new Vector3(20f, 0f, 0f)), AgentRadius, 0.016f);

        PathFollowOutput output = follower.Tick(Vector3.Zero,
            SmallRegion(new Vector3(20f + xDrift, yDrift, 0f)), AgentRadius, 0.5f);

        Assert.Equal(replan ? 2 : 1, planner.Calls);
        Assert.Equal(replan ? new Vector2(0f, 5f) : new Vector2(5f, 0f), output.ActiveWaypoint);
        Assert.Equal(PathFollowState.Following, output.State);
    }

    [Fact]
    public void DueRetargetKeepsTheRouteUntilTheCooldownDrains()
    {
        var planner = new ScriptedPlanner();
        planner.Enqueue(Complete(Walk(5f, 0f)));
        planner.Enqueue(Complete(Walk(0f, 5f)));
        var follower = new PathFollower(planner);
        follower.Tick(Vector3.Zero, SmallRegion(new Vector3(20f, 0f, 0f)), AgentRadius, 0.016f);
        NavGoalRegion moved = SmallRegion(new Vector3(22f, 4f, 0f));

        PathFollowOutput cooling = follower.Tick(Vector3.Zero, moved, AgentRadius, 0.1f);
        Assert.Equal(1, planner.Calls);
        Assert.Equal(new Vector2(5f, 0f), cooling.ActiveWaypoint);

        PathFollowOutput replanned = follower.Tick(Vector3.Zero, moved, AgentRadius, 0.4f);
        Assert.Equal(2, planner.Calls);
        Assert.Equal(new Vector2(0f, 5f), replanned.ActiveWaypoint);
        Assert.Same(moved, planner.LastRegion);
    }

    [Fact]
    public void ResetDuringPartialCooldownPlansFreshImmediately()
    {
        var planner = new ScriptedPlanner();
        planner.Enqueue(new NavPath(NavPathStatus.Partial, new[] { Walk(2f, 0f) }));
        planner.Enqueue(Complete(Walk(0f, 5f)));
        var budget = new PathQueryBudget { MaxExpandedNodes = 17, SnapRadius = 3f };
        var follower = new PathFollower(planner, new PathFollowConfig { Budget = budget });
        NavGoalRegion region = SmallRegion(new Vector3(20f, 0f, 0f));
        follower.Tick(Vector3.Zero, region, AgentRadius, 0.016f);
        follower.Tick(new Vector3(2f, 0f, 0f), region, AgentRadius, 0.1f);

        follower.Reset();

        Assert.Null(follower.ActivePath);
        Assert.Equal(0, follower.ActiveWaypointIndex);
        PathFollowOutput output = follower.Tick(Vector3.Zero, region, AgentRadius, 0.016f);
        Assert.Equal(2, planner.Calls);
        Assert.Equal(PathFollowState.Following, output.State);
        Assert.Equal(new Vector2(0f, 5f), output.ActiveWaypoint);
        Assert.Equal(Vector3.Zero, planner.LastStart);
        Assert.Equal(AgentRadius, planner.LastRadius);
        Assert.Equal(budget, planner.LastBudget);
    }

    [Fact]
    public void HopSuspendsGroundSteeringUntilTheLandingLayerIsReached()
    {
        var planner = new ScriptedPlanner();
        planner.Enqueue(Complete(Walk(5f, 0f), Walk(10f, 0f, 1) with { Kind = NavWaypointKind.Hop }, Walk(15f, 0f, 1)));
        var follower = new PathFollower(planner, space: TwoFloors());
        NavGoalRegion region = SmallRegion(new Vector3(15f, 4f, 0f));
        follower.Tick(Vector3.Zero, region, AgentRadius, 0.016f);

        PathFollowOutput hopping = follower.Tick(new Vector3(5f, 0.5f, 0f), region, AgentRadius, 0.016f);
        Assert.Equal(PathFollowState.Hopping, hopping.State);
        Assert.Equal(Vector2.Zero, hopping.WorldDir);
        Assert.Equal(new Vector2(5f, 0f), hopping.HopStart);
        Assert.Equal(new Vector2(10f, 0f), hopping.ActiveWaypoint);

        PathFollowOutput belowLanding = follower.Tick(new Vector3(10f, 0.5f, 0f), region, AgentRadius, 0.016f);
        Assert.Equal(PathFollowState.Hopping, belowLanding.State);
        Assert.Equal(1, follower.ActiveWaypointIndex);

        PathFollowOutput landed = follower.Tick(new Vector3(10f, 4f, 0f), region, AgentRadius, 0.016f);
        Assert.Equal(PathFollowState.Following, landed.State);
        Assert.Equal(new Vector2(15f, 0f), landed.ActiveWaypoint);
        Assert.Equal(Vector2.Zero, landed.HopStart);
        Assert.Equal(2, follower.ActiveWaypointIndex);
        Assert.Equal(1, planner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingOverloadsDiscardsThePreviousModesRoute(bool regionFirst)
    {
        var planner = new ScriptedPlanner();
        planner.Enqueue(Complete(Walk(5f, 0f)));
        planner.Enqueue(Complete(Walk(0f, 5f)));
        var follower = new PathFollower(planner);
        var anchor = new Vector3(20f, 0f, 0f);
        NavGoalRegion region = SmallRegion(anchor);
        if (regionFirst)
            follower.Tick(Vector3.Zero, region, AgentRadius, 0.016f);
        else
            follower.Tick(Vector3.Zero, anchor, AgentRadius, 0.016f);

        PathFollowOutput output = regionFirst
            ? follower.Tick(Vector3.Zero, anchor, AgentRadius, 0.016f)
            : follower.Tick(Vector3.Zero, region, AgentRadius, 0.016f);

        Assert.Equal(2, planner.Calls);
        Assert.Equal(!regionFirst, planner.LastCallWasRegion);
        Assert.Equal(PathFollowState.Following, output.State);
        Assert.Equal(Vector2.UnitY, output.WorldDir);
        Assert.Equal(new Vector2(0f, 5f), output.ActiveWaypoint);
    }

    [Fact]
    public void UnreachableIsRetriedAfterTheSharedCooldown()
    {
        var planner = new ScriptedPlanner();
        planner.Enqueue(NavPath.Unreachable);
        planner.Enqueue(Complete(Walk(5f, 0f)));
        var follower = new PathFollower(planner);
        NavGoalRegion region = SmallRegion(new Vector3(20f, 0f, 0f));

        PathFollowOutput first = follower.Tick(Vector3.Zero, region, AgentRadius, 0.016f);
        PathFollowOutput cooling = follower.Tick(Vector3.Zero, region, AgentRadius, 0.1f);
        Assert.Equal(PathFollowState.Unreachable, first.State);
        Assert.Equal(PathFollowState.Unreachable, cooling.State);
        Assert.Equal(Vector2.Zero, cooling.WorldDir);
        Assert.Equal(1, planner.Calls);
        Assert.Null(follower.ActivePath);

        PathFollowOutput retry = follower.Tick(Vector3.Zero, region, AgentRadius, 0.4f);
        Assert.Equal(2, planner.Calls);
        Assert.Equal(PathFollowState.Following, retry.State);
    }
}
