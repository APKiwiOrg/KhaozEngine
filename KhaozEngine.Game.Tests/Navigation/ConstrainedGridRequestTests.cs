using System;
using KhaozEngine.Navigation;
using Xunit;
using GridPlanningWorkspaces = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest.GridPlanningWorkspaces;
using GridSearchRequest = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest;
using RequestAdmission = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest.GridPlanningWorkspaces.RequestAdmission;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;

namespace KhaozEngine.Tests.Navigation;

public sealed class ConstrainedGridRequestTests
{
    [Fact]
    public void ConstrainedPlannerRefusesBothIncrementalDoorsWithoutCallingTheConstraint()
    {
        var space = NavSpace.Single(DisconnectedPlannerFixture.Grid(1, 1, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0f, 1f, [new NavTraversalLayer(1, 1, [true], [0])], []);
        var constrainedPlanner = new GridPathPlanner(space, graph, _ => throw new InvalidOperationException());
        IIncrementalRegionPathPlanner constrained = constrainedPlanner;
        IIncrementalRegionPathPlanner plain = new GridPathPlanner(space, graph);
        using var pool = plain.CreateWorkspaces(1, PathQueryBudget.Default);
        Assert.True(plain.SupportsRequests);
        Assert.False(constrained.SupportsRequests);
        Assert.Throws<NotSupportedException>(() => constrained.CreateWorkspaces(1, PathQueryBudget.Default));
        Assert.Throws<NotSupportedException>(() => constrained.BeginPath(Start, End(1), 0f, PathQueryBudget.Default, pool));
        Assert.Throws<NotSupportedException>(() =>
            new GridPlanningWorkspaces(constrainedPlanner, 1, PathQueryBudget.Default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PointOnlyGridRefusesWorkspacesBeforeCheckingTheCount(int count)
    {
        IIncrementalRegionPathPlanner pointOnly =
            new GridPathPlanner(NavSpace.Single(NavGrid.FromWalkable(3, 1, 1f, 0f, 0f, (_, _) => true)));

        Assert.False(pointOnly.SupportsRequests);
        Assert.Throws<NotSupportedException>(() => pointOnly.CreateWorkspaces(count, PathQueryBudget.Default));
    }

    [Fact]
    public void PointOnlyGridRefusesRegionBeginBeforeCheckingPoolOwnership()
    {
        IIncrementalRegionPathPlanner pointOnly =
            new GridPathPlanner(NavSpace.Single(NavGrid.FromWalkable(3, 1, 1f, 0f, 0f, (_, _) => true)));
        IIncrementalRegionPathPlanner physical = Corridor(3);
        using var foreign = physical.CreateWorkspaces(1, PathQueryBudget.Default);

        Assert.False(pointOnly.SupportsRequests);
        Assert.Throws<NotSupportedException>(() =>
            pointOnly.BeginPath(Start, End(3), 0f, PathQueryBudget.Default, foreign));
    }

    [Fact]
    public void PoolRejectsARequestCreatedByAnotherPool()
    {
        var planner = new GridPathPlanner(NavSpace.Single(DisconnectedPlannerFixture.Grid(1, 1, (_, _) => true)));
        PathQueryBudget budget = PathQueryBudget.Default;
        var adapter = new RequestAdapter(planner);
        using var first = new GridPlanningWorkspaces(adapter, 1, budget);
        using var second = new GridPlanningWorkspaces(adapter, 1, budget);
        using var foreign = adapter.CreateRequest(RequestAdmission.Admit(first, Start, End(1), 0f, budget));
        adapter.Foreign = foreign;

        Assert.Throws<ArgumentException>("adapter", () => second.BeginAdapted(adapter, Start, End(1), 0f, budget));

        adapter.Foreign = null;
        using var next = second.BeginAdapted(adapter, Start, End(1), 0f, budget);
        next.Advance(new(1));
        Assert.True(next.WorkSpent > 0);
        Assert.Equal(0, second.Waits);
    }

    sealed class RequestAdapter(GridPathPlanner planner) : IGridRequestAdapter
    {
        internal GridSearchRequest? Foreign { get; set; }

        GridPathPlanner IGridRequestAdapter.RequestGrid => planner;

        public GridSearchRequest CreateRequest(RequestAdmission admission) => Foreign ?? new AdaptedRequest(admission, this);
    }

    sealed class AdaptedRequest(RequestAdmission admission, IGridRequestAdapter adapter)
        : GridSearchRequest(admission, adapter);
}
