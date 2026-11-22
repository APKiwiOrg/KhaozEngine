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
        IIncrementalRegionPathPlanner constrained = new GridPathPlanner(space, graph, _ => throw new InvalidOperationException());
        IIncrementalRegionPathPlanner plain = new GridPathPlanner(space, graph);
        using var pool = plain.CreateWorkspaces(1, PathQueryBudget.Default);
        Assert.True(plain.SupportsRequests);
        Assert.False(constrained.SupportsRequests);
        Assert.Throws<NotSupportedException>(() => constrained.CreateWorkspaces(1, PathQueryBudget.Default));
        Assert.Throws<NotSupportedException>(() => constrained.BeginPath(Start, End(1), 0f, PathQueryBudget.Default, pool));
    }
}
