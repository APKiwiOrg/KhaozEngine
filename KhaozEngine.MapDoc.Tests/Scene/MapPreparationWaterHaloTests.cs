using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationWaterHaloTests
{
    // One 16 m rectangle in a 64 m storage owner. Each of the 64 selected 2 m cells
    // sees the same complete rectangle through its 16 m halo.
    static MapScopedScene Scene(NativeSceneFixture fixture)
    {
        var body = fixture.Document.Water!.Bodies[0];
        fixture.Document.Water.Bodies[0] = body with
        { Domain = body.Domain with { Rects = [new(0, 0, 16, 16)], Rings = [] } };
        return NativeSceneFixtures.Open(fixture, false)
            .Acquire(NativeSceneFixtures.WaterWindow(7, 8, 8, 7) with { MotionPaddingMetres = 16 }).Scene!;
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
}
