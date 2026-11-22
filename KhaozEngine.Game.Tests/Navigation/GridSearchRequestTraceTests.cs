using System;
using System.Collections.Generic;
using System.Linq;
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
    [Fact]
    public void DisconnectedCertificateHasALiteralTrace()
    {
        var space = NavSpace.Single(Grid(5, 1, (x, _) => x != 2));
        var visits = new List<float>();
        var goal = new NavGoalRegion(new(3.5f, 0f, 0.5f), 1f, feet =>
        {
            visits.Add(feet.X);
            return true;
        });
        var query = new RegionQuery("certificate", new GridPathPlanner(space), new(1.5f, 0f, 0.5f),
            goal, Budget(8), NavPathStatus.Unreachable);
        AssertTrace(query, NavPath.Unreachable,
        [
            (Phase.Snap, 2), (Phase.MemberBox, 2),
            (Phase.Member, 2), (Phase.Member, 2), (Phase.Member, 2), (Phase.Member, 2),
            (Phase.CertificateLabel, 1), (Phase.CertificateLabel, 1),
            (Phase.CertificateProgress, 1), (Phase.CertificateProgress, 1), // Start against both members.
            (Phase.CertificateProgress, 1), (Phase.CertificateProgress, 1), // Other component node.
        ], memberVisits: (visits, [(5, 3.5f), (6, 4.5f)]));
    }

    [Fact]
    public void ParallelLinksHaveALiteralTrace()
    {
        AssertTrace(EqualPriceLinks(), VerticalLanding(NavWaypointKind.Hop),
        [
            (Phase.Layer, 2), (Phase.Layer, 2), (Phase.Snap, 2),
            (Phase.MemberBox, 2), (Phase.MemberBox, 2), (Phase.Member, 2), (Phase.Member, 2),
            (Phase.CertificateLabel, 1), (Phase.Seed, 4), (Phase.SearchProgress, 1),
            (Phase.Pop, 4), (Phase.SearchProgress, 1),
            (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6),
            (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6),
            (Phase.Link, 6), (Phase.Link, 6),
            (Phase.Pop, 4), (Phase.SearchProgress, 1),
            (Phase.Parent, 1), (Phase.Parent, 1), (Phase.Reverse, 1),
            (Phase.Decode, 2), (Phase.Decode, 2), (Phase.Allocate, 2), (Phase.Copy, 1), (Phase.Copy, 1),
        ]);
    }

    [Fact]
    public void GuardedOwnCellSnapHasALiteralTrace()
    {
        var space = NavSpace.Single(Grid(1, 1, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0f, 1f,
            [new NavTraversalLayer(1, 1, [true], [0])], []);
        var query = new RegionQuery("guarded-own-cell", new GridPathPlanner(space, graph), Start,
            End(1), Budget(1) with { SnapRadius = -2f }, NavPathStatus.Complete);
        AssertTrace(query, new NavPath(NavPathStatus.Complete, [new NavWaypoint(new(0.5f, 0.5f), 0)]),
        [
            (Phase.Snap, 2), (Phase.MemberBox, 2), (Phase.Member, 2), (Phase.CertificateLabel, 1),
            (Phase.Seed, 4), (Phase.SearchProgress, 1), (Phase.Pop, 4), (Phase.SearchProgress, 1),
            (Phase.Parent, 1), (Phase.Decode, 2), (Phase.Allocate, 2), (Phase.Copy, 1),
        ]);
    }

    [Fact]
    public void StaleQueueEntryHasALiteralTrace()
    {
        AssertTrace(StaleEntry(), StalePartial(),
        [
            (Phase.Snap, 2), (Phase.MemberBox, 2), (Phase.Member, 2), (Phase.Member, 2),
            (Phase.CertificateLabel, 1), (Phase.CertificateProgress, 1), (Phase.CertificateProgress, 1),
            (Phase.Seed, 4), (Phase.SearchProgress, 1),
            (Phase.Pop, 4), (Phase.SearchProgress, 1),
            (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6),
            (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6),
            (Phase.Link, 6), (Phase.Link, 6),
            (Phase.Pop, 4), (Phase.SearchProgress, 1),
            (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6),
            (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6),
            (Phase.Pop, 4), // Stale: no progress or edges.
            (Phase.Parent, 1), (Phase.Parent, 1), (Phase.Reverse, 1),
            (Phase.Decode, 2), (Phase.Decode, 2), (Phase.Allocate, 2), (Phase.Copy, 1), (Phase.Copy, 1),
        ]);
    }

    [Fact]
    public void ExpansionCapHasALiteralTrace()
    {
        var query = new RegionQuery("expansion-cap", Corridor(3), Start, End(3), Budget(2), NavPathStatus.Partial);
        AssertTrace(query, new NavPath(NavPathStatus.Partial, [new NavWaypoint(new(1.5f, 0.5f), 0)]),
        [
            (Phase.Snap, 2), (Phase.MemberBox, 2), (Phase.Member, 2), (Phase.Member, 2),
            (Phase.CertificateLabel, 1), (Phase.Seed, 4), (Phase.SearchProgress, 1),
            (Phase.Pop, 4), (Phase.SearchProgress, 1),
            (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6),
            (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6),
            (Phase.Pop, 4), (Phase.SearchProgress, 1),
            (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6),
            (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6), (Phase.Neighbor, 6),
            (Phase.Parent, 1), (Phase.Parent, 1), (Phase.Reverse, 1),
            (Phase.Decode, 2), (Phase.Allocate, 2), (Phase.Copy, 1),
        ]);
    }

    static readonly int[] LiteralTraceQuanta = [1, 2, 3, 7, 64, 5, 6, int.MaxValue];

    static void AssertTrace(RegionQuery query, NavPath expected, (Phase Kind, int Weight)[] operations,
        (List<float> Observed, (int Cut, float X)[] Expected)? memberVisits = null)
    {
        var planner = Incremental(query.Planner);
        using var pool = planner.CreateWorkspaces(1, query.Budget);
        EqualBits(expected, planner.FindPath(query.Start, query.Goal, 0f, query.Budget));
        AssertVisits(operations.Length);
        int total = operations.Sum(operation => operation.Weight);

        // Every adjacent pair of boundaries is the cut immediately before and after one operation.
        using (var request = Begin())
        {
            int spent = 0;
            AssertCut(request, 0, spent);
            for (int i = 0; i < operations.Length; i++)
            {
                int spare = i + 1 < operations.Length ? operations[i + 1].Weight - 1 : 0;
                request.Advance(new PathWorkBudget(operations[i].Weight + spare));
                spent += operations[i].Weight;
                AssertCut(request, i + 1, spent);
            }
            Assert.Equal(total, request.WorkSpent);
        }

        foreach (int quantum in LiteralTraceQuanta)
        {
            DriveQuantum(quantum, null);
            var work = new PathWorkByKind();
            DriveQuantum(quantum, work);
            Assert.Equal(total, work.Total);
            foreach (var kind in operations.GroupBy(operation => operation.Kind))
            {
                long units = work[(PathWorkKind)kind.Key];
                Assert.Equal(kind.Sum(operation => operation.Weight), units);
                long count = Math.DivRem(units, PathWorkWeights.For(kind.Key), out long remainder);
                Assert.Equal(0, remainder);
                Assert.Equal(kind.Count(), count);
            }
        }

        void DriveQuantum(int quantum, PathWorkByKind? work)
        {
            using var request = Begin();
            int spent = 0;
            int cut = 0;
            AssertCut(request, cut, spent);
            while (cut < operations.Length)
            {
                // The first operation always fits. Remaining operations must fit the leftover quota.
                int slice = operations[cut++].Weight;
                while (cut < operations.Length && operations[cut].Weight <= quantum - slice)
                    slice += operations[cut++].Weight;
                spent += slice;
                var budget = new PathWorkBudget(quantum);
                if (work is null) request.Advance(budget);
                else request.AdvanceForTests(budget, work);
                AssertCut(request, cut, spent);
            }
            Assert.Equal(total, request.WorkSpent);
        }

        GridPathPlanner.GridSearchRequest Begin()
        {
            memberVisits?.Observed.Clear();
            return Assert.IsType<GridPathPlanner.GridSearchRequest>(planner.BeginPath(query.Start, query.Goal, 0f, query.Budget, pool));
        }

        void AssertVisits(int cut)
        {
            if (memberVisits is { } visits)
                Assert.Equal(visits.Expected.Where(visit => visit.Cut <= cut).Select(visit => visit.X), visits.Observed);
        }

        void AssertCut(GridPathPlanner.GridSearchRequest request, int cut, int spent)
        {
            Assert.Equal(spent, request.WorkSpent);
            AssertVisits(cut);
            if (cut < operations.Length)
            {
                Assert.Equal(operations[cut].Kind, request.NextPhaseForTests);
                Assert.Equal(PathPlanState.Pending, request.State);
                Assert.Null(request.Result);
            }
            else
            {
                Assert.Equal(Phase.Done, request.NextPhaseForTests);
                Assert.Equal(PathPlanState.Ready, request.State);
                EqualBits(expected, Assert.IsType<NavPath>(request.Result));
            }
        }
    }
}
