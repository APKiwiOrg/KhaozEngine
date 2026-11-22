using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Navigation.DisconnectedPlannerFixture;
using static KhaozEngine.Tests.Navigation.GridPathPlannerDisconnectedProgressTests;
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
        => AssertBothDrivers(EqualPriceLinks(), VerticalLanding(NavWaypointKind.Hop));

    [Fact]
    public void OrdinaryEdgeWinsBeforeAnEqualPriceLink()
    {
        var hop = new NavLink(0, 0, 0, 0, 1, 0) { Kind = NavLinkKind.Hop };
        var space = new NavSpace([Grid(2, 1, (_, _) => true)], [hop]);
        var query = new RegionQuery("ordinary-before-hop", new GridPathPlanner(space, hopCostCells: 1f),
            Start, End(2), Budget(2), NavPathStatus.Complete);
        AssertBothDrivers(query, new NavPath(NavPathStatus.Complete, [new NavWaypoint(new(1.5f, 0.5f), 0)]));
    }

    [Fact]
    public void OffsetOrderBreaksAnEqualFAndProgressTieAtTheCap()
    {
        // East and north are symmetric. Their opposite diagonal companions (west and south) are
        // blocked. The guarded exit mask refuses the direct northeast diagonal to the goal.
        var space = NavSpace.Single(Grid(3, 3, (x, z) => x >= 1 && z >= 1));
        var graph = new NavTraversalGraph(space, 0f, 1f,
            [new NavTraversalLayer(3, 3, [false, false, false, false, true, true, false, true, true],
                [0, 0, 0, 0, 5, 4, 0, 1, 0])], []);
        var start = new Vector3(1.5f, 0f, 1.5f);
        var goal = Member(new(2.5f, 0f, 2.5f));
        var query = new RegionQuery("offset-tie", new GridPathPlanner(space, graph), start, goal,
            Budget(2), NavPathStatus.Partial);
        AssertBothDrivers(query, new NavPath(NavPathStatus.Partial, [new NavWaypoint(new(2.5f, 1.5f), 0)]));
    }

    [Theory]
    [InlineData(NavLinkKind.Stair, NavPathStatus.Complete)]
    [InlineData(NavLinkKind.Hop, NavPathStatus.Unreachable)]
    [InlineData(NavLinkKind.Drop, NavPathStatus.Unreachable)]
    public void WalkOnlyAdmitsStairAndRefusesHopAndDrop(NavLinkKind kind, NavPathStatus status)
    {
        var link = new NavLink(0, 0, 0, 1, 0, 0) { Kind = kind };
        var space = new NavSpace([Grid(1, 1, (_, _) => true, 4f), Grid(1, 1, (_, _) => true)], [link]);
        var query = new RegionQuery("walk-only", new GridPathPlanner(space), Start with { Y = 4f },
            Member(Start), Budget(2) with { WalkOnly = true }, status);
        AssertBothDrivers(query, status == NavPathStatus.Complete
            ? VerticalLanding(NavWaypointKind.Walk) : NavPath.Unreachable);
    }

    [Fact]
    public void StaleEntryStillQueuedAtTheExpansionCapIsNotPopped()
    {
        RegionQuery query = StaleEntry();
        var planner = Incremental(query.Planner);
        PathQueryBudget budget = query.Budget with { MaxExpandedNodes = 2 };
        using var pool = planner.CreateWorkspaces(1, budget);
        using var request = planner.BeginPath(query.Start, query.Goal, 0f, budget, pool);
        EqualBits(StalePartial(), Finish(request, 1));
        // The corresponding exhausted route charges four more units for the stale pop.
        Assert.Equal(145, request.WorkSpent);
    }

    [Fact]
    public void SingleLegacyLayerCanRefuseBeforeAnySnapWork()
    {
        var planner = Incremental(Corridor());
        PathQueryBudget budget = Budget(8) with { SnapRadius = -2f };
        using var pool = planner.CreateWorkspaces(1, budget);
        using var request = Assert.IsType<GridPathPlanner.GridSearchRequest>(
            planner.BeginPath(Start, End(), 0f, budget, pool));
        Assert.Equal(Phase.Done, request.NextPhaseForTests);
        Assert.Equal(PathPlanState.Pending, request.State);
        Assert.Null(request.Result);
        request.Advance(new PathWorkBudget(1));
        Assert.Equal(0, request.WorkSpent);
        Assert.Equal(PathPlanState.Ready, request.State);
        Assert.Same(NavPath.Unreachable, request.Result);
    }

    static NavPath StalePartial() => new(NavPathStatus.Partial,
        [new NavWaypoint(new(0.5f, 0.5f), 0), new NavWaypoint(new(2.5f, 0.5f), 0)]);

    static NavPath VerticalLanding(NavWaypointKind kind) => new(NavPathStatus.Complete,
        [new NavWaypoint(new(0.5f, 0.5f), 0), new NavWaypoint(new(0.5f, 0.5f), 1) { Kind = kind }]);

    static void AssertBothDrivers(RegionQuery query, NavPath expected)
    {
        var planner = Incremental(query.Planner);
        EqualBits(expected, planner.FindPath(query.Start, query.Goal, 0f, query.Budget));
        using var pool = planner.CreateWorkspaces(1, query.Budget);
        foreach (int quantum in LiteralTraceQuanta)
        {
            using var request = planner.BeginPath(query.Start, query.Goal, 0f, query.Budget, pool);
            EqualBits(expected, Finish(request, quantum));
        }
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
