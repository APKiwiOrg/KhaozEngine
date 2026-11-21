using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;
using Phase = KhaozEngine.Navigation.GridPathPlanner.SearchPhase;

namespace KhaozEngine.Tests.Navigation;

public partial class GridSearchRequestEqualityTests
{
    [Theory]
    [InlineData(2_147_483_520f, 0.5f, 127f)]
    [InlineData(0.5f, 2_147_483_520f, 127f)]
    [InlineData(-2_147_483_520f, 0.5f, 128f)]
    [InlineData(0.5f, -2_147_483_520f, 128f)]
    public void SnapRingAtIntegerBoundaryIsUnreachable(float x, float z, float snapRadius)
    {
        var planner = Incremental(Corridor());
        var endpoint = new Vector3(x, 0f, z);
        NavGoalRegion goal = End();
        PathQueryBudget budget = PathQueryBudget.Default with { SnapRadius = snapRadius };
        using var pool = planner.CreateWorkspaces(1, budget);
        Assert.Equal(NavPathStatus.Unreachable, planner.FindPath(endpoint, goal.Anchor, 0f, budget).Status);
        Assert.Equal(NavPathStatus.Unreachable, planner.FindPath(endpoint, goal, 0f, budget).Status);
        Assert.Equal(NavPathStatus.Unreachable, planner.FindPath(Start, endpoint, 0f, budget).Status);
        using var request = planner.BeginPath(endpoint, goal, 0f, budget, pool);
        Assert.Equal(NavPathStatus.Unreachable, Finish(request, 64).Status);
    }

    [Fact]
    public void EqualPriceLinksKeepTheFirstLandingKind()
    {
        RegionQuery query = EqualPriceLinks();
        var planner = Incremental(query.Planner);
        using var pool = planner.CreateWorkspaces(1, query.Budget);
        using var request = planner.BeginPath(query.Start, query.Goal, 0f, query.Budget, pool);
        NavPath path = Finish(request, 1);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(NavWaypointKind.Hop, path.Waypoints[^1].Kind);
    }

    [Fact]
    public void StaleQueueEntryCostsAPopButNoProgressComparison()
    {
        RegionQuery query = StaleEntry();
        var planner = Incremental(query.Planner);
        using var pool = planner.CreateWorkspaces(1, query.Budget);
        using var handle = planner.BeginPath(query.Start, query.Goal, 0f, query.Budget, pool);
        var request = Assert.IsType<GridPathPlanner.GridSearchRequest>(handle);
        int pops = 0, comparisons = 0;
        for (int i = 0; request.State == PathPlanState.Pending; i++)
        {
            Assert.True(i < 1000);
            if (request.NextPhaseForTests == Phase.Pop) pops++;
            if (request.NextPhaseForTests == Phase.SearchProgress) comparisons++;
            request.Advance(new PathWorkBudget(1));
        }
        // One seed and two distinct popped nodes compare progress. The third pop is a duplicate.
        Assert.Equal(3, pops);
        Assert.Equal(3, comparisons);
        Assert.Equal(NavPathStatus.Partial, Assert.IsType<NavPath>(request.Result).Status);
    }

    [Theory]
    [InlineData("certified-terminal", 32, 2, 0, 1)]
    [InlineData("certificate-node-cap", 32_769, 1, 1, 0)]
    [InlineData("certificate-comparison-cap", 1025, 256, 1, 0)]
    public void CertificateTerminalAndCapsSpendWorkInTheExpectedPhase(string name, int nodes, int members,
        long searches, long certificates)
    {
        RegionQuery query = Disconnected(name, nodes, members);
        var planner = Incremental(query.Planner);
        var before = query.Planner.WorkForTests;
        Assert.Equal(NavPathStatus.Unreachable, planner.FindPath(query.Start, query.Goal, 0f, query.Budget).Status);
        var afterSync = query.Planner.WorkForTests;
        Assert.Equal((searches, certificates), (afterSync.Searches - before.Searches, afterSync.Certificates - before.Certificates));

        using var pool = planner.CreateWorkspaces(1, query.Budget);
        using var request = planner.BeginPath(query.Start, query.Goal, 0f, query.Budget, pool);
        Assert.Equal(NavPathStatus.Unreachable, Finish(request, 7).Status);
        var afterRequest = query.Planner.WorkForTests;
        Assert.Equal((searches, certificates),
            (afterRequest.Searches - afterSync.Searches, afterRequest.Certificates - afterSync.Certificates));
    }
}
