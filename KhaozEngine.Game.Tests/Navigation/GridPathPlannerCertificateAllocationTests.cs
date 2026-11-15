using System;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Navigation.DisconnectedPlannerFixture;

namespace KhaozEngine.Tests.Navigation;

[Collection("AllocSensitive")]
public class GridPathPlannerCertificateAllocationTests
{
    [Fact]
    public void ImmutableGraphSharesLabelsAcrossPlannersAndCapturedRestrictions()
    {
        NavSpace space = DuckSpace();
        NavTraversalGraph graph = Graph(space, 0.2f);
        var planner = new GridPathPlanner(space, graph);
        Assert.True(planner.SharesConnectivityForTests(new GridPathPlanner(space, graph)));
        Assert.True(planner.SharesConnectivityForTests(new GridPathPlanner(space, graph, _ => false)));
        Assert.False(planner.SharesConnectivityForTests(new GridPathPlanner(space)));
        Assert.True(new GridPathPlanner(space).SharesConnectivityForTests(new GridPathPlanner(space)));
    }

    [Fact]
    public void WarmedCertifiedDuckQueryAllocatesNothing()
    {
        NavSpace space = DuckSpace();
        var planner = new GridPathPlanner(space, Graph(space, 0.2f));
        NavGoalRegion goal = DuckRegion();
        Func<NavPath> query = () => planner.FindPath(DuckStart, goal, 0.2f, PathQueryBudget.Default);
        Assert.Same(NavPath.Unreachable, query());
        Assert.Same(NavPath.Unreachable, query());
        AllocationMeasurement.NoPerCallAllocation("certified disconnected region", () => GC.KeepAlive(query()));
        Assert.Equal((0, 3), planner.WorkForTests);
    }
}
