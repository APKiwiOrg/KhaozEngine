using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class PathFollowerFreshTerminalTests
{
    static readonly Vector3 Endpoint = new(20.625f, 0f, -30.125f);
    const float Dt = 0.125f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshCompleteStartCellKeepsCooldownAcrossSuccessiveTicks(bool replaceStaleRoute)
    {
        NavSpace space = Space();
        var planner = new Recorder(new GridPathPlanner(space));
        var follower = new PathFollower(planner, space: space);
        var region = new NavGoalRegion(Endpoint, 0f, feet => feet == Endpoint);
        var actualFeet = Endpoint with { Y = 0.001f };
        Assert.False(region.Contains(actualFeet));
        if (replaceStaleRoute)
        {
            PathFollowOutput travelling = follower.Tick(actualFeet + Vector3.UnitZ * 0.75f, region, 0.3f, Dt);
            Assert.Equal(PathFollowState.Following, travelling.State);
            Assert.NotEqual(Vector2.Zero, travelling.WorldDir);
            Assert.Equal(1, planner.Calls);
        }

        int[] expectedCalls = replaceStaleRoute ? [2, 2, 2, 2, 3, 3, 3, 3, 4] : [1, 1, 1, 1, 2, 2, 2, 2, 3];
        for (int tick = 0; tick < expectedCalls.Length; tick++)
        {
            PathFollowOutput output = follower.Tick(actualFeet, region, 0.3f, Dt);
            Assert.True(planner.Calls == expectedCalls[tick], $"Tick {tick}: expected {expectedCalls[tick]} queries, got {planner.Calls}.");
            Assert.Equal(PathFollowState.Following, output.State);
            Assert.Equal(Vector2.Zero, output.WorldDir);
            Assert.Equal(new Vector2(20.625f, -30.125f), output.ActiveWaypoint);
            Assert.Equal(NavPathStatus.Complete, follower.ActivePath!.Status);
            Assert.Single(follower.ActivePath.Waypoints);
        }
    }

    [Fact]
    public void PointStartCellKeepsCooldownAfterConsumingTheFreshRoute()
    {
        NavSpace space = Space();
        var planner = new Recorder(new GridPathPlanner(space));
        var follower = new PathFollower(planner, new() { VerticalAcceptTolerance = 0f }, space);
        var actualFeet = Endpoint with { Y = 0.001f };
        int[] expectedCalls = [1, 1, 1, 1, 2, 2, 2, 2, 3];
        PathFollowState[] expectedStates =
        [
            PathFollowState.Arrived, PathFollowState.Unreachable, PathFollowState.Unreachable, PathFollowState.Unreachable,
            PathFollowState.Arrived, PathFollowState.Unreachable, PathFollowState.Unreachable, PathFollowState.Unreachable,
            PathFollowState.Arrived,
        ];
        for (int tick = 0; tick < expectedCalls.Length; tick++)
        {
            PathFollowOutput output = follower.Tick(actualFeet, Endpoint, 0.3f, Dt);
            Assert.Equal(expectedCalls[tick], planner.Calls);
            Assert.Equal(expectedStates[tick], output.State);
            Assert.Equal(Vector2.Zero, output.WorldDir);
            Assert.Null(follower.ActivePath);
        }
    }

    [Fact]
    public void PointTerminalConsumesReachedWaypointWithoutExactPosition()
    {
        NavSpace space = Space();
        var planner = new Recorder(new GridPathPlanner(space));
        var follower = new PathFollower(planner, space: space);
        Assert.Equal(PathFollowState.Following,
            follower.Tick(Endpoint + Vector3.UnitZ * 2f, Endpoint, 0.3f, Dt).State);
        var nearEndpoint = Endpoint with { X = MathF.BitDecrement(Endpoint.X) };

        PathFollowOutput output = follower.Tick(nearEndpoint, Endpoint - Vector3.UnitZ, 0.3f, Dt);

        Assert.Equal(1, planner.Calls);
        Assert.Equal(PathFollowState.Arrived, output.State);
        Assert.Equal(Vector2.Zero, output.WorldDir);
        Assert.Null(follower.ActivePath);
    }

    static NavSpace Space() => NavSpace.Single(NavGrid.FromSurfaces(40, 160, 0.25f, 16f, -48f,
        (_, _) => new NavSurfaceSample(true, 0f, 4f), 0.4f, 1.5f));

    sealed class Recorder(IRegionPathPlanner inner) : IRegionPathPlanner
    {
        public int Calls { get; private set; }
        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float radius, PathQueryBudget budget)
        {
            Calls++;
            return inner.FindPath(start, goal, radius, budget);
        }
        public NavPath FindPath(Vector3 start, Vector3 goal, float radius, PathQueryBudget budget)
        {
            Calls++;
            return inner.FindPath(start, goal, radius, budget);
        }
    }
}
