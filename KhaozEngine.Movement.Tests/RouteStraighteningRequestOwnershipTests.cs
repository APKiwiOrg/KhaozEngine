using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using GridPlanningWorkspaces = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest.GridPlanningWorkspaces;
using GridSearchRequest = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest;
using RequestAdmission = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest.GridPlanningWorkspaces.RequestAdmission;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;

namespace KhaozEngine.Tests.Movement;

public sealed partial class RouteStraighteningRequestTests
{
    [Fact]
    public void FactoryCannotReplaceTheAdmittedExpansionCap()
    {
        var navigation = Flat(9, 6, 0.25f);
        IGridRequestAdapter profile = (IGridRequestAdapter)navigation.Planner;
        PathQueryBudget capOne = Budget with { MaxExpandedNodes = 1 };
        var goal = Goal(Start with { X = 4.5f });
        var adapter = new TestRequestAdapter(profile.RequestGrid, (owner, admission) =>
            new TestAdaptedRequest(RequestAdmission.Admit(admission.Pool, admission.Start, admission.Goal,
                admission.Radius, Budget), owner));
        using var pool = new GridPlanningWorkspaces(adapter, 1, capOne);

        Assert.Throws<ArgumentException>("budget", () =>
        {
            using var oversized = pool.BeginAdapted(adapter, Start, goal, navigation.AgentRadius, capOne);
        });

        // The failed factory did not take the slot or put work into the queue.
        adapter.Create = (owner, admission) => new TestAdaptedRequest(admission, owner);
        using var next = pool.BeginAdapted(adapter, Start, goal, navigation.AgentRadius, capOne);
        next.Advance(new(1));
        Assert.True(next.WorkSpent > 0);
        Assert.NotEqual(NavPathStatus.Complete, Finish(next, [6]).Status);
        Assert.Equal(0, pool.Waits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BeginHookCannotCancelBeforeLeaseAssignment(bool hasOwner)
    {
        var navigation = Flat(9, 6, 0.25f);
        var goal = Goal(Start with { X = 4.5f });
        IGridRequestAdapter profile = (IGridRequestAdapter)navigation.Planner;
        var adapter = new TestRequestAdapter(profile.RequestGrid, (owner, admission) => new TestAdaptedRequest(admission, owner));
        using var pool = new GridPlanningWorkspaces(adapter, 1, Budget);
        using var owner = hasOwner ? Begin() : null;

        adapter.Create = (owner, admission) => new CancellingBeginRequest(admission, owner);
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var rejected = Begin();
        });
        Assert.Equal(0, pool.Waits);

        owner?.Cancel();
        adapter.Create = (owner, admission) => new TestAdaptedRequest(admission, owner);
        using var next = Begin();
        next.Advance(new(1));
        Assert.True(next.WorkSpent > 0);
        Assert.Equal(0, pool.Waits);

        PathPlanRequest Begin() => pool.BeginAdapted(adapter, Start, goal, navigation.AgentRadius, Budget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProfilePoolRefusesRawBeginThatWouldBypassStartAdmission(bool throughPlanner)
    {
        var navigation = Flat(9, 6, 0.25f);
        var start = new Vector3(0.125f, 0f, 2.5f);
        var goal = Goal(new(3.5f, 0f, 2.5f));
        using var pool = Assert.IsType<GridPlanningWorkspaces>(navigation.CreatePlanningWorkspaces(1, Budget));

        Assert.Throws<ArgumentException>("workspaces", () =>
        {
            using var raw = throughPlanner
                ? ((IIncrementalRegionPathPlanner)pool.Planner).BeginPath(start, goal, navigation.AgentRadius, Budget, pool)
                : pool.BeginRaw(start, goal, navigation.AgentRadius, Budget);
        });

        using var profile = ((IIncrementalRegionPathPlanner)navigation.Planner)
            .BeginPath(start, goal, navigation.AgentRadius, Budget, pool);
        Assert.Same(NavPath.Unreachable, Finish(profile, [1]));
        Assert.Equal(0, pool.Waits);
    }

    [Fact]
    public void AdapterPoolRefusesAnotherAdaptersRequestBeforeConsumingFallback()
    {
        var navigation = Flat(9, 6, 0.25f);
        var foreign = new RouteStraightener(navigation);
        var goal = Goal(Start with { X = 4.5f });
        NavPath previous = foreign.FindPath(Start, goal, navigation.AgentRadius, Budget);
        AssertBits(Path(NavPathStatus.Complete, Waypoint(4.5f)), previous);
        Assert.Same(previous, foreign.LastStraightened);
        foreign.FallBackOnce();

        IGridRequestAdapter foreignAdapter = foreign;
        var owner = new TestRequestAdapter(foreignAdapter.RequestGrid, (_, admission) => foreignAdapter.CreateRequest(admission));
        using var pool = new GridPlanningWorkspaces(owner, 1, Budget);
        Assert.Throws<ArgumentException>("adapter", () =>
        {
            using var request = pool.BeginAdapted(owner, Start, goal, navigation.AgentRadius, Budget);
        });

        Assert.Same(previous, foreign.LastStraightened);
        AssertBits(Path(NavPathStatus.Complete, Waypoint(2.5f), Waypoint(3.5f), Waypoint(4.5f)),
            foreign.FindPath(Start, goal, navigation.AgentRadius, Budget));
        Assert.Null(foreign.LastStraightened);
        owner.Create = (adapter, admission) => new TestAdaptedRequest(admission, adapter);
        using var next = pool.BeginAdapted(owner, Start, goal, navigation.AgentRadius, Budget);
        Assert.Equal(NavPathStatus.Complete, Finish(next, [6]).Status);
        Assert.Equal(0, pool.Waits);
    }

    [Fact]
    public void ExcludingFootprintWithSourceGraphKeepsSynchronousBehaviorAndRefusesRequests()
    {
        var source = Flat(3, 1, 0f, (x, _) => x == 1 ? 1u : 0u);
        var view = source.ExcludingAreas(1u);
        var tuning = MoveTuning.Default with { CapsuleRadius = 0f };
        var hybrid = new GroundNavigation(source.Graph, tuning, view.Footprint);
        var start = new Vector3(0.5f, 0f, 0.5f);
        var end = new Vector3(2.5f, 0f, 0.5f);
        var goal = Goal(end);
        var expected = Path(NavPathStatus.Complete, new NavWaypoint(new(1.5f, 0.5f), 0), new NavWaypoint(new(2.5f, 0.5f), 0));

        AssertBits(expected, hybrid.Planner.FindPath(start, goal, 0f, Budget));
        Assert.False(hybrid.AllowsSegment(start, end));
        var straightener = new RouteStraightener(hybrid);
        AssertBits(expected, straightener.FindPath(start, goal, 0f, Budget));
        using var unrelated = source.CreatePlanningWorkspaces(1, Budget);
        AssertUnavailable((IIncrementalRegionPathPlanner)hybrid.Planner);
        AssertUnavailable(straightener);

        void AssertUnavailable(IIncrementalRegionPathPlanner planner)
        {
            Assert.False(planner.SupportsRequests);
            Assert.Throws<NotSupportedException>(() => planner.CreateWorkspaces(1, Budget));
            Assert.Throws<NotSupportedException>(() => planner.BeginPath(start, goal, 0f, Budget, unrelated));
        }
    }

    sealed class TestRequestAdapter(GridPathPlanner grid, Func<IGridRequestAdapter, RequestAdmission, GridSearchRequest> create)
        : IGridRequestAdapter
    {
        internal Func<IGridRequestAdapter, RequestAdmission, GridSearchRequest> Create { get; set; } = create;

        GridPathPlanner IGridRequestAdapter.RequestGrid => grid;

        GridSearchRequest IGridRequestAdapter.CreateRequest(RequestAdmission admission) => Create(this, admission);
    }

    sealed class CancellingBeginRequest(RequestAdmission admission, IGridRequestAdapter adapter)
        : GridSearchRequest(admission, adapter)
    {
        private protected override void OnBegin() => CancelPending();
    }

    sealed class TestAdaptedRequest(RequestAdmission admission, IGridRequestAdapter adapter)
        : GridSearchRequest(admission, adapter);
}
