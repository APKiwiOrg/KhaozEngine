using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDocCompatibility;

public sealed class LegacyWaterOracleTests
{
    static readonly LegacyWaterOracleWorld World = LegacyWaterOracleWorld.Create();
    static readonly (MapDocument Document, IReadOnlyList<LegacyWaterSource> Sources) Native = LegacyWaterConverter.ToNative(World.Document, World.Catalogs);
    static readonly LegacyWaterReport Report = LegacyWaterComparison.Compare(Native.Document, Native.Sources);
    static MapWaterQuery Query() => new(MapScopedSurfaces.CompleteView(Native.Document.Surfaces), Native.Document.Water!);

    [Fact]
    public void RegionPieces_KeepOneBodyEach_AndEveryLevelExactly()
    {
        Assert.Equal(5, Native.Sources.Count);
        foreach (LegacyWaterSource source in Native.Sources)
            Assert.Equal(source.Body.SurfaceY, Native.Document.Water!.Bodies.Single(b => b.Id == source.BodyId).SurfaceY);
        float river = Assert.Single(Native.Sources.Where(LegacyWaterOracleWorld.IsRiver).Select(s => s.Body.SurfaceY).Distinct());
        Assert.Equal(2, Native.Sources.Count(LegacyWaterOracleWorld.IsRiver));
        Assert.All(Native.Sources.Where(s => !LegacyWaterOracleWorld.IsRiver(s)), s => Assert.NotEqual(river, s.Body.SurfaceY));
    }

    [Fact]
    public void EveryCellCentre_HasWaterInItsOwnBody_ExceptTheShoalCorners()
    {
        Assert.Empty(Report.Failures);
        Assert.Equal((5, 6, 218), (Report.Bodies, Report.Rects, Report.Cells));
        Assert.Equal(Native.Sources.Sum(s => s.Body.Rects.Sum(r => r.Width * r.Height)), Report.Cells);
        LegacyWaterDifference[] dry = Report.Differences.Where(d => d.Kind == LegacyWaterDifferenceKind.FloorAboveSurface).ToArray();
        Assert.Equal(4, dry.Length);
        string shoal = Native.Sources.Single(LegacyWaterOracleWorld.IsShoal).BodyId;
        Assert.All(dry, d => Assert.Equal((shoal, true, false), (d.BodyId, d.Legacy, d.Native)));
    }

    [Fact]
    public void ShallowPuddle_IsWetByFiveMillimetres()
    {
        LegacyWaterSource puddle = Native.Sources.Single(LegacyWaterOracleWorld.IsPuddle);
        MapFramePoint centre = LegacyWaterComparison.CellCentres(puddle, y: 0f).Single();
        MapWaterSample s = Assert.Single(Query().Column(centre));
        Assert.Equal((MapWaterStatus.Wet, puddle.BodyId, 0.075f, puddle.Body.SurfaceY), (s.Status, s.BodyId, s.LowerY, s.UpperY));
        Assert.True(s.UpperY - s.LowerY < 0.006f);
    }

    [Fact]
    public void NegativeZEdges_ReportEveryOwnershipChange()
    {
        LegacyWaterDifference[] owners = Report.Differences.Where(d => d.Kind == LegacyWaterDifferenceKind.BoundaryOwner).ToArray();
        Assert.Equal(48, Report.BoundarySamples);
        Assert.Equal(22, owners.Length);
        Assert.Equal(10, owners.Count(d => d.Legacy));
        Assert.Equal(12, owners.Count(d => d.Native));
        Assert.NotEmpty(owners);
        Assert.All(owners, d => Assert.Equal(1, d.Point.Z.Denominator));
        Assert.All(owners, d => Assert.NotEqual(d.Legacy, d.Native));
        Assert.Contains(owners, d => d.Legacy && !d.Native);
        Assert.Contains(owners, d => !d.Legacy && d.Native);
    }

    [Fact]
    public void SeamBetweenRegionPieces_IsOwnedOnce_AtOneLevel()
    {
        MapWaterBodyDoc[] pieces = Native.Sources.Where(LegacyWaterOracleWorld.IsRiver)
            .Select(s => Native.Document.Water!.Bodies.Single(b => b.Id == s.BodyId)).ToArray();
        Assert.Equal(pieces[0].SurfaceY, pieces[1].SurfaceY);
        foreach (MapExactXz p in LegacyWaterOracleWorld.SeamSamples())
        {
            Assert.Equal(1, pieces.Count(b => MapWaterDomain.Compile(b.Domain).Contains(p)));
            MapWaterSample s = Query().Sample(new MapFramePoint(default, new Vector3(p.X.ToSingle(), pieces[0].SurfaceY - 0.01f, p.Z.ToSingle())));
            Assert.Equal(MapWaterStatus.Wet, s.Status);
        }
    }

    [Fact]
    public void SurfaceFeet_StayDry()
    {
        foreach (LegacyWaterSource source in Native.Sources)
            foreach (MapFramePoint p in LegacyWaterComparison.CellCentres(source, y: source.Body.SurfaceY))
                Assert.Equal(MapWaterStatus.Dry, Query().Sample(p).Status);
    }

    [Fact]
    public void ConvertedWater_IsContained_AndEmitsTwoTrianglesPerRect()
    {
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(Native.Document.Surfaces);
        MapWaterContainmentReport containment = MapWaterContainment.Validate(view, Native.Document.Water!);
        Assert.Equal((true, 0), (containment.Complete, containment.Errors.Count));
        IReadOnlyList<MapCompiledWaterBody> compiled = MapWaterCompiler.Compile(view, Native.Document.Water!);
        Assert.All(compiled, b => Assert.Equal(MapWaterCompileStatus.Complete, b.Status));
        Assert.Equal(2 * Native.Sources.Sum(s => s.Body.Rects.Count), compiled.Sum(b => b.Work.Triangles));
        foreach (LegacyWaterSource source in Native.Sources)
        {
            int lowestCm = source.Body.Rects.SelectMany(r =>
                Enumerable.Range(r.Z, r.Height + 1).SelectMany(z =>
                    Enumerable.Range(r.X, r.Width + 1).Select(x => World.Document.CornerHeightCm(x, z, source.Plane)))).Min();
            Assert.Equal(lowestCm / 100f, compiled.Single(b => b.BodyId == source.BodyId).DeepestBedY);
        }
    }

    [Fact]
    public void WaterOffPlaneZero_Refuses()
    {
        LegacyWaterOracleWorld world = LegacyWaterOracleWorld.WithPlaneOneWater();
        var error = Assert.Throws<MapDocumentException>(() => LegacyWaterConverter.ToNative(world.Document, world.Catalogs));
        Assert.Contains("water on plane 1 is outside the R4 comparison", error.Message, StringComparison.Ordinal);
    }
}
