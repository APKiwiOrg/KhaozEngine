using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class PathFollowerPointIntentTests
{
    static readonly Vector3 Endpoint = new(20.625f, 0f, -30.125f);
    const float Dt = 0.125f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshExhaustedPointResultKeepsCooldownWhenIntentChanges(bool replaceStaleRoute)
    {
        NavSpace space = Space();
        var planner = new Recorder(new GridPathPlanner(space));
        var follower = new PathFollower(planner, new() { VerticalAcceptTolerance = 0f }, space);
        Vector3 feet = Endpoint with { Y = 0.001f };
        if (replaceStaleRoute)
        {
            PathFollowOutput travelling = follower.Tick(feet + Vector3.UnitZ * 2f,
                Endpoint with { Y = 0.002f }, 0.3f, Dt);
            Assert.Equal(PathFollowState.Following, travelling.State);
            Assert.NotEqual(Vector2.Zero, travelling.WorldDir);
            Assert.Equal(1, planner.Calls);
        }

        int[] expectedCalls = replaceStaleRoute ? [2, 2, 2, 2, 3, 3, 3, 3, 4] : [1, 1, 1, 1, 2, 2, 2, 2, 3];
        PathFollowState[] expectedStates =
        [
            PathFollowState.Arrived, PathFollowState.WaitingForPath, PathFollowState.WaitingForPath, PathFollowState.WaitingForPath,
            PathFollowState.Arrived, PathFollowState.WaitingForPath, PathFollowState.WaitingForPath, PathFollowState.WaitingForPath,
            PathFollowState.Arrived,
        ];
        for (int tick = 0; tick < expectedCalls.Length; tick++)
        {
            // Height changes stay below retarget tolerance but identify a different current point intent.
            Vector3 goal = Endpoint with { Y = tick * 0.002f };
            PathFollowOutput output = follower.Tick(feet, goal, 0.3f, Dt);
            Assert.True(planner.Calls == expectedCalls[tick], $"Tick {tick}: expected {expectedCalls[tick]} queries, got {planner.Calls}.");
            Assert.Equal(expectedStates[tick], output.State);
            Assert.Equal(Vector2.Zero, output.WorldDir);
            Assert.Equal(Vector2.Zero, output.ActiveWaypoint);
            Assert.Null(follower.ActivePath);
        }
    }

    [Fact]
    public void ChangedPointWaitsForItsOwnQueryAfterAnUnreachableResult()
    {
        NavSpace space = Space();
        var planner = new Recorder(new GridPathPlanner(space));
        var follower = new PathFollower(planner, space: space);
        Assert.Equal(PathFollowState.Unreachable,
            follower.Tick(Endpoint, Endpoint + Vector3.UnitX * 100f, 0.3f, Dt).State);

        int[] expectedCalls = [1, 1, 1, 2, 2];
        PathFollowState[] expectedStates =
        [
            PathFollowState.WaitingForPath, PathFollowState.WaitingForPath, PathFollowState.WaitingForPath,
            PathFollowState.Following, PathFollowState.Following,
        ];
        for (int tick = 0; tick < expectedCalls.Length; tick++)
        {
            PathFollowOutput output = follower.Tick(Endpoint, Endpoint - Vector3.UnitZ, 0.3f, Dt);
            Assert.Equal(expectedCalls[tick], planner.Calls);
            Assert.Equal(expectedStates[tick], output.State);
            if (tick < 3)
            {
                Assert.Equal(Vector2.Zero, output.WorldDir);
                Assert.Null(follower.ActivePath);
            }
            else
            {
                Assert.Equal(-Vector2.UnitY, output.WorldDir);
                Assert.NotNull(follower.ActivePath);
            }
        }
    }

    [Fact]
    public void SamePointIntentStillCompletesAtItsSnappedEndpoint()
    {
        NavSpace space = Space();
        var planner = new Recorder(new GridPathPlanner(space));
        var follower = new PathFollower(planner, space: space);
        // The grid ends at X 26. The off-grid goal snaps to its last passable cell centre.
        var goal = new Vector3(26.75f, 0f, -30.125f);
        Assert.Equal(PathFollowState.Following, follower.Tick(Endpoint, goal, 0.3f, Dt).State);
        NavPath route = follower.ActivePath!;
        Assert.Equal(NavPathStatus.Complete, route.Status);
        Vector2 terminal = route.Waypoints[^1].Position;
        Assert.True(Vector2.Distance(terminal, new Vector2(goal.X, goal.Z)) > 0.6f);

        PathFollowOutput output = follower.Tick(new Vector3(terminal.X, 0f, terminal.Y), goal, 0.3f, Dt);

        Assert.Equal(1, planner.Calls);
        Assert.Equal(PathFollowState.Arrived, output.State);
        Assert.Equal(Vector2.Zero, output.WorldDir);
        Assert.Null(follower.ActivePath);
    }

    static NavSpace Space() => NavSpace.Single(NavGrid.FromSurfaces(40, 160, 0.25f, 16f, -48f,
        (_, _) => new NavSurfaceSample(true, 0f, 4f), 0.4f, 1.5f));

    sealed class Recorder(IPathPlanner inner) : IPathPlanner
    {
        public int Calls { get; private set; }
        public NavPath FindPath(Vector3 start, Vector3 goal, float radius, PathQueryBudget budget)
        {
            Calls++;
            return inner.FindPath(start, goal, radius, budget);
        }
    }
}
