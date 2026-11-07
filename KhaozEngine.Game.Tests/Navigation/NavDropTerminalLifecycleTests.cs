using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavDropTerminalLifecycleTests
{
    static readonly Vector3 Source = new(0.5f, 4f, 0.5f);
    static readonly Vector3 Landing = new(1.5f, 2f, 0.5f);
    const float Dt = 0.125f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LandingRefreshesMovedGoalBeforeCooldownExpires(bool region)
    {
        var (space, planner) = Setup();
        var follower = new PathFollower(planner, space: space);
        PathFollowOutput Tick(Vector3 feet, Vector3 target) => region
            ? follower.Tick(feet, new NavGoalRegion(target, 0f, p => p == target), 0f, Dt)
            : follower.Tick(feet, target, 0f, Dt);
        PathFollowOutput launch = Tick(Source, Landing);
        Assert.Equal(PathFollowState.Dropping, launch.State);
        NavPath original = follower.ActivePath!;
        Assert.Equal(2, original.Waypoints.Count);
        Assert.Equal(NavWaypointKind.Drop, original.Waypoints[1].Kind);

        // One metre is below retarget tolerance but beyond arrival radius. The flight is shorter
        // than cooldown, so only the acknowledged stale terminal can make this query immediate.
        Vector3 latest = Landing + Vector3.UnitX;
        PathFollowOutput flight = Tick(Landing + Vector3.UnitY, latest);
        Assert.Equal(PathFollowState.Dropping, flight.State);
        Assert.Equal(launch.DropTraversal, flight.DropTraversal);
        Assert.Equal(Vector2.Zero, flight.WorldDir);
        Assert.Same(original, follower.ActivePath);
        Assert.Equal(1, planner.Calls);

        follower.CompleteDrop();
        Assert.Null(follower.ActivePath);
        PathFollowOutput landed = Tick(Landing, latest);
        Assert.Equal(2, planner.Calls);
        Assert.Equal(Landing, planner.LastStart);
        Assert.Equal(latest, planner.LastGoal);
        Assert.Equal(PathFollowState.Following, landed.State);
        Assert.Equal(Vector2.UnitX, landed.WorldDir);
        Assert.Null(landed.DropTraversal);
        Assert.NotSame(original, follower.ActivePath);
        Assert.DoesNotContain(follower.ActivePath!.Waypoints, w => w.Kind == NavWaypointKind.Drop);

        for (int tick = 0; tick < 8; tick++)
        {
            Assert.Equal(PathFollowState.Following, Tick(Landing, latest).State);
            Assert.Equal(2, planner.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshTerminalReplacementAfterLandingKeepsCooldown(bool region)
    {
        var (space, planner) = Setup();
        var follower = new PathFollower(planner, new() { VerticalAcceptTolerance = 0f }, space);
        PathFollowOutput Tick(Vector3 feet, Vector3 target) => region
            ? follower.Tick(feet, new NavGoalRegion(target, 0f, p => p == Landing), 0f, Dt)
            : follower.Tick(feet, target, 0f, Dt);
        Assert.Equal(PathFollowState.Dropping, Tick(Source, Landing).State);
        Vector3 changed = Landing with { Y = 2.002f };
        Assert.Equal(PathFollowState.Dropping, Tick(Landing + Vector3.UnitY, changed).State);
        Assert.Equal(1, planner.Calls);
        follower.CompleteDrop();

        // The real planner accepts the baked feet, while actual feet differ in height. Its fresh
        // start-cell result must not inherit the completed Drop's immediate-refresh permission.
        Vector3 actualFeet = Landing with { Y = 2.001f };
        int[] expectedCalls = [2, 2, 2, 2, 3, 3, 3, 3, 4];
        PathFollowState[] pointStates =
        [
            PathFollowState.Arrived, PathFollowState.WaitingForPath, PathFollowState.WaitingForPath, PathFollowState.WaitingForPath,
            PathFollowState.Arrived, PathFollowState.WaitingForPath, PathFollowState.WaitingForPath, PathFollowState.WaitingForPath,
            PathFollowState.Arrived,
        ];
        for (int tick = 0; tick < expectedCalls.Length; tick++)
        {
            Vector3 latest = Landing with { Y = 2.002f + tick * 0.002f };
            PathFollowOutput output = Tick(actualFeet, latest);
            Assert.Equal(expectedCalls[tick], planner.Calls);
            Assert.Equal(region ? PathFollowState.Following : pointStates[tick], output.State);
            Assert.Equal(Vector2.Zero, output.WorldDir);
            Assert.Null(output.DropTraversal);
            if (region)
            {
                Assert.Equal(NavPathStatus.Complete, follower.ActivePath!.Status);
                Assert.Single(follower.ActivePath.Waypoints);
                Assert.Equal(NavWaypointKind.Walk, follower.ActivePath.Waypoints[0].Kind);
            }
            else Assert.Null(follower.ActivePath);
        }
    }

    static (NavSpace Space, Recorder Planner) Setup()
    {
        var grid = NavGrid.FromBlockedSurfaces(3, 1, 1f, 0f, 0f, new bool[3], new float[] { 4f, 2f, 2f });
        var drop = new NavLink(0, 0, 0, 0, 1, 0) { Kind = NavLinkKind.Drop };
        var space = new NavSpace(new[] { grid }, new[] { drop });
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(3, 1, new[] { true, true, true }, new byte[] { 0, 1, 2 }) }, new[] { drop });
        return (space, new Recorder(new GridPathPlanner(space, graph)));
    }

    sealed class Recorder(IRegionPathPlanner inner) : IRegionPathPlanner
    {
        public int Calls { get; private set; }
        public Vector3 LastStart { get; private set; }
        public Vector3 LastGoal { get; private set; }
        public NavPath FindPath(Vector3 start, Vector3 goal, float radius, PathQueryBudget budget)
        {
            Calls++;
            LastStart = start;
            LastGoal = goal;
            return inner.FindPath(start, goal, radius, budget);
        }
        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float radius, PathQueryBudget budget)
        {
            Calls++;
            LastStart = start;
            LastGoal = goal.Anchor;
            return inner.FindPath(start, goal, radius, budget);
        }
    }
}
