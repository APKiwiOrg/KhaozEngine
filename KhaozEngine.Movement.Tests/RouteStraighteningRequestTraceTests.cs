using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;
using Search = KhaozEngine.Navigation.GridPathPlanner.SearchPhase;
using Segment = KhaozEngine.Movement.NavSegmentOperation;
using Straight = KhaozEngine.Movement.RouteStraighteningOperation;

namespace KhaozEngine.Tests.Movement;

public sealed partial class RouteStraighteningRequestTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InnerResultPassesThroughAtTheLastPaidOperation(bool member)
    {
        GroundNavigation navigation = Flat(1, 1, 0f);
        var straightener = new RouteStraightener(navigation);
        IIncrementalRegionPathPlanner planner = straightener;
        var start = new Vector3(0.5f, 0f, 0.5f);
        var goal = new NavGoalRegion(start, 0f, _ => member);
        NavPath expected = member ? Path(NavPathStatus.Complete, new NavWaypoint(new(0.5f, 0.5f), 0)) : NavPath.Unreachable;
        AssertBits(expected, planner.FindPath(start, goal, 0f, Budget));

        (Segment Start, Search Search, int Units)[] prefix =
        [
            (Segment.EndpointStart, Search.Done, 1), (Segment.FootprintRow, Search.Done, 2),
            (Segment.FootprintColumn, Search.Done, 3), (Segment.EndpointLayer, Search.Done, 2),
            (Segment.Done, Search.Snap, 2), (Segment.Done, Search.MemberBox, 2), (Segment.Done, Search.Member, 2),
        ];
        (Segment Start, Search Search, int Units)[] search = member
            ?
            [
                (Segment.Done, Search.CertificateLabel, 1), (Segment.Done, Search.Seed, 4),
                (Segment.Done, Search.SearchProgress, 1), (Segment.Done, Search.Pop, 4),
                (Segment.Done, Search.SearchProgress, 1), (Segment.Done, Search.Parent, 1),
                (Segment.Done, Search.Decode, 2), (Segment.Done, Search.Allocate, 2), (Segment.Done, Search.Copy, 1),
            ]
            : [];
        var trace = prefix.Concat(search).ToArray();
        using var pool = planner.CreateWorkspaces(1, Budget);
        int spent = 0;
        for (int cut = 0; cut < trace.Length; cut++)
        {
            using var request = Assert.IsType<RouteStraighteningRequest>(planner.BeginPath(start, goal, 0f, Budget, pool));
            var work = new PathWorkByKind();
            if (spent != 0) request.AdvanceForTests(new(spent), work);
            Assert.Equal(spent, request.WorkSpent);
            Assert.Equal(trace[cut].Start, request.NextStartOperationForTests);
            if (trace[cut].Start == Segment.Done) Assert.Equal(trace[cut].Search, request.NextPhaseForTests);
            Assert.Null(request.Result);
            bool last = cut + 1 == trace.Length;
            request.AdvanceForTests(new(1), work);
            spent += trace[cut].Units;
            Assert.Equal(request.WorkSpent, work.Total);
            Assert.Equal(spent, request.WorkSpent);
            if (last)
            {
                Assert.Equal(PathPlanState.Ready, request.State);
                AssertBits(expected, Assert.IsType<NavPath>(request.Result));
                Assert.Null(straightener.LastStraightened);
            }
            else
            {
                Assert.Equal(PathPlanState.Pending, request.State);
                Assert.Null(request.Result);
                Assert.Equal(trace[cut + 1].Start, request.NextStartOperationForTests);
                if (trace[cut + 1].Start == Segment.Done) Assert.Equal(trace[cut + 1].Search, request.NextPhaseForTests);
            }
        }
        Assert.Equal(member ? 31 : 14, spent);
    }

    [Fact]
    public void LongSweepVisitsEachEndpointRowColumnAndCrossingBeforeTheNextGuard()
    {
        GroundNavigation navigation = Flat(13, 7, 0.5f);
        NavPath raw = Path(NavPathStatus.Complete, Waypoint(2.5f), Waypoint(9.5f));
        NavPath expected = Path(NavPathStatus.Complete, Waypoint(9.5f));
        Paid[] guard = [.. Endpoint(2, 2), .. Endpoint(2, 2), Cell(Segment.SegmentStart, 1),
            .. Row(10), .. Row(10), .. Enumerable.Repeat(Cell(Segment.CellCrossing, 3), 8)];
        Paid[] trace =
        [
            Step(Straight.ValidateFeet, 2), Step(Straight.ValidateFeet, 2), Step(Straight.Candidate, 3),
            .. guard, Step(Straight.SideOffsets, 2), .. guard, .. guard,
            Step(Straight.KeepAnchor, 2), Step(Straight.Allocate, 2), Step(Straight.Copy, 1),
        ];
        Vector3 end = Start with { X = 9.5f };
        AssertStraighteningCuts(navigation, raw, expected, trace, total: 395, cells: 84,
            expectedGuards: [(Start, end), (Start with { Z = 2.999f }, end with { Z = 2.999f }),
                (Start with { Z = 2.001f }, end with { Z = 2.001f })]);
    }

    [Fact]
    public void PlusSideRefusalSkipsTheMinusGuardAndKeepsTheNextRawEdge()
    {
        GroundNavigation marked = MarkedFloor().ExcludingAreas(1u);
        NavPath raw = Path(NavPathStatus.Complete, Waypoint(2.5f), Waypoint(6.5f));
        Paid[] trace =
        [
            Step(Straight.ValidateFeet, 2), Step(Straight.ValidateFeet, 2), Step(Straight.Candidate, 3),
            .. Endpoint(1), .. Endpoint(1), Cell(Segment.SegmentStart, 1), .. Row(6),
            .. Enumerable.Repeat(Cell(Segment.CellCrossing, 3), 5),
            Step(Straight.SideOffsets, 2),
            .. Endpoint(1, 1), .. Endpoint(1, 1), Cell(Segment.SegmentStart, 1), .. Row(6), .. Row(3),
            Step(Straight.KeepAnchor, 2), Step(Straight.KeepAnchor, 2),
        ];
        Vector3 end = Start with { X = 6.5f };
        AssertStraighteningCuts(marked, raw, raw, trace, total: 123, cells: 21,
            expectedGuards: [(Start, end), (Start with { Z = 2.999f }, end with { Z = 2.999f })]);
    }

    [Fact]
    public void MinusSideRefusalKeepsTheNextRawEdgeAfterTheOtherGuardsPass()
    {
        GroundNavigation marked = Flat(9, 6, 0.25f, (x, z) => x == 3 && z == 1 ? 1u : 0u).ExcludingAreas(1u);
        NavPath raw = Path(NavPathStatus.Complete, Waypoint(2.5f), Waypoint(6.5f));
        Paid[] trace =
        [
            Step(Straight.ValidateFeet, 2), Step(Straight.ValidateFeet, 2), Step(Straight.Candidate, 3),
            .. Endpoint(1), .. Endpoint(1), Cell(Segment.SegmentStart, 1), .. Row(6),
            .. Enumerable.Repeat(Cell(Segment.CellCrossing, 3), 5),
            Step(Straight.SideOffsets, 2),
            .. Endpoint(1, 1), .. Endpoint(1, 1), Cell(Segment.SegmentStart, 1), .. Row(6), .. Row(6),
            .. Enumerable.Repeat(Cell(Segment.CellCrossing, 3), 5),
            .. Endpoint(1, 1), .. Endpoint(1, 1), Cell(Segment.SegmentStart, 1), .. Row(3),
            Step(Straight.KeepAnchor, 2), Step(Straight.KeepAnchor, 2),
        ];
        Vector3 end = Start with { X = 6.5f };
        AssertStraighteningCuts(marked, raw, raw, trace, total: 185, cells: 31,
            expectedGuards: [(Start, end), (Start with { Z = 2.999f }, end with { Z = 2.999f }),
                (Start with { Z = 2.001f }, end with { Z = 2.001f })]);
    }

    [Fact]
    public void CentreRefusalKeepsTheNextRawEdgeWithoutComputingSideOffsets()
    {
        GroundNavigation marked = Flat(9, 6, 0.25f, (x, z) => x == 3 && z == 2 ? 1u : 0u).ExcludingAreas(1u);
        NavPath raw = Path(NavPathStatus.Complete, Waypoint(2.5f), Waypoint(6.5f));
        Paid[] trace =
        [
            Step(Straight.ValidateFeet, 2), Step(Straight.ValidateFeet, 2), Step(Straight.Candidate, 3),
            .. Endpoint(1), .. Endpoint(1), Cell(Segment.SegmentStart, 1), .. Row(3),
            Step(Straight.KeepAnchor, 2), Step(Straight.KeepAnchor, 2),
        ];
        AssertStraighteningCuts(marked, raw, raw, trace, total: 39, cells: 5,
            expectedGuards: [(Start, new(6.5f, 0f, 2.5f))]);
    }

    [Theory]
    [InlineData(NavWaypointKind.Hop, 0)]
    [InlineData(NavWaypointKind.Walk, 1)]
    public void CandidateTransitionKeepsItsPredecessorBeforeTestingAnyGuard(NavWaypointKind kind, int layer)
    {
        var tuning = MoveTuning.Default with { CapsuleRadius = 0f };
        var columns = NavAreaFootprintRowsTests.Columns(NavAreaFootprintRowsTests.Options());
        var footprint = new NavAreaFootprint(columns, columns.Options, tuning, default, surfaceReach: 2d);
        GroundNavigation navigation = Build(footprint, tuning, 4f,
            [new NavLink(0, 2, 2, layer, 4, 2) { Kind = kind == NavWaypointKind.Hop ? NavLinkKind.Hop : NavLinkKind.Stair }]);
        NavPath raw = Path(NavPathStatus.Complete, Waypoint(2.5f), Waypoint(4.5f, layer, kind));
        AssertStraighteningCuts(navigation, raw, raw,
            [
                Step(Straight.ValidateFeet, 2), Step(Straight.ValidateFeet, 2), Step(Straight.Candidate, 3),
                Step(Straight.KeepAnchor, 2), Step(Straight.KeepAnchor, 2),
            ], total: 11, cells: 0, expectedGuards: []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MandatoryLandingCannotBeSkippedToARegularCandidate(bool hop)
    {
        GroundNavigation navigation = Layered(hop ? NavLinkKind.Hop : NavLinkKind.Stair);
        NavPath raw = Path(NavPathStatus.Complete,
            Waypoint(2.5f, hop ? 0 : 1, hop ? NavWaypointKind.Hop : NavWaypointKind.Walk), Waypoint(3.5f));
        AssertStraighteningCuts(navigation, raw, raw,
            [
                Step(Straight.ValidateFeet, 2), Step(Straight.ValidateFeet, 2), Step(Straight.Candidate, 3),
                Step(Straight.KeepAnchor, 2), Step(Straight.KeepAnchor, 2),
            ], total: 11, cells: 0, expectedGuards: []);
    }

    [Fact]
    public void MalformedRouteIsReturnedIntactBeforeAnyGuard()
    {
        GroundNavigation navigation = Flat(9, 6, 0.25f);
        NavPath raw = Path(NavPathStatus.Partial, Waypoint(2.5f), Waypoint(3.5f), Waypoint(4.5f, layer: 3));
        AssertStraighteningCuts(navigation, raw, raw,
            [Step(Straight.ValidateFeet, 2), Step(Straight.ValidateFeet, 2), Step(Straight.ValidateFeet, 2)],
            total: 6, cells: 0, expectedGuards: []);
    }

    [Fact]
    public void RouteKeepsItsLeaseThroughThePaidAllocationAndCopy()
    {
        GroundNavigation navigation = Flat(5, 5, 0f);
        var straightener = new RouteStraightener(navigation);
        IIncrementalRegionPathPlanner planner = straightener;
        NavGoalRegion goal = Goal(Start with { X = 3.5f });
        using var pool = planner.CreateWorkspaces(1, Budget);
        using var owner = Assert.IsType<RouteStraighteningRequest>(planner.BeginPath(Start, goal, 0f, Budget, pool));
        using var waiter = planner.BeginPath(Start, goal, 0f, Budget, pool);
        for (int cut = 0; owner.NextOperationForTests != Straight.Allocate; cut++)
        {
            Assert.True(cut < 300);
            Assert.False(owner.HasStagedStraightenedArrayForTests);
            Assert.Null(owner.Result);
            owner.Advance(new(1));
        }
        Assert.False(owner.HasStagedStraightenedArrayForTests);
        Assert.Null(straightener.LastStraightened);
        waiter.Advance(new(1));
        Assert.Equal(0, waiter.WorkSpent);

        long before = owner.WorkSpent;
        owner.Advance(new(1));
        Assert.Equal(before + 2, owner.WorkSpent);
        Assert.Equal(Straight.Copy, owner.NextOperationForTests);
        Assert.True(owner.HasStagedStraightenedArrayForTests);
        Assert.Null(owner.Result);
        waiter.Advance(new(1));
        Assert.Equal(0, waiter.WorkSpent);

        owner.Advance(new(1));
        Assert.Equal(before + 3, owner.WorkSpent);
        AssertBits(Path(NavPathStatus.Complete, Waypoint(3.5f)), Assert.IsType<NavPath>(owner.Result));
        Assert.Same(owner.Result, straightener.LastStraightened);
        waiter.Advance(new(1));
        Assert.Equal(1, waiter.WorkSpent);
    }

    static void AssertStraighteningCuts(GroundNavigation navigation, NavPath raw, NavPath expected,
        Paid[] trace, int total, long cells, (Vector3 From, Vector3 To)[]? expectedGuards = null)
    {
        var observed = new List<(Vector3, Vector3)>();
        var straightener = new RouteStraightener(new FixedPath(raw), navigation.Space, (from, to) =>
        {
            observed.Add((from, to));
            return navigation.AllowsSegment(from, to);
        });
        using (var diagnostic = new NavFootprintDiagnostics())
        {
            NavPath actual = straightener.FindPath(Start, Goal(Start), navigation.AgentRadius, Budget);
            AssertBits(expected, actual);
            if (ReferenceEquals(raw, expected)) Assert.Same(raw, actual);
            if (expectedGuards is not null) Assert.Equal(expectedGuards, observed);
            Assert.Equal(cells, diagnostic.CandidateCells);
        }

        var slicedStraightener = new RouteStraightener(navigation);
        List<NavWaypoint> scratch = new(raw.Waypoints.Count);
        int prefix = 0;
        var slicedGuards = new List<(Vector3 From, Vector3 To)>();
        foreach (var (operation, segment, units) in trace)
        {
            using var diagnostic = new NavFootprintDiagnostics();
            var work = new PathWorkByKind();
            var cursor = new RouteStraighteningCursor(slicedStraightener, Start, 0, raw, fallBack: false, scratch);
            int spent = 0;
            if (prefix != 0) cursor.RunSliceForTests(prefix, ref spent, work);
            Assert.Equal(prefix, spent);
            Assert.Equal(operation, cursor.NextOperation);
            if (operation == Straight.Segment)
            {
                Assert.Equal(segment, cursor.NextSegmentOperation);
                if (segment == Segment.EndpointStart && cursor.CurrentGuardForTests is { } guard &&
                    (slicedGuards.Count == 0 || slicedGuards[^1] != guard))
                    slicedGuards.Add(guard);
            }
            Assert.Null(cursor.Result);

            spent = 0;
            cursor.RunSliceForTests(1, ref spent, work);
            prefix += units;
            Assert.Equal(units, spent);
            bool last = prefix == total;
            Assert.Equal(last, cursor.Result is not null);
            if (!last)
            {
                int cumulative = 0;
                Paid next = trace.First(step => (cumulative += step.Units) > prefix);
                Assert.Equal(next.Operation, cursor.NextOperation);
                if (next.Operation == Straight.Segment) Assert.Equal(next.Segment, cursor.NextSegmentOperation);
                cursor.RunSliceForTests(int.MaxValue, ref spent, work);
            }
            Assert.Equal(total - prefix + units, spent);
            Assert.Equal(total, work.Total);
            AssertBits(expected, Assert.IsType<NavPath>(cursor.Result));
            if (ReferenceEquals(raw, expected)) Assert.Same(raw, cursor.Result);
            Assert.Equal(cells, diagnostic.CandidateCells);
        }
        Assert.Equal(total, prefix);
        if (expectedGuards is not null) Assert.Equal(expectedGuards, slicedGuards);
    }

    readonly record struct Paid(Straight Operation, Segment Segment, int Units);

    static Paid Step(Straight operation, int units) => new(operation, Segment.Done, units);

    static Paid Cell(Segment operation, int units) => new(Straight.Segment, operation, units);

    static Paid[] Row(int columns)
        => [Cell(Segment.FootprintRow, 2), .. Enumerable.Repeat(Cell(Segment.FootprintColumn, 3), columns)];

    static Paid[] Endpoint(params int[] rows)
        => [Cell(Segment.EndpointStart, 1), .. rows.SelectMany(Row), Cell(Segment.EndpointLayer, 2)];
}
