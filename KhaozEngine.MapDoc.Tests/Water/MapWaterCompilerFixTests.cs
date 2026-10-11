using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public class MapWaterCompilerFixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TangentialCeiling_ClassifiesTiesClosed_WithoutDegenerateDrawing(bool vertexOnly)
    {
        MapDocument doc = WaterFixtures.TangentialCeiling(vertexOnly);
        MapCompiledWaterBody lake = MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!).Single(b => b.BodyId == "cave-lake");
        Assert.Equal(MapWaterCompileStatus.Complete, lake.Status);
        Assert.DoesNotContain(lake.Regions, r => r.Kind == MapWaterRegionKind.Closed);
        Assert.All(lake.Regions.SelectMany(r => r.Triangles), t => Assert.True(WaterFixtures.NormalY(t).Sign > 0));
        MapWaterQuery query = WaterFixtures.Query(doc);
        foreach (var (x, z, kind) in new[]
        {
            (3f, -2f, MapWaterRegionKind.Closed),
            (2.999f, -2f, MapWaterRegionKind.FreeSurface),
            (3.001f, -2f, MapWaterRegionKind.FreeSurface),
            (3f, -2.001f, vertexOnly ? MapWaterRegionKind.FreeSurface : MapWaterRegionKind.Closed),
        })
        {
            MapWaterSample sample = query.Sample(WaterFixtures.Point(x, -38, z));
            Assert.Equal(MapWaterStatus.Wet, sample.Status);
            Assert.Equal(kind == MapWaterRegionKind.FreeSurface, sample.UpperIsFreeSurface);
            Assert.Equal(kind, lake.Classify("lake-cave", new(MapExactValue.FromSingle(x), MapExactValue.FromSingle(z))));
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(20_000)]
    public void SparseMissingBound_RefusesBeforeReadOrRegionTraversal(int boundCells)
    {
        MapDocument doc = WaterFixtures.FineMissingCeiling();
        var limits = new MapWaterCompileLimits(MaxPieces: 1, MaxPieceVertices: 4, MaxFootprintCells: 24,
            MaxBoundFaces: 2, MaxPairChecks: 64, MaxBoundCells: boundCells);
        MapCompiledWaterBody lake = Assert.Single(MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!, limits));
        Assert.Equal((MapWaterCompileStatus.CapacityExceeded, "water bound cells"), (lake.Status, lake.Detail));
        Assert.Empty(lake.Regions);
        Assert.True(lake.Work.BoundCells + lake.Work.BoundSlots > boundCells);
        Assert.Equal((1, 2, 0), (lake.Work.BoundReads, lake.Work.LowerFaces, lake.Work.UpperFaces));
    }

    [Fact]
    public void SparseMissingBound_ChargesAbsentSlotsAndFaceFreeCells_WithDefaultLimits()
    {
        MapDocument doc = WaterFixtures.FineMissingCeiling();
        MapCompiledWaterBody lake = Assert.Single(MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!));
        Assert.Equal(MapWaterCompileStatus.Incomplete, lake.Status);
        Assert.Equal((5, 32_770), (lake.Work.BoundSlots, lake.Work.BoundCells));
        Assert.Equal(new MapExactValue(1, 1), WaterFixtures.Area(lake.Regions.SelectMany(r => r.Triangles)));
    }

    [Fact]
    public void CanonicalZeroLevel_HasTheSameCompiledDigestAndBounds()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapCompiledWaterBody positive = Ocean(doc);
        SetOceanLevel(doc, BitConverter.Int32BitsToSingle(int.MinValue));
        MapCompiledWaterBody negative = Ocean(doc);
        Assert.Equal(positive.Digest, negative.Digest);
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(negative.Bounds!.Value.MaxY));
    }

    [Fact]
    public void CanonicalWaterHashEncoding_NormalizesZeroMediumValues()
    {
        var medium = new MapWaterMediumDoc("water", MapWaterMediumKind.Water, 0f);
        Assert.Equal(MapWaterSet.MediumDigest(medium),
            MapWaterSet.MediumDigest(medium with { WadeSpeedScale = BitConverter.Int32BitsToSingle(int.MinValue) }));
    }

    [Fact]
    public void NegativeZeroLevel_SaveLoadPreservesTheCompiledIdentity()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        SetOceanLevel(doc, BitConverter.Int32BitsToSingle(int.MinValue));
        MapCompiledWaterBody before = Ocean(doc);
        string dir = WaterFixtures.TempDirectory();
        MapDocumentFile.SaveTiled(doc, dir);
        MapDocument loaded = MapDocumentFile.LoadTiled(dir);
        Assert.Equal(0, BitConverter.SingleToInt32Bits(loaded.Water!.Bodies.Single(b => b.Id == "ocean").SurfaceY));
        Assert.Equal(before.Digest, Ocean(loaded).Digest);
    }

    public static IEnumerable<object[]> NegativeLimits()
    {
        yield return new object[] { new MapWaterCompileLimits(MaxPieces: -1), "MaxPieces" };
        yield return new object[] { new MapWaterCompileLimits(MaxPieceVertices: -1), "MaxPieceVertices" };
        yield return new object[] { new MapWaterCompileLimits(MaxFootprintCells: -1), "MaxFootprintCells" };
        yield return new object[] { new MapWaterCompileLimits(MaxBoundFaces: -1), "MaxBoundFaces" };
        yield return new object[] { new MapWaterCompileLimits(MaxPairChecks: -1), "MaxPairChecks" };
        yield return new object[] { new MapWaterCompileLimits(MaxTriangles: -1), "MaxTriangles" };
        yield return new object[] { new MapWaterCompileLimits(MaxBoundCells: -1), "MaxBoundCells" };
        yield return new object[] { new MapWaterCompileLimits(Reads: new(MaxSourceFacesPerBound: -1)), "Reads.MaxSourceFacesPerBound" };
        yield return new object[] { new MapWaterCompileLimits(Reads: new(MaxPairChecks: -1)), "Reads.MaxPairChecks" };
        yield return new object[] { new MapWaterCompileLimits(Reads: new(MaxRefinementFaces: -1)), "Reads.MaxRefinementFaces" };
        yield return new object[] { new MapWaterCompileLimits(Reads: new(MaxRefinementVertices: -1)), "Reads.MaxRefinementVertices" };
        yield return new object[] { new MapWaterCompileLimits(Reads: new(MaxContextPatches: -1)), "Reads.MaxContextPatches" };
        yield return new object[] { new MapWaterCompileLimits(Reads: new(MaxContextFaces: -1)), "Reads.MaxContextFaces" };
    }

    [Theory]
    [MemberData(nameof(NegativeLimits))]
    public void NegativeCompileLimit_IsRejectedAtEntry_EvenWithoutBodies(MapWaterCompileLimits limits, string name)
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MapWaterCompiler.Compile(CaveFixtures.View(doc), new MapWaterSet(), limits));
        Assert.Equal(name, error.ParamName);
    }

    static MapCompiledWaterBody Ocean(MapDocument doc) => MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!)
        .Single(b => b.BodyId == "ocean");

    static void SetOceanLevel(MapDocument doc, float level)
    {
        int ocean = doc.Water!.Bodies.FindIndex(b => b.Id == "ocean");
        doc.Water.Bodies[ocean] = doc.Water.Bodies[ocean] with { SurfaceY = level };
    }
}
