using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using Phase = KhaozEngine.Navigation.GridPathPlanner.SearchPhase;

namespace KhaozEngine.Tests.Movement;

public sealed class RouteCalibrationPhaseWorkTests
{
    [Theory]
    [InlineData((int)Phase.Parent, (int)Phase.Reverse, (int)PathWorkKind.Parent, 2049, 2049)]
    [InlineData((int)Phase.Reverse, (int)Phase.Decode, (int)PathWorkKind.Reverse, 1024, 1024)]
    [InlineData((int)Phase.Decode, (int)Phase.Allocate, (int)PathWorkKind.Decode, 4096, 2048)]
    [InlineData((int)Phase.Copy, (int)Phase.Done, (int)PathWorkKind.Copy, 2048, 2048)]
    public void ReconstructionWindowSpendsOnlyItsNamedPhase(int phase, int next, int kind, int units, int operations)
    {
        var fixture = RouteCalibrationFixture.LongReconstruction();
        var slice = RouteCalibrationPhases.Reconstruction.Single(slice => slice.Phase == (Phase)phase);
        using var pool = fixture.CreatePool();
        using var request = fixture.Begin(pool);
        slice.Prepare(request);

        var work = new PathWorkByKind();
        long prior = request.WorkSpent;
        request.AdvanceForTests(slice.Budget, work);

        Assert.Equal((Phase)next, request.NextPhaseForTests);
        Assert.Equal(units, request.WorkSpent - prior);
        Assert.Equal(units, work[(PathWorkKind)kind]);
        Assert.Equal(units, work.Total);
        Assert.Equal(operations, CalibrationWork.Operations(work, (PathWorkKind)kind));
    }

    [Fact]
    public void ProjectedFootprintWindowSpendsThreeUnitsPerPrepositionedRow()
    {
        var rows = RouteCalibrationPhases.ProjectedRows();
        var work = new PathWorkByKind();
        Assert.Equal(129, rows.Length);
        foreach (var prepared in rows)
        {
            var cursor = prepared;
            long prior = cursor.WorkSpent;
            Assert.Equal(NavSegmentOperation.FootprintRow, cursor.NextOperation);
            cursor.AdvanceForTests(1, work);
            Assert.Equal(3, cursor.WorkSpent - prior);
            Assert.Equal(NavSegmentOperation.FootprintColumn, cursor.NextOperation);
        }

        Assert.Equal(387, work[PathWorkKind.FootprintRow]);
        Assert.Equal(387, work.Total);
        Assert.Equal(129, CalibrationWork.Operations(work, PathWorkKind.FootprintRow));
    }

    [Fact]
    public void CrossingWindowsDifferOnlyByOneHundredTwentyEightCrossings()
    {
        var prepared = RouteCalibrationPhases.Crossings();
        var shortWork = Measure(prepared, RouteCalibrationPhases.ShortCrossingBudget);
        var longWork = Measure(prepared, RouteCalibrationPhases.LongCrossingBudget);

        Assert.Equal(768, longWork[PathWorkKind.CellCrossing] - shortWork[PathWorkKind.CellCrossing]);
        Assert.Equal(768, shortWork[PathWorkKind.CellCrossing]);
        Assert.Equal(1536, longWork[PathWorkKind.CellCrossing]);
        Assert.Equal(768, shortWork.Total);
        Assert.Equal(1536, longWork.Total);
        Assert.Equal(128, CalibrationWork.Operations(shortWork, PathWorkKind.CellCrossing));
        Assert.Equal(256, CalibrationWork.Operations(longWork, PathWorkKind.CellCrossing));

        static PathWorkByKind Measure(NavSegmentRequest cursor, int quota)
        {
            Assert.Equal(NavSegmentOperation.CellCrossing, cursor.NextOperation);
            var work = new PathWorkByKind();
            long prior = cursor.WorkSpent;
            cursor.AdvanceForTests(quota, work);
            Assert.Equal(NavSegmentOperation.CellCrossing, cursor.NextOperation);
            Assert.Null(cursor.Result);
            Assert.Equal(cursor.WorkSpent - prior, work.Total);
            return work;
        }
    }
}
