using System;
using System.Numerics;
using KhaozEngine.Movement;
using Xunit;
using static KhaozEngine.Movement.NavSegmentOperation;

namespace KhaozEngine.Tests.Movement;

public sealed class NavSegmentRequestTraceTests
{
    [Fact]
    public void EveryCutInEndpointRowsColumnsAndDirectedCrossings()
    {
        var navigation = SegmentAdmissionFixture.Flat(7, 5, 0.5f);
        Vector3 from = new(1.5f, 0f, 2.5f), to = new(3.5f, 0f, 2.5f);
        // Each endpoint scans its own 2 by 2 box. The sweep visits two rows of four candidates.
        (NavSegmentOperation, int)[] trace =
        [
            (EndpointStart, 1), (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3),
            (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3), (EndpointLayer, 2),
            (EndpointStart, 1), (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3),
            (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3), (EndpointLayer, 2),
            (SegmentStart, 1), (FootprintRow, 2), (FootprintColumn, 3), (FootprintColumn, 3),
            (FootprintColumn, 3), (FootprintColumn, 3), (FootprintRow, 2), (FootprintColumn, 3),
            (FootprintColumn, 3), (FootprintColumn, 3), (FootprintColumn, 3), (CellCrossing, 3), (CellCrossing, 3),
        ];
        AssertEveryCut(navigation, from, to, trace, accepted: true, cells: 16, total: 73);
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

    [Fact]
    public void RejectedFromEndpointNeverStartsToEndpoint()
    {
        var navigation = SegmentAdmissionFixture.Flat(5, 5, 0f);
        Vector3 from = new(float.NaN, 0f, 1.5f), to = new(2.5f, 0f, 1.5f);
        AssertEveryCut(navigation, from, to, [(EndpointStart, 1)], accepted: false, cells: 0, total: 1);
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
