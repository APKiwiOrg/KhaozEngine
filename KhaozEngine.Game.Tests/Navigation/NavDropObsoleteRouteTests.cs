using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavDropObsoleteRouteTests
{
    static readonly Vector3 Start = new(0.5f, 4f, 0.5f);
    static readonly Vector3 Middle = new(1.5f, 4f, 0.5f);
    static readonly Vector3 Source = new(2.5f, 4f, 0.5f);
    static readonly Vector3 Landing = new(3.5f, 2f, 0.5f);
    static readonly Vector3 Far = new(5.5f, 2f, 0.5f);
    const float Dt = 0.125f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GoalMovedOntoTheLedgeRefreshesBeforeTheObsoleteDrop(bool region)
    {
        var (space, planner) = Setup();
        var follower = new PathFollower(planner, space: space);
        PathFollowOutput Tick(Vector3 feet, Vector3 target) => region
            ? follower.Tick(feet, new NavGoalRegion(target, 0f, p => p == target), 0f, Dt)
            : follower.Tick(feet, target, 0f, Dt);

        PathFollowOutput first = Tick(Start, Far);
        Assert.Equal(1, planner.Calls);
        Assert.Equal(PathFollowState.Following, first.State);
        Assert.Contains(follower.ActivePath!.Waypoints, w => w.Kind == NavWaypointKind.Drop);

        // The goal moves back past retarget tolerance inside the cooldown. The deferred walk is reversible.
        PathFollowOutput walking = Tick(Middle, Start);
        Assert.Equal(1, planner.Calls);
        Assert.Equal(PathFollowState.Following, walking.State);
        Assert.Equal(Vector2.UnitX, walking.WorldDir);

        // Reaching the source would make the obsolete Drop next. The due query runs before that commitment.
        PathFollowOutput atSource = Tick(Source, Start);
        Assert.Equal(2, planner.Calls);
        Assert.Equal(Source, planner.LastStart);
        Assert.Equal(Start, planner.LastGoal);
        Assert.Equal(PathFollowState.Following, atSource.State);
        Assert.Equal(-Vector2.UnitX, atSource.WorldDir);
        Assert.Null(atSource.DropTraversal);
        Assert.DoesNotContain(follower.ActivePath!.Waypoints, w => w.Kind == NavWaypointKind.Drop);

        // The refreshed result is fresh and keeps the ordinary cooldown across its expiry.
        for (int tick = 0; tick < 8; tick++)
        {
            PathFollowOutput output = Tick(Source, Start);
            Assert.Equal(2, planner.Calls);
            Assert.Equal(PathFollowState.Following, output.State);
            Assert.Null(output.DropTraversal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GoalMovedBelowRefreshesThenLocksTheCurrentDrop(bool region)
    {
        var (space, planner) = Setup();
        var follower = new PathFollower(planner, space: space);
        PathFollowOutput Tick(Vector3 feet, Vector3 target) => region
            ? follower.Tick(feet, new NavGoalRegion(target, 0f, p => p == target), 0f, Dt)
            : follower.Tick(feet, target, 0f, Dt);

        Assert.Equal(PathFollowState.Following, Tick(Start, Far).State);
        NavPath original = follower.ActivePath!;
        Assert.Equal(PathFollowState.Following, Tick(Middle, Landing).State);
        Assert.Equal(1, planner.Calls);

        PathFollowOutput launch = Tick(Source, Landing);
        Assert.Equal(2, planner.Calls);
        Assert.Equal(Landing, planner.LastGoal);
        Assert.Equal(PathFollowState.Dropping, launch.State);
        Assert.NotSame(original, follower.ActivePath);
        NavDropTraversal traversal = Assert.IsType<NavDropTraversal>(launch.DropTraversal);
        Assert.Equal(Source, traversal.StartFeet);
        Assert.Equal(Landing, traversal.LandingFeet);

        // A Drop on the refreshed route locks normally, so a later move during flight queries nothing.
        PathFollowOutput flight = Tick(Landing + Vector3.UnitY, Far);
        Assert.Equal(PathFollowState.Dropping, flight.State);
        Assert.Equal(launch.DropTraversal, flight.DropTraversal);
        Assert.Equal(2, planner.Calls);
    }

    [Fact]
    public void PointPartialRouteRefreshesBeforeTheObsoleteDrop()
    {
        var (space, _) = Setup();
        var planner = new ScriptedPlanner();
        var follower = new PathFollower(planner, space: space);

        Assert.Equal(PathFollowState.Following, follower.Tick(Start, Far, 0f, Dt).State);
        Assert.Equal(NavPathStatus.Partial, follower.ActivePath!.Status);
        PathFollowOutput walking = follower.Tick(Middle, Start, 0f, Dt);
        Assert.Equal(1, planner.Calls);
        Assert.Equal(Vector2.UnitX, walking.WorldDir);

        // Point Partial consumption runs after query eligibility, so the Drop is not yet active here.
        PathFollowOutput atSource = follower.Tick(Source, Start, 0f, Dt);
        Assert.Equal(2, planner.Calls);
        Assert.Equal(PathFollowState.Following, atSource.State);
        Assert.Equal(-Vector2.UnitX, atSource.WorldDir);
        Assert.Null(atSource.DropTraversal);
        for (int tick = 0; tick < 8; tick++)
        {
            Assert.Equal(PathFollowState.Following, follower.Tick(Source, Start, 0f, Dt).State);
            Assert.Equal(2, planner.Calls);
        }
    }

    [Fact]
    public void RegionMembershipOnTheLedgeArrivesBeforeAPendingDrop()
    {
        var (space, planner) = Setup();
        var follower = new PathFollower(planner, space: space);
        Assert.Equal(PathFollowState.Following,
            follower.Tick(Start, new NavGoalRegion(Far, 0f, p => p == Far), 0f, Dt).State);
        Assert.Contains(follower.ActivePath!.Waypoints, w => w.Kind == NavWaypointKind.Drop);

        // The goal region grows to contain the whole ledge while the Drop is still unlocked.
        PathFollowOutput output = follower.Tick(Middle, new NavGoalRegion(Far, 0f, p => p.Y == 4f), 0f, Dt);
        Assert.Equal(PathFollowState.Arrived, output.State);
        Assert.Equal(Vector2.Zero, output.WorldDir);
        Assert.Null(output.DropTraversal);
        Assert.Null(follower.ActivePath);
        Assert.Equal(1, planner.Calls);
    }

    [Fact]
    public void PointProximityAboveTheLandingStillDrops()
    {
        var (space, planner) = Setup();
        var follower = new PathFollower(planner, new PathFollowConfig
        {
            AcceptRadius = 0.6f, VerticalAcceptTolerance = float.PositiveInfinity,
        }, space);
        Assert.Equal(PathFollowState.Following, follower.Tick(Middle, Landing, 0f, Dt).State);

        // XZ proximity without height is no evidence of the lower goal, so the pending Drop still owns arrival.
        PathFollowOutput output = follower.Tick(Source with { X = 3.0f }, Landing, 0f, Dt);
        Assert.Equal(PathFollowState.Dropping, output.State);
        Assert.Equal(1, planner.Calls);
    }

    // Three ledge cells at height 4 reach three lower cells at height 2 only through the Drop from cell 2 to 3.
    static (NavSpace Space, Recorder Planner) Setup()
    {
        var grid = NavGrid.FromBlockedSurfaces(6, 1, 1f, 0f, 0f, new bool[6], new float[] { 4f, 4f, 4f, 2f, 2f, 2f });
        var drop = new NavLink(0, 2, 0, 0, 3, 0) { Kind = NavLinkKind.Drop };
        var space = new NavSpace(new[] { grid }, new[] { drop });
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(6, 1, new bool[] { true, true, true, true, true, true }, new byte[] { 1, 3, 2, 1, 3, 2 }) },
            new[] { drop });
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

    // A best-effort Partial toward the lower goal through the Drop, or a Complete walk back along the ledge.
    sealed class ScriptedPlanner : IPathPlanner
    {
        public int Calls { get; private set; }
        public NavPath FindPath(Vector3 start, Vector3 goal, float radius, PathQueryBudget budget)
        {
            Calls++;
            return goal.X > Source.X
                ? new(NavPathStatus.Partial, new[] { new NavWaypoint(new(1.5f, 0.5f), 0), new NavWaypoint(new(2.5f, 0.5f), 0),
                    new NavWaypoint(new(3.5f, 0.5f), 0) { Kind = NavWaypointKind.Drop } })
                : new(NavPathStatus.Complete, new[] { new NavWaypoint(new(1.5f, 0.5f), 0), new NavWaypoint(new(0.5f, 0.5f), 0) });
        }
    }
}
