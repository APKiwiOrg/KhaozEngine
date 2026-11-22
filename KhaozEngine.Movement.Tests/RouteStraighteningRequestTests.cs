using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using GridPlanningWorkspaces = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest.GridPlanningWorkspaces;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;

namespace KhaozEngine.Tests.Movement;

public sealed partial class RouteStraighteningRequestTests
{
    static readonly PathQueryBudget Budget = PathQueryBudget.Default with { MaxExpandedNodes = 128 };
    static readonly Vector3 Start = new(1.5f, 0f, 2.5f);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AreaExcludingViewOffersNoRequests(bool straighten)
    {
        GroundNavigation view = MarkedFloor().ExcludingAreas(1u);
        var planner = straighten ? new RouteStraightener(view) : (IIncrementalRegionPathPlanner)view.Planner;
        Assert.False(planner.SupportsRequests);
        Assert.Throws<NotSupportedException>(() => planner.CreateWorkspaces(1, Budget));
        using var pool = ((IIncrementalRegionPathPlanner)MarkedFloor().Planner).CreateWorkspaces(1, Budget);
        Assert.Throws<NotSupportedException>(() => planner.BeginPath(Start, Goal(Start), view.AgentRadius, Budget, pool));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProfileRefusesAForeignStraightenerBeforeCreatingWorkOrConsumingFallback(bool begin)
    {
        GroundNavigation source = MarkedFloor();
        GroundNavigation view = source.ExcludingAreas(1u);
        var foreign = new RouteStraightener(view);
        Vector3 end = new(6.5f, 0f, 2.5f);
        AssertBits(Path(NavPathStatus.Complete, Waypoint(2.5f), Waypoint(3.5f), Waypoint(4.5f), Waypoint(6.5f)),
            foreign.FindPath(Start, Goal(end), view.AgentRadius, Budget));

        // A route away from the mark can shorten. Arm fallback with that route already published.
        Vector3 safeStart = new(1.5f, 0f, 1.5f), safeEnd = new(3.5f, 0f, 1.5f);
        var safeGoal = Goal(safeEnd);
        NavPath previous = foreign.FindPath(safeStart, safeGoal, view.AgentRadius, Budget);
        Assert.Single(previous.Waypoints);
        Assert.Same(previous, foreign.LastStraightened);
        foreign.FallBackOnce();
        if (begin)
        {
            using var pool = source.CreatePlanningWorkspaces(1, Budget);
            Assert.Throws<ArgumentException>("straightener", () =>
                source.BeginPlanningPath(Start, Goal(end), source.AgentRadius, Budget, pool, foreign));
            Assert.Equal(0, pool.Waits);
        }
        else
        {
            Assert.Throws<ArgumentException>("straightener", () => source.CreatePlanningWorkspaces(1, Budget, foreign));
        }

        Assert.Same(previous, foreign.LastStraightened);
        AssertBits(Path(NavPathStatus.Complete, new NavWaypoint(new(2.5f, 1.5f), 0), new NavWaypoint(new(3.5f, 1.5f), 0)),
            foreign.FindPath(safeStart, safeGoal, view.AgentRadius, Budget));
        Assert.Null(foreign.LastStraightened);
    }

    [Fact]
    public void ProfileRefusesADelegateOnlyStraightener()
    {
        GroundNavigation source = MarkedFloor();
        var straightener = new RouteStraightener(source.Planner, source.Space, source.AllowsSegment);
        using var pool = source.CreatePlanningWorkspaces(1, Budget);
        Assert.Throws<ArgumentException>("straightener", () => source.CreatePlanningWorkspaces(1, Budget, straightener));
        Assert.Throws<ArgumentException>("straightener", () => source.BeginPlanningPath(
            Start, Goal(Start), source.AgentRadius, Budget, pool, straightener));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AreaExcludingViewKeepsItsSynchronousRoute(bool blocked)
    {
        GroundNavigation view = (blocked ? MarkedFloor() : Flat(9, 6, 0.25f)).ExcludingAreas(1u);
        var end = new Vector3(6.5f, 0f, 2.5f);
        var planner = new RouteStraightener(view);
        NavPath expected = blocked
            ? Path(NavPathStatus.Complete, Waypoint(2.5f), Waypoint(3.5f), Waypoint(4.5f), Waypoint(6.5f))
            : Path(NavPathStatus.Complete, Waypoint(6.5f));
        AssertBits(expected, planner.FindPath(Start, Goal(end), view.AgentRadius, Budget));
    }

    [Theory]
    [InlineData("flat")]
    [InlineData("large-footprint")]
    [InlineData("side-refusal")]
    [InlineData("layer")]
    [InlineData("hop")]
    [InlineData("unreachable")]
    [InlineData("single")]
    public void EqualsSynchronousRoute(string shape)
    {
        GroundNavigation nav = Flat(13, 7, 0.25f);
        Vector3 end = Start with { X = 6.5f };
        NavPath expected = Path(NavPathStatus.Complete, Waypoint(6.5f));
        NavGoalRegion goal;
        switch (shape)
        {
            case "large-footprint":
                nav = Flat(15, 9, 0.5f);
                end = Start with { X = 9.5f };
                expected = Path(NavPathStatus.Complete, Waypoint(9.5f));
                break;
            case "side-refusal":
                nav = MarkedFloor();
                var tuning = MoveTuning.Default with { CapsuleRadius = nav.AgentRadius };
                nav = Build(new NavAreaFootprint(nav.Footprint.Columns, nav.Footprint.Options, tuning, new(0u, 1u)), tuning);
                expected = Path(NavPathStatus.Complete, Waypoint(2.5f), Waypoint(3.5f), Waypoint(4.5f), Waypoint(6.5f));
                break;
            case "layer":
            case "hop":
                nav = Layered(shape == "hop" ? NavLinkKind.Hop : NavLinkKind.Stair);
                end = new(5.5f, 4f, Start.Z);
                expected = Path(NavPathStatus.Complete, Waypoint(3.5f),
                    Waypoint(4.5f, 1, shape == "hop" ? NavWaypointKind.Hop : NavWaypointKind.Walk), Waypoint(5.5f, 1));
                break;
            case "unreachable":
                end = new(30.5f, 0f, Start.Z);
                expected = NavPath.Unreachable;
                break;
            case "single":
                end = Start;
                expected = Path(NavPathStatus.Complete, Waypoint(1.5f));
                break;
        }
        goal = Goal(end);

        var planner = new RouteStraightener(nav);
        IIncrementalRegionPathPlanner incremental = planner;
        Assert.True(incremental.SupportsRequests);
        NavPath synchronous = planner.FindPath(Start, goal, nav.AgentRadius, Budget);
        AssertBits(expected, synchronous);
        bool removed = planner.LastStraightened is not null;
        using var pool = incremental.CreateWorkspaces(1, Budget);
        long? total = null;
        foreach (int[] schedule in new int[][] { [1], [6], [3, 1, 32, 2, 7, 13, 1], [int.MaxValue] })
        {
            using var request = incremental.BeginPath(Start, goal, nav.AgentRadius, Budget, pool);
            Assert.Null(planner.LastStraightened);
            NavPath actual = Finish(request, schedule);
            AssertBits(expected, actual);
            Assert.Same(removed ? actual : null, planner.LastStraightened);
            total ??= request.WorkSpent;
            Assert.Equal(total.Value, request.WorkSpent);
        }
    }

    [Fact]
    public void FallBackFlagConsumedAtBegin()
    {
        var nav = Flat(9, 6, 0.25f);
        NavGoalRegion goal = Goal(Start with { X = 6.5f });
        var straightener = new RouteStraightener(nav);
        IIncrementalRegionPathPlanner planner = straightener;
        NavPath raw = nav.Planner.FindPath(Start, goal, nav.AgentRadius, Budget);
        NavPath shortened = straightener.FindPath(Start, goal, nav.AgentRadius, Budget);
        Assert.Single(shortened.Waypoints);
        using var pool = planner.CreateWorkspaces(1, Budget);

        straightener.FallBackOnce();
        using var first = planner.BeginPath(Start, goal, nav.AgentRadius, Budget, pool);
        Assert.Null(straightener.LastStraightened);
        using var second = planner.BeginPath(Start, goal, nav.AgentRadius, Budget, pool);
        straightener.FallBackOnce();
        AssertBits(raw, Finish(first, [6]));
        Assert.Null(straightener.LastStraightened);
        AssertBits(shortened, Finish(second, [6]));
        using var third = planner.BeginPath(Start, goal, nav.AgentRadius, Budget, pool);
        AssertBits(raw, Finish(third, [6]));
        using var fourth = planner.BeginPath(Start, goal, nav.AgentRadius, Budget, pool);
        AssertBits(shortened, Finish(fourth, [6]));
    }

    [Fact]
    public void ThrowingAdvanceRecordsCompletedStartAndSearchWorkAndReleasesTheLease()
    {
        GroundNavigation navigation = Flat(1, 1, 0f);
        IIncrementalRegionPathPlanner planner = new RouteStraightener(navigation);
        var start = new Vector3(0.5f, 0f, 0.5f);
        var sentinel = new InvalidOperationException();
        using var pool = planner.CreateWorkspaces(1, Budget);
        using var owner = planner.BeginPath(start, new(start, 0f, _ => throw sentinel), 0f, Budget, pool);
        using var waiter = planner.BeginPath(start, Goal(start), 0f, Budget, pool);
        waiter.Advance(new(1));
        Assert.Equal(0, waiter.WorkSpent);

        Assert.Same(sentinel, Assert.Throws<InvalidOperationException>(() => owner.Advance(new(int.MaxValue))));
        Assert.Equal(12, owner.WorkSpent); // Start: 1 + 2 + 3 + 2. Snap and member box: 2 + 2.
        Assert.Equal(PathPlanState.Cancelled, owner.State);
        Assert.Null(owner.Result);
        AssertBits(Path(NavPathStatus.Complete, new NavWaypoint(new(0.5f, 0.5f), 0)), Finish(waiter, [6]));
    }

    [Fact]
    public void ProfileAndStraightenerKeepSeparatePoolOwnership()
    {
        GroundNavigation navigation = Flat(9, 6, 0.25f);
        var profile = (IIncrementalRegionPathPlanner)navigation.Planner;
        IIncrementalRegionPathPlanner straightener = new RouteStraightener(navigation);
        using var profilePool = profile.CreateWorkspaces(1, Budget);
        using var straightenerPool = straightener.CreateWorkspaces(1, Budget);
        NavGoalRegion goal = Goal(Start with { X = 4.5f });
        Assert.Throws<ArgumentException>(() => straightener.BeginPath(Start, goal, navigation.AgentRadius, Budget, profilePool));
        Assert.Throws<ArgumentException>(() => profile.BeginPath(Start, goal, navigation.AgentRadius, Budget, straightenerPool));
        using var request = profile.BeginPath(Start, goal, navigation.AgentRadius, Budget, profilePool);
        AssertBits(Path(NavPathStatus.Complete, Waypoint(2.5f), Waypoint(3.5f), Waypoint(4.5f)), Finish(request, [1]));
        Assert.True(request.StartsFrom(Start with { X = 1.75f }));
        Assert.False(request.StartsFrom(Start with { Y = 0.75f }));
    }

    [Fact]
    public void ProfileRequestRejectsAnotherProfilesGridPool()
    {
        GroundNavigation navigation = Flat(9, 6, 0.25f);
        using var pool = Assert.IsType<GridPlanningWorkspaces>(MarkedFloor().CreatePlanningWorkspaces(1, Budget));
        Assert.Throws<ArgumentException>("workspaces", () => pool.BeginAdapted((IGridRequestAdapter)navigation.Planner,
            Start, Goal(Start), navigation.AgentRadius, Budget));
    }

    static GroundNavigation MarkedFloor() => Flat(9, 6, 0.25f, (x, z) => x == 3 && z == 3 ? 1u : 0u);

    static GroundNavigation Layered(NavLinkKind kind)
    {
        var tuning = MoveTuning.Default with { CapsuleRadius = 0f };
        var columns = NavAreaFootprintRowsTests.Columns(NavAreaFootprintRowsTests.Options());
        var footprint = new NavAreaFootprint(columns, columns.Options, tuning, default, surfaceReach: 2d);
        return Build(footprint, tuning, 4f, [new NavLink(0, 3, 2, 1, 4, 2) { Kind = kind }]);
    }

    static NavPath Finish(PathPlanRequest request, int[] schedule)
    {
        for (int i = 0; request.State == PathPlanState.Pending; i++)
        {
            Assert.True(i < 20_000, "The request did not complete.");
            Assert.Null(request.Result);
            int quota = schedule[i % schedule.Length];
            long before = request.WorkSpent;
            request.Advance(new(quota));
            Assert.InRange(request.WorkSpent - before, 1, Math.Max(quota, 6));
        }

        return Assert.IsType<NavPath>(request.Result);
    }

    static NavWaypoint Waypoint(float x, int layer = 0, NavWaypointKind kind = NavWaypointKind.Walk)
        => new(new(x, 2.5f), layer) { Kind = kind };

    static NavPath Path(NavPathStatus status, params NavWaypoint[] waypoints) => new(status, waypoints);

    static void AssertBits(NavPath expected, NavPath actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Waypoints.Select(Bits), actual.Waypoints.Select(Bits));
    }

    static (int X, int Z, int Layer, NavWaypointKind Kind) Bits(NavWaypoint waypoint)
        => (BitConverter.SingleToInt32Bits(waypoint.Position.X), BitConverter.SingleToInt32Bits(waypoint.Position.Y),
            waypoint.Layer, waypoint.Kind);
}
