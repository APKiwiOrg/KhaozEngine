using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class PathFollowerTerminalReplanTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void OnlyExactExhaustionRefreshesAnObsoleteCompleteRouteBeforeCooldown(int phase)
    {
        var planner = new AnchorPlanner();
        var follower = new PathFollower(planner);
        var endpoint = new Vector3(20.625f, 0f, -30.125f);
        follower.Tick(endpoint + Vector3.UnitZ, Region(endpoint), 0.3f, 1f / 30f);
        var moved = Region(endpoint - Vector3.UnitZ);
        float z = phase switch
        {
            -1 => MathF.BitDecrement(endpoint.Z),
            1 => MathF.BitIncrement(endpoint.Z),
            _ => endpoint.Z,
        };

        PathFollowOutput output = follower.Tick(endpoint with { Z = z }, moved, 0.3f, 1f / 30f);

        Assert.Equal(phase == 0 ? 2 : 1, planner.Calls);
        Assert.Equal(PathFollowState.Following, output.State);
        Assert.Equal(phase == -1 ? Vector2.UnitY : -Vector2.UnitY, output.WorldDir);
        Assert.Equal(phase == 0 ? new Vector2(20.625f, -31.125f) : new Vector2(20.625f, -30.125f), output.ActiveWaypoint);

        if (phase != 0)
        {
            PathFollowOutput exhausted = follower.Tick(endpoint, moved, 0.3f, 1f / 30f);
            Assert.Equal(2, planner.Calls);
            Assert.Equal(PathFollowState.Following, exhausted.State);
            Assert.Equal(-Vector2.UnitY, exhausted.WorldDir);
            Assert.Equal(new Vector2(20.625f, -31.125f), exhausted.ActiveWaypoint);
        }
    }

    static NavGoalRegion Region(Vector3 anchor) => new(anchor, 0f, feet => feet == anchor);

    sealed class AnchorPlanner : IRegionPathPlanner
    {
        public int Calls { get; private set; }
        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)
        {
            Calls++;
            return new(NavPathStatus.Complete, [new(new(goal.Anchor.X, goal.Anchor.Z), 0)]);
        }
        public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget)
            => throw new InvalidOperationException("Region following must use region queries.");
    }
}
