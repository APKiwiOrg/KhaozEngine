using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationWaterHaloTests
{
    // One 16 m rectangle in a 64 m storage owner. Each of the 64 selected 2 m cells
    // sees the same complete rectangle through its 16 m halo.
    static MapScopedScene Scene(NativeSceneFixture fixture, double padding = 16)
    {
        var body = fixture.Document.Water!.Bodies[0];
        fixture.Document.Water.Bodies[0] = body with
        { Domain = body.Domain with { Rects = [new(0, 0, 16, 16)], Rings = [] } };
        return NativeSceneFixtures.Open(fixture, false)
            .Acquire(NativeSceneFixtures.WaterWindow(0, 1, 1) with { MotionPaddingMetres = padding }).Scene!;
    }

    [Fact]
    public void RepeatedOverlappingHalos_ReleaseConsumedContainersBeforeNextCell()
    {
        using var fixture = NativeSceneFixtures.Create("water-closed");
        var scene = Scene(fixture);
        // Independent discovery bound: compiler 512, water clone 328, domain 2560,
        // part/index 512, initial cells 64*128, expanded backing at most 89*128,
        // one nearby list 16, conversion 136 and candidate scratch 448+2*64*128.
        const long fittingPeak = 512 + 328 + 2560 + 512 + 64 * 128 + 89 * 128 + 16 + 136 + 448 + 2 * 64 * 128;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: fittingPeak));
        using var active = admission.Enter();
        // Discovery spends 128 initial checks and 64*(1+128) halo checks. Stop at
        // the first canonical-cell index visit, independently of its compilation.
        using var result = MapWaterScopedCompiler.Compile(scene, limits: new(MaxPairChecks: 8384));
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Equal(nameof(MapWaterCompileLimits.MaxPairChecks), result.Capacity!.LimitName);
        Assert.Equal(8385, result.Capacity.Requested);
        Assert.Equal(8384, result.Capacity.Allowance);
        Assert.Equal(4160, result.CandidateCells);
        Assert.Equal(8385, result.PairChecks);
        Assert.Empty(result.AcquiredCells);
        Assert.InRange(admission.PeakBytes, 1, fittingPeak);
        Assert.Equal(0, admission.WorkspaceBytes + admission.RetainedBytes);
    }

    [Theory]
    [InlineData(22000, 23496, 64, 128)]
    [InlineData(30000, 32288, 128, 129)]
    public void ExpandedCells_AdmitCopyAndFirstHaloWhileInitialCellsRemainLive(
        long allowance, long requested, long candidates, long pairs)
    {
        using var fixture = NativeSceneFixtures.Create("water-closed");
        var scene = Scene(fixture);
        // Discovery starts at B=3912. The initial 64 cells retain 8192 bytes.
        // Copying their 89-slot backing adds 11392 before construction (23496).
        // First-halo lookup/conversion/clipping adds 16+136+448, then its initial
        // candidate reservation adds 8192 (32288), before any dead halo exists.
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: allowance));
        using var active = admission.Enter();
        using var result = MapWaterScopedCompiler.Compile(scene, limits: new(MaxPairChecks: 8384));
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Equal(nameof(MapSceneLimits.MaxPreparedBytes), result.Capacity!.LimitName);
        Assert.Equal(requested, result.Capacity.Requested);
        Assert.Equal(allowance, result.Capacity.Allowance);
        Assert.Equal(candidates, result.CandidateCells);
        Assert.Equal(pairs, result.PairChecks);
        Assert.Empty(result.Cells);
        Assert.InRange(admission.PeakBytes, 1, allowance);
        Assert.Equal(0, admission.WorkspaceBytes + admission.RetainedBytes);
    }

    [Fact]
    public void ExpandedCells_AdmitReplacementWhileOldBackingAndHaloRemainLive()
    {
        using var fixture = NativeSceneFixtures.Create("water-closed");
        var scene = Scene(fixture, padding: 1);
        // One initial cell retains 128 and its copied three-slot set retains 384.
        // The first halo produces four cells. Before adding its fourth unique
        // cell, B=3912 + 128+384 + 16+136+512 is live. Replacement reserves
        // 2*6*128=1536 while old backing remains, giving 6624. Candidate
        // construction itself fits at 6144, so it cannot mask this missing gate.
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 6500));
        using var active = admission.Enter();
        using var result = MapWaterScopedCompiler.Compile(scene, limits: new(MaxPairChecks: 11));
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Equal(nameof(MapSceneLimits.MaxPreparedBytes), result.Capacity!.LimitName);
        Assert.Equal(6624, result.Capacity.Requested);
        Assert.Equal(6500, result.Capacity.Allowance);
        Assert.Equal(5, result.CandidateCells);
        Assert.Equal(11, result.PairChecks);
        Assert.Empty(result.Cells);
        Assert.InRange(admission.PeakBytes, 1, 6500);
        Assert.Equal(0, admission.WorkspaceBytes + admission.RetainedBytes);
    }
}
