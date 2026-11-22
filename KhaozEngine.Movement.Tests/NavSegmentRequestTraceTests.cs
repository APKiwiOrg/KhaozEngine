using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Movement.NavSegmentOperation;

namespace KhaozEngine.Tests.Movement;

public sealed class NavSegmentRequestTraceTests
{
    [Fact]
    public void EveryCutInEndpointRowsColumnsAndDirectedCrossings()
    {
        var navigation = SegmentAdmissionFixture.Flat(13, 5, 0.5f);
        Vector3 from = new(1.5f, 0f, 2.5f), to = new(9.5f, 0f, 2.5f);
        // Each endpoint scans its own 2 by 2 box. The sweep visits two rows of ten candidates.
        (NavSegmentOperation, int)[] trace =
        [
            (EndpointStart, 1), (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3),
            (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3), (EndpointLayer, 2),
            (EndpointStart, 1), (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3),
            (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3), (EndpointLayer, 2),
            (SegmentStart, 1),
            (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3), (FootprintColumn, 3),
            (FootprintColumn, 3), (FootprintColumn, 3), (FootprintColumn, 3), (FootprintColumn, 3),
            (FootprintColumn, 3), (FootprintColumn, 3), (FootprintColumn, 3),
            (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3), (FootprintColumn, 3),
            (FootprintColumn, 3), (FootprintColumn, 3), (FootprintColumn, 3), (FootprintColumn, 3),
            (FootprintColumn, 3), (FootprintColumn, 3), (FootprintColumn, 3),
            (CellCrossing, 3), (CellCrossing, 3), (CellCrossing, 3), (CellCrossing, 3),
            (CellCrossing, 3), (CellCrossing, 3), (CellCrossing, 3), (CellCrossing, 3),
        ];
        AssertEveryCut(navigation, from, to, trace, accepted: true, cells: 28, total: 127);
    }

    [Fact]
    public void FirstRefusalStopsBeforeLaterColumnsAndRows()
    {
        var navigation = SegmentAdmissionFixture.Flat(7, 5, 0.5f, (x, z) => x == 2 && z == 2 ? 1u : 0u)
            .ExcludingAreas(1u);
        Vector3 from = new(1.5f, 0f, 2.5f), to = new(4.5f, 0f, 2.5f);
        // The first endpoint touches the excluded cell on its right edge. It never probes the second row or layer.
        (NavSegmentOperation, int)[] trace =
            [(EndpointStart, 1), (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3)];
        AssertEveryCut(navigation, from, to, trace, accepted: false, cells: 2, total: 9);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolvesFromBeforeAnEndpointThatRefusesAtSetup(bool invalidFrom)
    {
        var navigation = SegmentAdmissionFixture.Flat(5, 5, 0f);
        Vector3 invalid = new(float.NaN, 0f, 1.5f), valid = new(2.5f, 0f, 1.5f);
        if (invalidFrom)
            AssertEveryCut(navigation, invalid, valid, [(EndpointStart, 1)], accepted: false, cells: 0, total: 1);
        else
            AssertEveryCut(navigation, valid, invalid, [.. PointEndpoint, (EndpointStart, 1)], accepted: false, cells: 1, total: 9);
    }

    [Theory]
    [InlineData(2, 11, 50)]
    [InlineData(3, 17, 70)]
    public void SweepVisitsLowerRowFirstAndStopsAtTheMarkedColumn(int markedRow, int cells, int total)
    {
        var navigation = SegmentAdmissionFixture.Flat(8, 6, 0.5f, (x, z) => x == 3 && z == markedRow ? 1u : 0u)
            .ExcludingAreas(1u);
        Vector3 from = new(1.5f, 0f, 2.5f), to = new(5.5f, 0f, 2.5f);
        // Each endpoint has four candidates. The sweep's Z=2 row visits X=1 through 6 unless X=3
        // is marked. With the mark in Z=3, all six Z=2 candidates pass before that row starts.
        (NavSegmentOperation, int)[] halfCellEndpoint = [.. ScanRow(2), .. ScanRow(2), (EndpointLayer, 2)];
        (NavSegmentOperation, int)[] trace =
        [
            (EndpointStart, 1), .. halfCellEndpoint, (EndpointStart, 1), .. halfCellEndpoint,
            (SegmentStart, 1), .. (markedRow == 3 ? ScanRow(6) : []), .. ScanRow(3),
        ];
        AssertEveryCut(navigation, from, to, trace, accepted: false, cells, total);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EqualHeightDistancesKeepTheFirstLayerForEitherEndpoint(bool tieAtFrom)
    {
        var tuning = MoveTuning.Default with { CapsuleRadius = 0f };
        var columns = SegmentAdmissionFixture.Flat(5, 5, 0f).Footprint.Columns;
        var footprint = new NavAreaFootprint(columns, columns.Options, tuning, default, surfaceReach: 1d);
        // Only the lower endpoint of the tie has the directed Stair. A winning higher layer
        // instead sees no ordinary exit. Both endpoint positions are exactly 0.25 m from the two layers.
        NavLink stair = tieAtFrom ? new(0, 1, 2, 1, 2, 2) : new(1, 1, 2, 0, 2, 2);
        var navigation = SegmentAdmissionFixture.Build(footprint, tuning, 0.5f, [stair],
            allowsEdge: (_, _, _, _) => false);
        Vector3 from = new(1.5f, tieAtFrom ? 0.25f : 0.5f, 2.5f);
        Vector3 to = new(2.5f, tieAtFrom ? 0.5f : 0.25f, 2.5f);
        (NavSegmentOperation, int)[] endpoint = [.. PointEndpoint, (EndpointLayer, 2)];
        AssertEveryCut(navigation, from, to,
            [.. endpoint, .. endpoint, (SegmentStart, 1), .. ScanRow(2), (CellCrossing, 3)],
            accepted: true, cells: 4, total: 32);
    }

    [Fact]
    public void NonTiedCrossingUsesTheEarlierBoundaryThenStopsOnTheRefusedEdge()
    {
        // X first at 1/6, Z second at 1/4. The first exit is present and the second is absent.
        var navigation = SegmentAdmissionFixture.Flat(6, 6, 0f,
            allowsEdge: (x, z, nx, nz) => (x, z, nx, nz) == (1, 1, 2, 1));
        Vector3 from = new(1.5f, 0f, 1.5f), to = new(4.5f, 0f, 3.5f);
        AssertEveryCut(navigation, from, to,
            [
                .. PointEndpoint, .. PointEndpoint, (SegmentStart, 1), .. ScanRow(4), .. ScanRow(4), .. ScanRow(4),
                (CellCrossing, 3), (CellCrossing, 3),
            ], accepted: false, cells: 14, total: 65);
    }

    [Fact]
    public void CaptureMaximumTangencyRefusesBeforeAnyRow()
    {
        var navigation = SegmentAdmissionFixture.Flat(5, 5, 0.5f);
        Vector3 from = new(4.5f, 0f, 2.5f), to = new(2.5f, 0f, 2.5f);
        AssertEveryCut(navigation, from, to, [(EndpointStart, 1)], accepted: false, cells: 0, total: 1);
    }

    [Fact]
    public void StairFollowsBothLayerProbesAndTheWholeSweep()
    {
        var tuning = MoveTuning.Default with { CapsuleRadius = 0f };
        var columns = NavAreaFootprintRowsTests.Columns(NavAreaFootprintRowsTests.Options());
        var footprint = new NavAreaFootprint(columns, columns.Options, tuning, default, surfaceReach: 2d);
        var navigation = SegmentAdmissionFixture.Build(footprint, tuning, 4f, [new NavLink(0, 2, 2, 1, 3, 2)]);
        Vector3 from = new(2.5f, 0f, 2.5f), to = new(3.5f, 4f, 2.5f);
        (NavSegmentOperation, int)[] trace =
        [
            (EndpointStart, 1), (FootprintRow, 2), (FootprintColumn, 3), (EndpointLayer, 2), (EndpointLayer, 2),
            (EndpointStart, 1), (FootprintRow, 2), (FootprintColumn, 3), (EndpointLayer, 2), (EndpointLayer, 2),
            (SegmentStart, 1), (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3), (CellCrossing, 3),
        ];
        AssertEveryCut(navigation, from, to, trace, accepted: true, cells: 4, total: 32);
    }

    static readonly (NavSegmentOperation, int)[] PointEndpoint =
        [(EndpointStart, 1), (FootprintRow, 2), (FootprintColumn, 3), (EndpointLayer, 2)];

    static (NavSegmentOperation, int)[] ScanRow(int columns)
    {
        var row = new (NavSegmentOperation, int)[columns + 1];
        row[0] = (FootprintRow, 2);
        Array.Fill(row, (FootprintColumn, 3), 1, columns);
        return row;
    }

    static void AssertEveryCut(GroundNavigation navigation, Vector3 from, Vector3 to,
        (NavSegmentOperation Op, int Weight)[] trace, bool accepted, long cells, int total)
    {
        using (var synchronous = new NavFootprintDiagnostics())
        {
            Assert.Equal(accepted, navigation.AllowsSegment(from, to));
            Assert.Equal(cells, synchronous.CandidateCells);
        }

        int prefix = 0;
        for (int cut = 0; cut < trace.Length; cut++)
        {
            var request = new NavSegmentRequest(navigation, from, to);
            using var work = new NavFootprintDiagnostics();
            if (prefix != 0) request.Advance(prefix);
            Assert.Equal(prefix, request.WorkSpent);
            Assert.Equal(trace[cut].Op, request.NextOperation);
            Assert.Null(request.Result);

            request.Advance(1);
            prefix += trace[cut].Weight;
            Assert.Equal(prefix, request.WorkSpent);
            bool last = cut + 1 == trace.Length;
            Assert.Equal(last ? Done : trace[cut + 1].Op, request.NextOperation);
            Assert.Equal(last ? accepted : (bool?)null, request.Result);

            request.Advance(int.MaxValue);
            Assert.Equal(accepted, request.Result);
            Assert.Equal(total, request.WorkSpent);
            Assert.Equal(cells, work.CandidateCells);
        }
        Assert.Equal(total, prefix);
    }
}
