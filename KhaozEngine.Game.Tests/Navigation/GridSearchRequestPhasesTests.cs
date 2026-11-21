using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Navigation.DisconnectedPlannerFixture;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;
using Phase = KhaozEngine.Navigation.GridPathPlanner.SearchPhase;

namespace KhaozEngine.Tests.Navigation;

public partial class GridSearchRequestEqualityTests
{
    [Fact]
    public void PhaseBoundaryQuanta()
    {
        var (planner, goal) = DisconnectedCorridor();
        using var pool = planner.CreateWorkspaces(1, PathQueryBudget.Default);
        using var baseline = NewRequest(planner, pool, goal);
        var boundaries = new Dictionary<Phase, (long WorkSpent, Phase Next)>();
        for (int i = 0; baseline.State == PathPlanState.Pending; i++)
        {
            Assert.True(i < 1000);
            Phase phase = baseline.NextPhaseForTests;
            baseline.Advance(new PathWorkBudget(1));
            if (phase != baseline.NextPhaseForTests)
                boundaries[phase] = (baseline.WorkSpent, baseline.NextPhaseForTests);
        }

        using var request = NewRequest(planner, pool, goal);
        Phase[] cuts = { Phase.Member, Phase.CertificateProgress, Phase.Neighbor, Phase.Parent, Phase.Reverse, Phase.Copy };
        Array.Sort(cuts, (left, right) => boundaries[left].WorkSpent.CompareTo(boundaries[right].WorkSpent));
        foreach (Phase cut in cuts)
        {
            var (workSpent, next) = boundaries[cut];
            request.Advance(new PathWorkBudget(checked((int)(workSpent - request.WorkSpent))));
            Assert.Equal((workSpent, next), (request.WorkSpent, request.NextPhaseForTests));
        }
        Assert.Equal(PathPlanState.Ready, request.State);
        EqualBits(Assert.IsType<NavPath>(baseline.Result), Assert.IsType<NavPath>(request.Result));
    }

    [Fact]
    public void QuotaBelowLargestWeight()
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(1, PathQueryBudget.Default);
        using var request = NewRequest(planner, pool, End());
        bool exceededQuota = false;
        for (int i = 0; request.State == PathPlanState.Pending; i++)
        {
            Assert.True(i < 1000);
            int nextWeight = PathWorkWeights.For(request.NextPhaseForTests);
            long before = request.WorkSpent;
            request.Advance(new PathWorkBudget(1));
            long spent = request.WorkSpent - before;
            Assert.Equal(nextWeight, spent);
            Assert.InRange(spent, 1, PathWorkWeights.Largest);
            exceededQuota |= spent > 1;
        }
        Assert.True(exceededQuota);
        Assert.Equal(PathPlanState.Ready, request.State);
    }

    [Theory]
    [InlineData((int)Phase.Member)]
    [InlineData((int)Phase.CertificateProgress)]
    [InlineData((int)Phase.SearchProgress)]
    [InlineData((int)Phase.Parent)]
    public void CancellingASlicedPhaseReturnsOneWorkspace(int phase)
    {
        var (planner, goal) = DisconnectedCorridor();
        using var pool = planner.CreateWorkspaces(1, PathQueryBudget.Default);
        using var owner = NewRequest(planner, pool, goal);
        using var oldest = NewRequest(planner, pool, End());
        using var youngest = NewRequest(planner, pool, End());
        for (int i = 0; owner.NextPhaseForTests != (Phase)phase; i++)
        {
            Assert.True(i < 1000 && owner.State == PathPlanState.Pending, "the request never entered the selected phase");
            owner.Advance(new PathWorkBudget(1));
        }
        owner.Advance(new PathWorkBudget(1));
        owner.Cancel();
        owner.Dispose();
        Assert.Equal(PathPlanState.Cancelled, owner.State);
        Assert.Null(owner.Result);
        youngest.Advance(new PathWorkBudget(1));
        Assert.Equal(0, youngest.WorkSpent);
        Finish(oldest, int.MaxValue);
        Finish(youngest, int.MaxValue);
    }

    static (IIncrementalRegionPathPlanner Planner, NavGoalRegion Goal) DisconnectedCorridor()
    {
        var space = NavSpace.Single(Grid(18, 2, (x, z) => (z == 0 && x < 16) || x == 17));
        return (Incremental(new GridPathPlanner(space)),
            new NavGoalRegion(new Vector3(17.5f, 0f, 0.5f), 1f, feet => feet.X == 17.5f));
    }

    internal static GridPathPlanner.GridSearchRequest NewRequest(IIncrementalRegionPathPlanner planner,
        PathPlanWorkspaces pool, NavGoalRegion goal)
        => Assert.IsType<GridPathPlanner.GridSearchRequest>(planner.BeginPath(Start, goal, 0f, PathQueryBudget.Default, pool));
}
