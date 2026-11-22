using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Navigation.GridPathPlannerDisconnectedProgressTests;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;

namespace KhaozEngine.Tests.Navigation;

public sealed class GridSearchWorkAccountingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void DisconnectedCertificateChargesEveryComparisonInEachBatch(int quota)
    {
        var query = Disconnected("certificate", 3, 2, certified: true);
        var planner = Incremental(query.Planner);
        using var pool = planner.CreateWorkspaces(1, query.Budget);
        using var request = Assert.IsType<GridPathPlanner.GridSearchRequest>(
            planner.BeginPath(query.Start, query.Goal, 0f, query.Budget, pool));
        var units = new PathWorkByKind();

        while (request.State == PathPlanState.Pending)
            request.AdvanceForTests(new(quota), units);

        Assert.Same(NavPath.Unreachable, request.Result);
        Assert.Equal(2, units[PathWorkKind.Snap]);
        Assert.Equal(2, units[PathWorkKind.MemberBox]);
        Assert.Equal(8, units[PathWorkKind.Member]);
        Assert.Equal(2, units[PathWorkKind.CertificateLabel]);
        Assert.Equal(6, units[PathWorkKind.CertificateProgress]);
        Assert.Equal(20, units.Total);
        Assert.Equal(request.WorkSpent, units.Total);
    }
}
