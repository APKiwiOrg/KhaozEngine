using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.TileWorld;
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
        var nonRiver = Native.Sources.Where(s => !LegacyWaterOracleWorld.IsRiver(s)).ToArray();
        Assert.NotEmpty(nonRiver);
        Assert.All(nonRiver, s => Assert.NotEqual(river, s.Body.SurfaceY));
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
    public void UnrepresentableCellCentres_ReportFailuresInsteadOfCheckingRoundedPoints()
    {
        var source = new LegacyWaterSource("far-water", RegionCoord.Of(8_388_609, 0), 0, 0,
            new TileWaterBody(new[] { new TileRect(8_388_609, 0, 2, 1) }, 0f));
        MapWaterBodyDoc template = Native.Document.Water!.Bodies[0];
        var water = new MapWaterSet();
        water.Media.AddRange(Native.Document.Water.Media);
        water.Bodies.Add(template with
        {
            Id = source.BodyId,
            SurfaceY = source.Body.SurfaceY,
            Domain = new(MapLatticeFrame.ImportedMetreCentimetre,
                new[] { new MapWaterRect(8_388_609, 0, 8_388_611, 1) }, Array.Empty<MapWaterRing>()),
        });
        var document = new MapDocument { Surfaces = Native.Document.Surfaces, Water = water };

        LegacyWaterReport report = LegacyWaterComparison.Compare(document, new[] { source });

        Assert.Contains(report.Failures, f => f.Contains("far-water", StringComparison.Ordinal) && f.Contains("(8388609, 0)", StringComparison.Ordinal));
        Assert.Contains(report.Failures, f => f.Contains("far-water", StringComparison.Ordinal) && f.Contains("(8388610, 0)", StringComparison.Ordinal));
        Assert.Equal(0, report.Cells);
        Assert.Throws<MapDocumentException>(() => LegacyWaterComparison.CellCentres(source, 0f).ToArray());
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
        Assert.NotEmpty(Native.Sources);
        foreach (LegacyWaterSource source in Native.Sources)
        {
            var centres = LegacyWaterComparison.CellCentres(source, y: source.Body.SurfaceY).ToArray();
            Assert.NotEmpty(centres);
            foreach (MapFramePoint p in centres)
                Assert.Equal(MapWaterStatus.Dry, Query().Sample(p).Status);
        }
    }

    [Fact]
    public void ConvertedWater_IsContained_AndEmitsTwoTrianglesPerRect()
    {
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(Native.Document.Surfaces);
        MapWaterContainmentReport containment = MapWaterContainment.Validate(view, Native.Document.Water!);
        Assert.Equal((true, 0), (containment.Complete, containment.Errors.Count));
        IReadOnlyList<MapCompiledWaterBody> compiled = MapWaterCompiler.Compile(view, Native.Document.Water!);
        Assert.NotEmpty(compiled);
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
        var error = Assert.Throws<LegacyWaterComparisonException>(() => LegacyWaterConverter.ToNative(world.Document, world.Catalogs));
        Assert.Equal(LegacyWaterComparisonRefusal.PlaneUnsupported, error.Reason);
        Assert.Equal(1, error.Plane);

        world.Document.SetUnderlay(4, 4, 1, 2);
        var accepted = LegacyWaterConverter.ToNative(world.Document, world.Catalogs);
        Assert.DoesNotContain(accepted.Sources, source => source.Plane != 0);
    }

    [Fact]
    public void MissingPlaneZeroRegion_Refuses()
    {
        var document = new TileWorldDocument { Id = "public-legacy-water-oracle" };
        document.GetOrCreateRegion(new(0, 0));
        var error = Assert.Throws<LegacyWaterComparisonException>(() => LegacyWaterConverter.ToNative(document, World.Catalogs));
        Assert.Equal(LegacyWaterComparisonRefusal.RegionMissing, error.Reason);
        Assert.Null(error.Plane);

        document.SetUnderlay(0, 0, 0, 2);
        var accepted = LegacyWaterConverter.ToNative(document, World.Catalogs);
        Assert.Contains(accepted.Document.Surfaces.Refs, surface => surface.Id == "plane-0");
    }
}
