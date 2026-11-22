using System;
using KhaozEngine.Navigation;
using Xunit;
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
            new GridPathPlanner.GridPlanningWorkspaces(constrainedPlanner, constrainedPlanner, 1, PathQueryBudget.Default));
    }

    [Fact]
    public void PoolRejectsARequestCreatedByAnotherPool()
    {
        var planner = new GridPathPlanner(NavSpace.Single(DisconnectedPlannerFixture.Grid(1, 1, (_, _) => true)));
        PathQueryBudget budget = PathQueryBudget.Default;
        using var first = Assert.IsType<GridPathPlanner.GridPlanningWorkspaces>(planner.CreateRequestWorkspaces(planner, 1, budget));
        using var second = Assert.IsType<GridPathPlanner.GridPlanningWorkspaces>(planner.CreateRequestWorkspaces(planner, 1, budget));
        using var foreign = Assert.IsType<GridPathPlanner.GridSearchRequest>(first.Begin(Start, End(1), 0f, budget, null));

        Assert.Throws<ArgumentException>("createRequest", () => second.Begin(Start, End(1), 0f, budget, _ => foreign));

        using var next = second.Begin(Start, End(1), 0f, budget, null);
        next.Advance(new(1));
        Assert.True(next.WorkSpent > 0);
        Assert.Equal(0, second.Waits);
    }
}
