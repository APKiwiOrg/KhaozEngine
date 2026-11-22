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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhaseBoundaryQuanta(bool useLinks)
    {
        RegionQuery links = EqualPriceLinks();
        var (planner, goal) = useLinks ? (Incremental(links.Planner), links.Goal) : DisconnectedCorridor();
        Vector3 start = useLinks ? links.Start : Start;
        using var pool = planner.CreateWorkspaces(1, PathQueryBudget.Default);
        using var baseline = Begin();
        var trace = new List<(Phase Operation, int After)>();
        for (int call = 0; baseline.State == PathPlanState.Pending; call++)
        {
            Assert.True(call < 1000);
            Phase phase = baseline.NextPhaseForTests;
            int before = checked((int)baseline.WorkSpent);
            baseline.Advance(new PathWorkBudget(1));
            int after = checked((int)baseline.WorkSpent);
            Assert.Equal(PathWorkWeights.For(phase), after - before);
            trace.Add((phase, after));
        }

        Phase[] required = useLinks
            ? new[] { Phase.Layer, Phase.Link, Phase.Decode, Phase.Copy }
            : new[] { Phase.Snap, Phase.MemberBox, Phase.Member, Phase.CertificateLabel, Phase.CertificateProgress,
                Phase.Seed, Phase.Pop, Phase.SearchProgress, Phase.Neighbor, Phase.Parent, Phase.Reverse,
                Phase.Decode, Phase.Allocate, Phase.Copy };
        foreach (Phase phase in required)
            Assert.Contains(trace, step => step.Operation == phase);

        // Pin both sides of the first two operations of each kind and of its last operation.
        // The second cut is within a repeated reduction, link list, decode or copy on these fixtures.
        var seen = new Dictionary<Phase, int>();
        var cuts = new SortedSet<int> { trace.Count };
        for (int index = 0; index < trace.Count; index++)
        {
            Phase operation = trace[index].Operation;
            seen.TryGetValue(operation, out int occurrences);
            seen[operation] = occurrences + 1;
            if (occurrences >= 2 && trace.FindLastIndex(step => step.Operation == operation) != index) continue;
            if (index > 0) cuts.Add(index);
            cuts.Add(index + 1);
        }

        using var request = Begin();
        int callCount = 0;
        int? readyCall = null;
        foreach (int cut in cuts)
        {
            bool last = cut == trace.Count;
            int workSpent = trace[cut - 1].After;
            Phase next = last ? Phase.Done : trace[cut].Operation;
            int quota = checked((int)(workSpent - request.WorkSpent));
            // Spare quota below the next operation's weight must leave it wholly untouched.
            if (!last) quota += PathWorkWeights.For(next) - 1;
            request.Advance(new PathWorkBudget(quota));

            Assert.Equal((workSpent, next), (request.WorkSpent, request.NextPhaseForTests));
            callCount++;
            if (request.State == PathPlanState.Ready)
                readyCall ??= callCount;
            else
            {
                Assert.Equal(PathPlanState.Pending, request.State);
                Assert.Null(request.Result);
            }
        }
        Assert.Equal(cuts.Count, readyCall);
        Assert.Equal(baseline.WorkSpent, request.WorkSpent);
        NavPath path = Assert.IsType<NavPath>(request.Result);
        EqualBits(Assert.IsType<NavPath>(baseline.Result), path);
        EqualBits(planner.FindPath(start, goal, 0f, PathQueryBudget.Default), path);

        GridPathPlanner.GridSearchRequest Begin()
            => Assert.IsType<GridPathPlanner.GridSearchRequest>(
                planner.BeginPath(start, goal, 0f, PathQueryBudget.Default, pool));
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
