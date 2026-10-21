using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapScopedWaterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopedWater_ProvesRequestedAndCompleteCellCoverage(bool tiled)
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var scene = NativeSceneFixtures.Open(fixture, tiled).Acquire(NativeSceneFixtures.WaterWindow(0, 1.25f)).Scene!;
        var water = MapWaterScopedCompiler.Compile(scene);
        Assert.Equal(MapSceneStatus.CompleteForScope, water.Status);
        Assert.True(water.RequestedWitness.CompleteForScope);
        Assert.True(water.AcquiredCellWitness.CompleteForScope);
        Assert.NotEqual(water.RequestedWitness.ScopeDigest, water.AcquiredCellWitness.ScopeDigest);
        Assert.Equal(scene.Witness.SourceGeneration, water.AcquiredCellWitness.SourceGeneration);
        var cell = Assert.Single(water.Cells);
        Assert.Equal(0, cell.Key.X);
        Assert.Equal(0, cell.Key.Z);
        Assert.Equal(.25f, cell.SurfaceY);
        Assert.Equal("fresh", cell.Medium.Key);
        Assert.Equal("water-look", cell.RenderMaterial);
        Assert.Contains(cell.Regions.SelectMany(r => r.Triangles).SelectMany(t => new[] { t.A, t.B, t.C }), p => p.X.ToDouble() == 2);
        Assert.Equal(3, Area(cell));
    }

    [Fact]
    public void OverlappingWindows_CanonicalWaterCellBytesAgree()
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var source = NativeSceneFixtures.Open(fixture, false);
        var a = MapWaterScopedCompiler.Compile(source.Acquire(NativeSceneFixtures.WaterWindow(0, 1.25f)).Scene!);
        var b = MapWaterScopedCompiler.Compile(source.Acquire(NativeSceneFixtures.WaterWindow(.75f, 2)).Scene!);
        var ac = Assert.Single(a.Cells); var bc = Assert.Single(b.Cells);
        Assert.Equal(ac.Key, bc.Key);
        Assert.Equal(ac.PayloadDigest, bc.PayloadDigest);
        Assert.Equal(ac.Regions.SelectMany(r => r.Triangles), bc.Regions.SelectMany(r => r.Triangles));
        Assert.DoesNotContain(ac.Regions.SelectMany(r => r.Triangles).SelectMany(t => new[] { t.A, t.B, t.C }),
            p => p.X.ToDouble() is .75 or 1.25);
    }

    [Fact]
    public void ClosedUnresolvedAndMissingWater_NeverBecomeVisibleOcean()
    {
        using var closedFixture = NativeSceneFixtures.Create("water-closed");
        var closed = MapWaterScopedCompiler.Compile(NativeSceneFixtures.Open(closedFixture, false).Acquire(NativeSceneFixtures.WaterWindow()).Scene!);
        Assert.True(closed.RequestedWitness.CompleteForScope);
        Assert.DoesNotContain(closed.Cells.SelectMany(c => c.Regions), r => r.Kind == MapWaterRegionKind.FreeSurface);
        using var missingFixture = NativeSceneFixtures.Create("water-missing");
        var missingColumn = MapWaterScopedCompiler.Compile(NativeSceneFixtures.Open(missingFixture, false)
            .Acquire(NativeSceneFixtures.WaterWindow(0, .75f)).Scene!);
        Assert.Equal(MapSceneStatus.Incomplete, missingColumn.Status);
        Assert.False(missingColumn.RequestedWitness.CompleteForScope);
        Assert.False(missingColumn.AcquiredCellWitness.CompleteForScope);
        Assert.Empty(missingColumn.Cells);
    }

    [Fact]
    public void CandidateAndPairLimits_RefuseWithoutPublishingPartialCells()
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(NativeSceneFixtures.WaterWindow()).Scene!;
        var candidate = MapWaterScopedCompiler.Compile(scene, .001f);
        Assert.Equal(MapSceneStatus.CapacityExceeded, candidate.Status);
        Assert.Empty(candidate.Cells);
        Assert.False(candidate.RequestedWitness.CompleteForScope);
        var pairs = MapWaterScopedCompiler.Compile(scene, limits: new(MaxPairChecks: 0));
        Assert.Equal(MapSceneStatus.CapacityExceeded, pairs.Status);
        Assert.Empty(pairs.Cells);
        Assert.Throws<ArgumentOutOfRangeException>(() => MapWaterScopedCompiler.Compile(scene, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapWaterScopedCompiler.Compile(scene, 3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StorageBordersSplitCells_AndNegativeIndicesSurvive(bool tiled)
    {
        using var fixture = NativeSceneFixtures.Create("water-bent-border");
        fixture.Document.TileSize = 19;
        var split = MapWaterScopedCompiler.Compile(NativeSceneFixtures.Open(fixture, tiled)
            .Acquire(NativeSceneFixtures.WaterWindow(18.75f, 19.25f, 1)).Scene!);
        Assert.Equal(MapSceneStatus.CompleteForScope, split.Status);
        Assert.Equal(2, split.Cells.Count);
        Assert.All(split.Cells, c => Assert.Equal(9, c.Key.X));
        Assert.Equal(new[] { new MapTileCoord(0, 0), new(1, 0) }, split.Cells.Select(c => c.Key.StorageOwner));
        Assert.Equal(2, split.Cells.Sum(Area));
        using var negative = NativeSceneFixtures.Create("water-negative");
        var water = MapWaterScopedCompiler.Compile(NativeSceneFixtures.Open(negative, tiled)
            .Acquire(NativeSceneFixtures.WaterWindow(-1.5f, -.5f, -.5f, -1.5f)).Scene!);
        var cell = Assert.Single(water.Cells);
        Assert.Equal(-1, cell.Key.X); Assert.Equal(-1, cell.Key.Z);
        Assert.Equal(new MapTileCoord(-1, -1), cell.Key.StorageOwner);
        Assert.Equal(4, Area(cell));
    }

    [Fact]
    public void WaterKernel_UsesTheSharedSceneAdmission()
    {
        var meter = new MapWaterWorkMeter(new());
        var admission = new MapSceneAdmission(new(MaxPreparedFaces: 0));
        using (admission.Enter()) Assert.Throws<MapSceneCapacityException>(() => meter.ChargeTriangles(1));
        Assert.Equal(0, admission.PreparedFaces);
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var empty = new MapSceneAdmission(new(MaxPreparedBytes: 0));
        using (empty.Enter()) Assert.Throws<MapSceneCapacityException>(() => fixture.Document.Water!.Clone());
        Assert.Equal(0, empty.RetainedBytes);
    }

    [Fact]
    public void EmptyWaterIsKnownEmpty_AndStalePinnedDependenciesRefuse()
    {
        using var empty = NativeSceneFixtures.Create("empty");
        var absent = MapWaterScopedCompiler.Compile(NativeSceneFixtures.Open(empty, false).Acquire(NativeSceneFixtures.WaterWindow()).Scene!);
        Assert.Empty(absent.Cells);
        Assert.True(absent.RequestedWitness.CompleteForScope);
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var source = NativeSceneFixtures.Open(fixture, true);
        var scene = source.Acquire(NativeSceneFixtures.WaterWindow()).Scene!;
        File.AppendAllText(Path.Combine(fixture.StorageDirectories[0], MapTiledFile.ManifestName), "\n");
        var stale = MapWaterScopedCompiler.Compile(scene);
        Assert.Equal(MapSceneStatus.Stale, stale.Status);
        Assert.Empty(stale.Cells);
        Assert.False(stale.RequestedWitness.CompleteForScope);
        Assert.False(stale.AcquiredCellWitness.CompleteForScope);
    }

    [Fact]
    public void MotionPaddingSelectsWholeDrawCells_BeforeTheirClipDependencies()
    {
        using var fixture = NativeSceneFixtures.Create("water-bent-one");
        var source = NativeSceneFixtures.Open(fixture, false);
        var scope = NativeSceneFixtures.WaterWindow(0, 2, 1) with { MotionPaddingMetres = 1 };
        var water = MapWaterScopedCompiler.Compile(source.Acquire(scope).Scene!);
        Assert.Equal(MapSceneStatus.CompleteForScope, water.Status);
        Assert.True(water.RequestedWitness.CompleteForScope);
        Assert.True(water.AcquiredCellWitness.CompleteForScope);
        Assert.Equal(new[] { (0L, 0L), (1L, 0L) }, water.Cells.Select(c => (c.Key.X, c.Key.Z)));
        // Motion selection reaches x=3, but the selected x=1 cell retains its full [2,4] x [0,1] surface.
        Assert.Equal(2, Area(water.Cells.Single(c => c.Key.X == 1)));
        Assert.Contains(water.AcquiredCells, c => c.Key.X == 2 && c.Key.Z == 0);
        Assert.Contains(water.AcquiredCells, c => c.Key.X == 0 && c.Key.Z == 1);
        Assert.DoesNotContain(water.Cells, c => c.Key.X == 2 || c.Key.Z == 1);
    }

    [Fact]
    public void MotionPaddingAcquiresNeighbours_WithoutChangingCanonicalPayloads()
    {
        using var fixture = NativeSceneFixtures.Create("water-bent-one");
        var source = NativeSceneFixtures.Open(fixture, false);
        var scope = NativeSceneFixtures.WaterWindow(0, 1, 1);
        var bare = MapWaterScopedCompiler.Compile(source.Acquire(scope).Scene!);
        var padded = MapWaterScopedCompiler.Compile(source.Acquire(scope with { MotionPaddingMetres = 1 }).Scene!);
        Assert.Equal(Assert.Single(bare.Cells).Key, Assert.Single(padded.Cells).Key);
        Assert.Equal(bare.Cells[0].PayloadDigest, padded.Cells[0].PayloadDigest);
        Assert.True(padded.AcquiredCells.Count > bare.AcquiredCells.Count);
        Assert.NotEqual(bare.RequestedWitness.ScopeDigest, padded.RequestedWitness.ScopeDigest);
        using var gap = NativeSceneFixtures.Create("water-bent-missing-halo");
        var gapSource = NativeSceneFixtures.Open(gap, false);
        var visible = MapWaterScopedCompiler.Compile(gapSource.Acquire(scope).Scene!);
        Assert.True(visible.RequestedWitness.CompleteForScope);
        var halo = MapWaterScopedCompiler.Compile(gapSource.Acquire(scope with { MotionPaddingMetres = 1 }).Scene!);
        Assert.Equal(MapSceneStatus.Incomplete, halo.Status);
        Assert.Empty(halo.Cells);
        Assert.False(halo.AcquiredCellWitness.CompleteForScope);
    }

    internal static double Area(MapWaterResourceCell cell) => cell.Regions.Where(r => r.Kind == MapWaterRegionKind.FreeSurface)
        .SelectMany(r => r.Triangles).Sum(t => Math.Abs((t.B.X.ToDouble() - t.A.X.ToDouble()) * (t.C.Z.ToDouble() - t.A.Z.ToDouble()) -
            (t.B.Z.ToDouble() - t.A.Z.ToDouble()) * (t.C.X.ToDouble() - t.A.X.ToDouble())) * .5);
}
