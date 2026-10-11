using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public class MapWaterCompilerTests
{
    static MapCompiledWaterBody Body(MapDocument doc, string id, MapWaterCompileLimits? limits = null) =>
        MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!, limits).Single(b => b.BodyId == id);
    static IEnumerable<MapExactTriangle> Of(MapCompiledWaterBody b, MapWaterRegionKind kind) =>
        b.Regions.Where(r => r.Kind == kind).SelectMany(r => r.Triangles);
    static HashSet<MapExactPoint> Points(IEnumerable<MapExactTriangle> t) => t.SelectMany(x => new[] { x.A, x.B, x.C }).ToHashSet();
    static MapExactXz Xz(float x, float z) => new(MapExactValue.FromSingle(x), MapExactValue.FromSingle(z));

    [Theory]
    [InlineData(512f)]
    [InlineData(4096f)]
    public void OceanSurface_IsTwoTriangles_AtAnySize(float sizeMetres)
    {
        MapCompiledWaterBody sea = Body(WaterFixtures.OpenSea(sizeMetres), "sea");
        int cells = (int)(sizeMetres / 8) * (int)(sizeMetres / 8);
        Assert.Equal((1, 1, cells, 0, 2), (sea.Work.Pieces, sea.Work.FootprintRects, sea.Work.FootprintCells, sea.Work.UpperFaces, sea.Work.Triangles));
        Assert.Equal(MapWaterRegionKind.FreeSurface, Assert.Single(sea.Regions).Kind);
        Assert.Equal(MapWaterCompileStatus.Complete, sea.Status);
    }

    [Fact]
    public void RiverOfManyRects_EmitsPerPiece()
    {
        MapCompiledWaterBody river = Body(WaterFixtures.ManyRectRiver(), "river");
        Assert.Equal((46, 1, 92), (river.Work.Pieces, river.Work.FootprintRects, river.Work.Triangles));
    }

    [Fact]
    public void CaveLake_SplitsAtTheCeilingContour()
    {
        MapCompiledWaterBody lake = Body(WaterFixtures.OceanOverCaves(), "cave-lake");
        Assert.Equal(new MapExactValue(16, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.FreeSurface)));
        Assert.Equal(new MapExactValue(8, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.Closed)));
        var seam = Points(Of(lake, MapWaterRegionKind.FreeSurface)).Intersect(Points(Of(lake, MapWaterRegionKind.Closed))).ToArray();
        Assert.All(seam, p => Assert.Equal(new MapExactValue(2, 1), p.X));
        Assert.Contains(seam, p => p.Z == new MapExactValue(-4, 1));
        Assert.Contains(seam, p => p.Z == new MapExactValue(0, 1));
    }

    [Fact]
    public void ContourPoints_ClassifyClosed_AsTheQueryDoes()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapCompiledWaterBody lake = Body(doc, "cave-lake");
        Assert.Equal(MapWaterRegionKind.Closed, lake.Classify("lake-cave", Xz(2, -1)));
        Assert.Equal(MapWaterRegionKind.Closed, lake.Classify("lake-cave", Xz(1, -1)));
        Assert.Equal(MapWaterRegionKind.FreeSurface, lake.Classify("lake-cave", Xz(2.5f, -1)));
        Assert.Null(lake.Classify("lake-cave", Xz(7, -1)));
        foreach (float x in new[] { 1f, 2f, 2.5f })
            Assert.Equal(lake.Classify("lake-cave", Xz(x, -1)) == MapWaterRegionKind.FreeSurface,
                WaterFixtures.Query(doc).Sample(WaterFixtures.Point(x, -38, -1)).UpperIsFreeSurface);
    }

    [Fact]
    public void DiagonalContour_SplitsExactly()
    {
        MapCompiledWaterBody lake = Body(WaterFixtures.Broken("diagonal-roof"), "cave-lake");
        Assert.Equal(new MapExactValue(16, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.FreeSurface)));
        Assert.Equal(new MapExactValue(8, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.Closed)));
        Assert.Equal(MapWaterRegionKind.Closed, lake.Classify("lake-cave", Xz(2, -2)));
        Assert.Equal(MapWaterRegionKind.FreeSurface, lake.Classify("lake-cave", Xz(2.5f, -2)));
    }

    [Fact]
    public void Classify_FollowsDomainAndFootprintOwnership_AsTheQueryDoes()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapCompiledWaterBody ocean = Body(doc, "ocean"), lake = Body(doc, "cave-lake");
        Assert.Null(ocean.Classify("sea", Xz(4, -4)));
        Assert.Null(ocean.Classify("sea", Xz(4, 0)));
        Assert.Null(ocean.Classify("sea", Xz(12, -2)));
        Assert.Equal(MapWaterRegionKind.FreeSurface, ocean.Classify("sea", Xz(0, -2)));
        Assert.Null(lake.Classify("lake-cave", Xz(2.5f, 0)));
        foreach (var (x, z) in new[] { (4f, -4f), (4f, 0f), (0f, -2f), (4f, -2f) })
            Assert.Equal(ocean.Classify("sea", Xz(x, z)) is not null, WaterFixtures.Query(doc).Sample(WaterFixtures.Point(x, -1, z)).Status == MapWaterStatus.Wet);
        MapDocument split = WaterFixtures.Broken("split-ocean");
        MapCompiledWaterBody west = Body(split, "ocean-west"), east = Body(split, "ocean-east");
        foreach (float z in new[] { -3.5f, -2f, -0.5f })
            Assert.Equal(1, new[] { west, east }.Count(b => b.Classify("sea", Xz(6, z)) is not null));
    }

    [Fact]
    public void Sinkhole_ClassifiesEachStackedSpaceOnItsOwn()
    {
        MapDocument doc = WaterFixtures.SinkholeCave(-10f);
        MapCompiledWaterBody ocean = Body(doc, "ocean");
        Assert.Equal(MapWaterRegionKind.FreeSurface, ocean.Classify("sea", Xz(5, -2)));
        Assert.Equal(MapWaterRegionKind.Closed, ocean.Classify("sink", Xz(5, -2)));
        Assert.True(WaterFixtures.Query(doc).Sample(WaterFixtures.Point(5, -5, -2)).UpperIsFreeSurface);
        Assert.False(WaterFixtures.Query(doc).Sample(WaterFixtures.Point(5, -20, -2)).UpperIsFreeSurface);
    }

    [Fact]
    public void CeilingExactlyAtTheSurface_IsClosed()
    {
        MapCompiledWaterBody lake = Body(WaterFixtures.Broken("flat-roof-at-surface"), "cave-lake");
        Assert.Empty(Of(lake, MapWaterRegionKind.FreeSurface));
        Assert.Equal(new MapExactValue(24, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.Closed)));
    }

    [Fact]
    public void FloodedCave_DrawsNoSurface()
    {
        MapCompiledWaterBody flooded = Body(WaterFixtures.OceanOverCaves(), "flooded");
        Assert.Empty(Of(flooded, MapWaterRegionKind.FreeSurface));
        Assert.Equal(new MapExactValue(8, 1), WaterFixtures.Area(Of(flooded, MapWaterRegionKind.Closed)));
    }

    [Fact]
    public void MixedLattices_SplitAtTheSameContour()
    {
        MapCompiledWaterBody lake = Body(WaterFixtures.OceanOverCavesWithThirdMetreRoof(), "cave-lake");
        Assert.Equal(new MapExactValue(16, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.FreeSurface)));
        Assert.Equal(new MapExactValue(8, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.Closed)));
        Assert.Equal(MapWaterCompileStatus.Complete, lake.Status);
    }

    [Fact]
    public void SubdividedCeiling_CompilesWithinReadLimits()
    {
        MapCompiledWaterBody lake = Body(WaterFixtures.Broken("fine-roof"), "cave-lake");
        Assert.Equal(MapWaterCompileStatus.Complete, lake.Status);
        Assert.Equal(new MapExactValue(8, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.Closed)));
    }

    [Fact]
    public void LargeThirdMetreRoof_ReadsInChunks()
    {
        MapCompiledWaterBody lake = Body(WaterFixtures.BigCaveLake(), "big-lake");
        Assert.Equal(MapWaterCompileStatus.Complete, lake.Status);
        Assert.Equal(73_728, lake.Work.UpperFaces);
        Assert.True(lake.Work.BoundReads >= 5);
        Assert.Equal(new MapExactValue(4096, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.FreeSurface)));
    }

    [Fact]
    public void SingleCellAboveTheReadLimit_RefusesWithCapacityExceeded()
    {
        var limits = new MapWaterCompileLimits(Reads: new MapRefinementLimits(MaxSourceFacesPerBound: 1));
        Assert.Equal(MapWaterCompileStatus.CapacityExceeded, Body(WaterFixtures.OceanOverCaves(), "cave-lake", limits).Status);
    }

    [Fact]
    public void UnrepresentableContour_IsNotRepresentable()
    {
        MapDocument doc = WaterFixtures.UnrepresentableContourCave();
        Assert.False(WaterFixtures.ContourNarrows(doc));
        MapCompiledWaterBody lake = Body(doc, "fine-lake");
        Assert.Equal(MapWaterCompileStatus.NotRepresentable, lake.Status);
        Assert.Empty(lake.Regions);
    }

    [Fact]
    public void HorizontalOpeningAboveWater_ActsAsACeiling()
    {
        MapCompiledWaterBody ocean = Body(WaterFixtures.SinkholeCave(-10f), "ocean");
        MapWaterRegion[] sink = ocean.Regions.Where(r => r.SpaceId == "sink").ToArray();
        Assert.All(sink, r => Assert.Equal(MapWaterRegionKind.Closed, r.Kind));
        Assert.Equal(new MapExactValue(8, 1), WaterFixtures.Area(sink.SelectMany(r => r.Triangles)));
    }

    [Fact]
    public void SignedSeam_CompilesAcrossSlots()
    {
        MapCompiledWaterBody sea = Body(WaterFixtures.SeaAcrossTheOrigin(), "sea");
        Assert.Equal(MapWaterCompileStatus.Complete, sea.Status);
        Assert.Equal(new MapExactValue(256, 1), WaterFixtures.Area(Of(sea, MapWaterRegionKind.FreeSurface)));
    }

    [Fact]
    public void KnownEmptyCeiling_IsUnresolved()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapScopedSurfaces view = WaterFixtures.ViewWithKnownEmpty(doc, "lake-roof");
        Assert.Contains(view.Witness.KnownEmpty, r => r.SurfaceId == "lake-roof");
        MapCompiledWaterBody lake = MapWaterCompiler.Compile(view, doc.Water!).Single(b => b.BodyId == "cave-lake");
        Assert.Equal(MapWaterCompileStatus.Incomplete, lake.Status);
        Assert.Equal(new MapExactValue(24, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.Unresolved)));
    }

    [Theory]
    [InlineData("lake-roof-missing-cell", 1)]
    [InlineData("lake-roof-partial", 6)]
    public void UncoveredCeiling_IsUnresolved_NotDropped(string variant, int unresolvedArea)
    {
        MapCompiledWaterBody lake = Body(WaterFixtures.Broken(variant), "cave-lake");
        Assert.Equal(MapWaterCompileStatus.Incomplete, lake.Status);
        Assert.Equal(new MapExactValue(unresolvedArea, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.Unresolved)));
        Assert.Equal(new MapExactValue(24, 1), WaterFixtures.Area(lake.Regions.SelectMany(r => r.Triangles)));
    }

    [Fact]
    public void LowerHole_LeavesItsAreaUnresolved()
    {
        MapCompiledWaterBody ocean = Body(WaterFixtures.Broken("seabed-hole"), "ocean");
        Assert.Equal(MapWaterCompileStatus.Incomplete, ocean.Status);
        Assert.Equal(new MapExactValue(1, 1), WaterFixtures.Area(Of(ocean, MapWaterRegionKind.Unresolved)));
        Assert.Equal(-20f, ocean.DeepestBedY);
    }

    [Fact]
    public void MissingCeilingGeometry_IsUnresolved()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapCompiledWaterBody lake = MapWaterCompiler.Compile(WaterFixtures.ViewWithUnloaded(doc, "lake-roof"), doc.Water!).Single(b => b.BodyId == "cave-lake");
        Assert.Equal(MapWaterCompileStatus.Incomplete, lake.Status);
        Assert.Contains(lake.Regions, r => r.Kind == MapWaterRegionKind.Unresolved);
    }

    [Theory]
    [InlineData(10, 65_536, 16_777_216, "water footprint cells")]
    [InlineData(4_194_304, 0, 16_777_216, "water pieces")]
    [InlineData(4_194_304, 65_536, 1, "water pair checks")]
    public void CompileLimits_RefuseOversizedWork(int cells, int pieces, int pairs, string detail)
    {
        var limits = new MapWaterCompileLimits(MaxPieces: pieces, MaxFootprintCells: cells, MaxPairChecks: pairs);
        MapCompiledWaterBody ocean = Body(WaterFixtures.OceanOverCaves(), "ocean", limits);
        Assert.Equal((MapWaterCompileStatus.CapacityExceeded, detail), (ocean.Status, ocean.Detail));
        Assert.Empty(ocean.Regions);
    }

    [Fact]
    public void UncontainedBody_IsInvalid()
    {
        MapDocument doc = WaterFixtures.Broken("domain-off-footprint");
        MapCompiledWaterBody ocean = Body(doc, "ocean");
        Assert.Equal((MapWaterCompileStatus.Invalid, "water body 'ocean' covers no space footprint"), (ocean.Status, ocean.Detail));
    }

    [Fact]
    public void Compile_IsDeterministic_AndSurvivesTiledSaveReload()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string dir = WaterFixtures.TempDirectory();
        MapDocumentFile.SaveTiled(doc, dir);
        MapDocument loaded = MapDocumentFile.LoadTiled(dir);
        Assert.Equal(MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!).Select(b => b.Digest),
            MapWaterCompiler.Compile(CaveFixtures.View(loaded), loaded.Water!).Select(b => b.Digest));
    }

    [Fact]
    public void Digest_FollowsOnlyWhatTheBodyRead()
    {
        MapDocument before = WaterFixtures.OceanOverCaves(), after = WaterFixtures.Broken("flood-floor-lowered");
        string Digest(MapDocument d, string id) => Body(d, id).Digest;
        Assert.NotEqual(Digest(before, "flooded"), Digest(after, "flooded"));
        Assert.Equal(Digest(before, "cave-lake"), Digest(after, "cave-lake"));
        Assert.Equal(Digest(before, "ocean"), Digest(after, "ocean"));
    }

    [Fact]
    public void Digest_FollowsConsumedSurfaceMetadata()
    {
        MapDocument before = WaterFixtures.OceanOverCaves(), halved = WaterFixtures.Broken("seabed-half-height-unit"), other = WaterFixtures.Broken("flood-floor-half-height-unit");
        MapCompiledWaterBody a = Body(before, "ocean"), b = Body(halved, "ocean");
        Assert.NotEqual(a.Digest, b.Digest);
        Assert.Equal((-20f, -10f), (a.DeepestBedY!.Value, b.DeepestBedY!.Value));
        Assert.Equal(a.Digest, Body(other, "ocean").Digest);
        Assert.Equal(Body(before, "cave-lake").Digest, Body(other, "cave-lake").Digest);
        Assert.NotEqual(Body(before, "flooded").Digest, Body(other, "flooded").Digest);
    }

    [Fact]
    public void MediumDefinition_EntersTheDigest()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string before = Body(doc, "ocean").Digest;
        doc.Water!.Media[0] = doc.Water.Media[0] with { WadeSpeedScale = 0.5f };
        MapCompiledWaterBody after = Body(doc, "ocean");
        Assert.NotEqual(before, after.Digest);
        Assert.Equal(0.5f, after.Medium.WadeSpeedScale);
    }

    [Fact]
    public void DeepestBed_BoundsTheBody()
    {
        MapCompiledWaterBody ocean = Body(WaterFixtures.OceanOverCaves(), "ocean");
        Assert.Equal(-20f, ocean.DeepestBedY);
        Assert.Equal((-20d, 0d), (ocean.Bounds!.Value.MinY, ocean.Bounds.Value.MaxY));
    }

    [Fact]
    public void FreeSurfaceTriangles_FaceUp()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        foreach (MapExactTriangle t in MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!).SelectMany(b => b.Regions).SelectMany(r => r.Triangles))
            Assert.True(WaterFixtures.NormalY(t).Sign > 0);
    }
}
