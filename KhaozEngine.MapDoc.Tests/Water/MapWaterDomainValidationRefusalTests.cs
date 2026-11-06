using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public sealed class MapWaterDomainValidationRefusalTests
{
    [Theory]
    [InlineData("domain", MapDocumentRefusal.SchemaValueRequired, "Domain")]
    [InlineData("rects", MapDocumentRefusal.SchemaValueRequired, "Domain.Rects")]
    [InlineData("rings", MapDocumentRefusal.SchemaValueRequired, "Domain.Rings")]
    [InlineData("frame", MapDocumentRefusal.SchemaValueRequired, "Domain.Frame")]
    [InlineData("unit", MapDocumentRefusal.ValueOutsideRange, "Domain.Frame.numerator")]
    [InlineData("enum", MapDocumentRefusal.SchemaEnumUnsupported, "Domain.Frame.RowDirection")]
    [InlineData("empty", MapDocumentRefusal.WaterDomainEmpty, "Domain")]
    [InlineData("rect", MapDocumentRefusal.BoundsInvalid, "Domain.Rects[0]")]
    [InlineData("ring", MapDocumentRefusal.SchemaValueRequired, "Domain.Rings[0]")]
    [InlineData("vertices", MapDocumentRefusal.SchemaValueRequired, "Domain.Rings[0].Vertices")]
    [InlineData("short", MapDocumentRefusal.ValueOutsideRange, "Domain.Rings[0].Vertices")]
    [InlineData("address", MapDocumentRefusal.ValueOutsideRange, "Domain.Rings[0].Vertices[0].denominator")]
    [InlineData("far", MapDocumentRefusal.ValueOutsideRange, "Domain.Rects[0]")]
    [InlineData("overflow", MapDocumentRefusal.GeometryNotRepresentable, "Domain.Rects[0]")]
    public void Domain_ReportsTheFailedBranchWithoutChangingTheDocument(string defect, MapDocumentRefusal reason, string field)
    {
        var doc = WaterFixtures.OceanOverCaves();
        var body = doc.Water!.Bodies.Single(b => b.Id == "ocean");
        var valid = body.Domain;
        var ring = new MapWaterRing([MapLatticeAddress.Corner(14, 0), MapLatticeAddress.Corner(15, 0), MapLatticeAddress.Corner(14, 1)]);
        var domain = defect switch
        {
            "domain" => null!,
            "rects" => valid with { Rects = null! },
            "rings" => valid with { Rings = null! },
            "frame" => valid with { Frame = null! },
            "unit" => valid with { Frame = valid.Frame with { CellUnitMetres = default } },
            "enum" => valid with { Frame = valid.Frame with { RowDirection = (MapRowDirection)255 } },
            "empty" => valid with { Rects = [], Rings = [] },
            "rect" => valid with { Rects = [new(0, 0, 0, 4)] },
            "ring" => valid with { Rings = [null!] },
            "vertices" => valid with { Rings = [new(null!)] },
            "short" => valid with { Rings = [new(ring.Vertices.Take(2).ToArray())] },
            "address" => valid with { Rings = [new([default, MapLatticeAddress.Corner(15, 0), MapLatticeAddress.Corner(14, 1)])] },
            "far" => valid with { Rects = [new(0, 0, 1_000_001, 4)] },
            _ => valid with { Frame = valid.Frame with { CellUnitMetres = new(int.MaxValue, 1) }, Rects = [new(0, 0, long.MaxValue, 1)] },
        };
        string before = doc.Water.Digest();
        var error = Assert.Throws<MapDocumentException>(() => MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(body with { Domain = domain })));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal(body.Id, error.EntityId);
        Assert.Equal(reason, Assert.Single(error.Findings!).Reason);
        if (defect is "rect" or "ring" or "vertices" or "short" or "address" or "far" or "overflow") Assert.Equal(0, error.Index);
        if (defect == "short") { Assert.Equal(2L, error.ActualCount); Assert.Equal(3L, error.MinimumInteger); }
        if (defect == "empty") Assert.Equal(0L, error.ActualCount);
        Assert.Equal(before, doc.Water.Digest());
        if (defect == "far") body = body with { Domain = valid with { Rects = [new(0, 0, 1_000_000, 4)] } };
        if (defect == "rect") body = body with { Domain = valid with { Rects = [new(0, 0, 1, 4)] } };
        if (defect is "ring" or "vertices" or "short" or "address") body = body with { Domain = valid with { Rings = [ring] } };
        Assert.NotNull(MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(body)).After);
    }

    [Theory]
    [InlineData("null", MapDocumentRefusal.SchemaValueRequired, "Covers")]
    [InlineData("empty", MapDocumentRefusal.WaterNoCoveredSpace, "Covers")]
    [InlineData("cover", MapDocumentRefusal.SchemaValueRequired, "Covers[0]")]
    [InlineData("id", MapDocumentRefusal.SchemaValueRequired, "Covers[0].Id")]
    [InlineData("anchor", MapDocumentRefusal.SchemaValueRequired, "Covers[0].Anchor.SurfaceId")]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate, "Covers[1]")]
    public void Covers_ReportsTheSubjectAndAcceptsOneValidCover(string defect, MapDocumentRefusal reason, string field)
    {
        var doc = WaterFixtures.OceanOverCaves();
        var body = doc.Water!.Bodies.Single(b => b.Id == "ocean");
        var cover = body.Covers[0];
        var covers = defect switch
        {
            "null" => null!,
            "empty" => Array.Empty<MapRecordRef>(),
            "cover" => new MapRecordRef[] { null! },
            "id" => [cover with { Id = " " }],
            "anchor" => [cover with { Anchor = new(" ", 0, 0) }],
            _ => new[] { cover, cover },
        };
        string before = doc.Water.Digest();
        var error = Assert.Throws<MapDocumentException>(() => MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(body with { Covers = covers })));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal(body.Id, error.EntityId);
        if (defect == "duplicate") { Assert.Equal(1, error.Index); Assert.Equal(cover.Id, error.RecordId); }
        if (defect == "empty") Assert.Equal(0L, error.ActualCount);
        Assert.Equal(before, doc.Water.Digest());
        Assert.NotNull(MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(body)).After);
    }

    [Fact]
    public void OverlappingPieces_RetainBothIndicesAndAcceptATouchingBoundary()
    {
        var doc = WaterFixtures.OceanOverCaves();
        var body = doc.Water!.Bodies.Single(b => b.Id == "ocean");
        var overlapping = body with { Domain = body.Domain with { Rects = [new(0, 0, 8, 4), new(6, 0, 12, 4)] } };
        string before = doc.Water.Digest();
        var error = Assert.Throws<MapDocumentException>(() => MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(overlapping)));
        Assert.Equal(MapDocumentRefusal.WaterDomainPiecesOverlap, error.Reason);
        Assert.Equal(body.Id, error.EntityId);
        Assert.Equal(0, error.Index);
        Assert.Equal(1, error.OtherIndex);
        Assert.Equal(before, doc.Water.Digest());
        overlapping = overlapping with { Domain = overlapping.Domain with { Rects = [new(0, 0, 6, 4), new(6, 0, 12, 4)] } };
        Assert.NotNull(MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(overlapping)).After);
    }

    [Fact]
    public void OverlappingBodies_RetainBothIdentitiesAndTheSharedSpace()
    {
        var doc = WaterFixtures.OceanOverCaves();
        var body = WaterFixtures.SecondLakeOverlappingCaveLake();
        string before = doc.Water!.Digest();
        var error = Assert.Throws<MapDocumentException>(() => MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(body)));
        Assert.Equal(MapDocumentRefusal.WaterBodiesOverlap, error.Reason);
        Assert.Equal(new[] { "cave-lake", "second-lake" }, error.SourceIds);
        Assert.Equal("lake-cave", error.SpaceId);
        Assert.Equal(before, doc.Water.Digest());
        body = body with { Domain = body.Domain with { Rects = [new(8, 0, 10, 4)] } };
        Assert.NotNull(MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(body)).After);
    }
}
