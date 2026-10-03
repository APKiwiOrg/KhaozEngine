using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class PathFollowerPassedWaypointTests
{
    const float AgentRadius = 0.3f;
    const float StrictRadius = 0.00001f;
    const float Dt = 0.016f;

    sealed class ScriptedPlanner : IRegionPathPlanner
    {
        readonly Queue<NavPath> _results = new();

        public void Enqueue(NavPath path) => _results.Enqueue(path);

        public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget) => Next();

        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget) => Next();

        NavPath Next() => _results.Count > 0 ? _results.Dequeue() : NavPath.Unreachable;
    }

    static NavWaypoint Walk(float x, float z, int layer = 0) => new(new Vector2(x, z), layer);
    static NavPath Complete(params NavWaypoint[] waypoints) => new(NavPathStatus.Complete, waypoints);
    static NavPath Straight() => Complete(Walk(0f, 0f), Walk(0.25f, 0f), Walk(0.5f, 0f), Walk(0.75f, 0f));
    static NavGoalRegion Unreached(Vector3 anchor) => new(anchor, 0.1f, _ => false);

    static PathFollowConfig Strict(bool consume) =>
        new() { AcceptRadius = StrictRadius, ConsumePassedCollinearWaypoints = consume };

    static PathFollower Follower(NavPath route, bool consume, NavSpace? space = null)
    {
        var planner = new ScriptedPlanner();
        planner.Enqueue(route);
        return new PathFollower(planner, Strict(consume), space);
    }

    static NavSpace TwoFloors() => new(new[]
    {
        NavGrid.FromWalkable(32, 32, 1f, 0f, 0f, (_, _) => true, yMin: 0f, yMax: 2f),
        NavGrid.FromWalkable(32, 32, 1f, 0f, 0f, (_, _) => true, yMin: 2.5f, yMax: 6f),
    });

    [Fact]
    public void PassedCollinearWaypointIsConsumedWhenOptedIn()
    {
        Assert.Equal(2, IndexAfterPassing(consume: true));
        Assert.Equal(1, IndexAfterPassing(consume: false));
    }

    static int IndexAfterPassing(bool consume)
    {
        PathFollower follower = Follower(Straight(), consume);
        NavGoalRegion region = Unreached(new Vector3(0.75f, 0f, 0f));
        follower.Tick(Vector3.Zero, region, AgentRadius, Dt);
        Assert.Equal(1, follower.ActiveWaypointIndex);

        PathFollowOutput output = follower.Tick(new Vector3(0.3f, 0f, 0f), region, AgentRadius, Dt);

        Assert.Equal(PathFollowState.Following, output.State);
        return follower.ActiveWaypointIndex;
    }

    [Fact]
    public void ConsumptionIsOffByDefault()
    {
        Assert.False(PathFollowConfig.Default.ConsumePassedCollinearWaypoints);
        Assert.False(new PathFollowConfig().ConsumePassedCollinearWaypoints);
    }

    [Fact]
    public void WaypointZeroIsNeverConsumedByPassing()
    {
        PathFollower follower = Follower(Straight(), consume: true);
        NavGoalRegion region = Unreached(new Vector3(0.75f, 0f, 0f));

        follower.Tick(new Vector3(0.1f, 0f, 0f), region, AgentRadius, Dt);

        Assert.Equal(0, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void CornerIsNotConsumedBeforeItIsReached()
    {
        PathFollower follower = Follower(Complete(Walk(0f, 0f), Walk(0.25f, 0f), Walk(0.25f, 0.25f)), consume: true);
        NavGoalRegion region = Unreached(new Vector3(0.25f, 0f, 0.25f));
        follower.Tick(Vector3.Zero, region, AgentRadius, Dt);
        Assert.Equal(1, follower.ActiveWaypointIndex);

        PathFollowOutput pastCorner = follower.Tick(new Vector3(0.26f, 0f, 0.001f), region, AgentRadius, Dt);

        Assert.Equal(1, follower.ActiveWaypointIndex);
        Assert.Equal(new Vector2(0.25f, 0f), pastCorner.ActiveWaypoint);

        follower.Tick(new Vector3(0.25f, 0f, 0f), region, AgentRadius, Dt);

        Assert.Equal(2, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void LateralMissKeepsTheWaypoint()
    {
        PathFollower follower = Follower(Straight(), consume: true);
        NavGoalRegion region = Unreached(new Vector3(0.75f, 0f, 0f));
        follower.Tick(Vector3.Zero, region, AgentRadius, Dt);

        follower.Tick(new Vector3(0.3f, 0f, 0.001f), region, AgentRadius, Dt);

        Assert.Equal(1, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void LayerMismatchKeepsTheWaypoint()
    {
        NavSpace space = TwoFloors();
        PathFollower follower = Follower(Straight(), consume: true, space);
        NavGoalRegion region = Unreached(new Vector3(0.75f, 0f, 0f));
        follower.Tick(Vector3.Zero, region, AgentRadius, Dt);
        Assert.Equal(1, follower.ActiveWaypointIndex);
        var upperFeet = new Vector3(0.3f, 3f, 0f);
        Assert.Equal(1, space.LayerAt(upperFeet));

        follower.Tick(upperFeet, region, AgentRadius, Dt);

        Assert.Equal(1, follower.ActiveWaypointIndex);

        follower.Tick(new Vector3(0.3f, 0f, 0f), region, AgentRadius, Dt);

        Assert.Equal(2, follower.ActiveWaypointIndex);
    }

    [Fact]
    public void LargeCoordinateAllowanceAdmitsOneFloatStep()
    {
        NavPath Diagonal() => Complete(
            Walk(150f, 150f), Walk(150.25f, 150.25f), Walk(150.5f, 150.5f), Walk(150.75f, 150.75f));
        NavGoalRegion region = Unreached(new Vector3(150.75f, 0f, 150.75f));
        float x = 150.3f;
        float oneStepZ = MathF.BitIncrement(x);
        double oneStepLateral = ((double)oneStepZ - x) / Math.Sqrt(2.0);
        Assert.True(oneStepLateral > StrictRadius);

        PathFollower oneStep = Follower(Diagonal(), consume: true);
        oneStep.Tick(new Vector3(150f, 0f, 150f), region, AgentRadius, Dt);
        Assert.Equal(1, oneStep.ActiveWaypointIndex);

        oneStep.Tick(new Vector3(x, 0f, oneStepZ), region, AgentRadius, Dt);

        Assert.Equal(2, oneStep.ActiveWaypointIndex);

        float halfOffset = 0.002f / MathF.Sqrt(2f);
        PathFollower twoMillimetres = Follower(Diagonal(), consume: true);
        twoMillimetres.Tick(new Vector3(150f, 0f, 150f), region, AgentRadius, Dt);

        twoMillimetres.Tick(new Vector3(x - halfOffset, 0f, x + halfOffset), region, AgentRadius, Dt);

        Assert.Equal(1, twoMillimetres.ActiveWaypointIndex);
    }

    [Fact]
    public void FinalCompleteRegionWaypointStillNeedsMembership()
    {
        var anchor = new Vector3(0.5f, 0f, 0f);
        var region = new NavGoalRegion(anchor, 0.1f, feet => Vector3.Distance(feet, anchor) <= 0.1f);
        PathFollower follower = Follower(Complete(Walk(0f, 0f), Walk(0.25f, 0f), Walk(0.5f, 0f)), consume: true);
        follower.Tick(Vector3.Zero, region, AgentRadius, Dt);
        var beyondFinal = new Vector3(0.65f, 0f, 0f);

        PathFollowOutput output = follower.Tick(beyondFinal, region, AgentRadius, Dt);

        Assert.False(region.Contains(beyondFinal));
        Assert.Equal(PathFollowState.Following, output.State);
        Assert.NotNull(follower.ActivePath);
        Assert.Equal(2, follower.ActiveWaypointIndex);
        Assert.Equal(new Vector2(0.5f, 0f), output.ActiveWaypoint);
        Assert.Equal(-Vector2.UnitX, output.WorldDir);
    }
}
