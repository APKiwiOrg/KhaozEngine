using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public sealed class MapWaterDomainRefusalTests
{
    static readonly MapLatticeFrame Frame = new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
    static MapWaterDomainDoc Valid => new(Frame, [new(0, 0, 2, 1)], []);

    [Theory]
    [InlineData("Rects")]
    [InlineData("Rings")]
    [InlineData("Frame")]
    public void MissingDomainComponent_NamesTheFirstRequiredField(string field)
    {
        var doc = field == "Rects" ? Valid with { Rects = null! }
            : field == "Rings" ? Valid with { Rings = null! } : Valid with { Frame = null! };
        var finding = Refused(doc);
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, finding.Reason);
        Assert.Equal(field, finding.FieldPath);
        Assert.NotEmpty(MapWaterDomain.Compile(Valid).Pieces);
    }

    [Fact]
    public void EmptyDomain_ReportsItsPieceCountAndAcceptsOnePiece()
    {
        var finding = Refused(Valid with { Rects = [] });
        Assert.Equal(MapDocumentRefusal.WaterDomainEmpty, finding.Reason);
        Assert.Equal("Domain", finding.FieldPath);
        Assert.Equal(0L, finding.ActualCount);
        Assert.Single(MapWaterDomain.Compile(Valid).Pieces);
    }

    [Theory]
    [InlineData("X", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("Y", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("Z", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("YawRadians", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("Scale", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("ZeroScale", MapDocumentRefusal.ValueOutsideRange)]
    public void PlacementAdmission_NamesTheFailedComponent(string field, MapDocumentRefusal reason)
    {
        var placement = new MapWaterPlacement(Vector3.Zero, 0, 1);
        var refused = field switch
        {
            "X" => placement with { Position = new(float.NaN, 0, 0) },
            "Y" => placement with { Position = new(0, float.NaN, 0) },
            "Z" => placement with { Position = new(0, 0, float.NaN) },
            "YawRadians" => placement with { Yaw = float.NaN },
            "Scale" => placement with { Scale = float.NaN },
            _ => placement with { Scale = 0 },
        };
        var finding = Refused(Valid, refused);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("Placement." + (field == "ZeroScale" ? "Scale" : field), finding.FieldPath);
        if (field == "ZeroScale") { Assert.Equal(0d, finding.ActualNumber); Assert.Equal(0d, finding.MinimumNumber); }
        else Assert.True(double.IsNaN(finding.ActualNumber!.Value));
        Assert.Single(MapWaterDomain.Compile(Valid, placement).Pieces);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyRectangle_NamesItsStoredIndex(bool x)
    {
        var finding = Refused(Valid with { Rects = [x ? new(0, 0, 0, 1) : new(0, 0, 2, 0)] });
        Assert.Equal(MapDocumentRefusal.BoundsInvalid, finding.Reason);
        Assert.Equal("Rects[0]", finding.FieldPath);
        Assert.Equal("rect", finding.SubjectKind);
        Assert.Equal(0, finding.Index);
        Assert.Single(MapWaterDomain.Compile(Valid).Pieces);
    }

    [Theory]
    [InlineData("ring", MapDocumentRefusal.SchemaValueRequired, "Rings[0]")]
    [InlineData("vertices", MapDocumentRefusal.SchemaValueRequired, "Rings[0].Vertices")]
    [InlineData("count", MapDocumentRefusal.ValueOutsideRange, "Rings[0].Vertices")]
    public void RingAdmission_DistinguishesMissingValuesFromCardinality(string fault, MapDocumentRefusal reason, string path)
    {
        var ring = fault == "ring" ? null! : fault == "vertices" ? new MapWaterRing(null!) : Ring((0, 0), (1, 0));
        var finding = Refused(new(Frame, [], [ring]));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(path, finding.FieldPath);
        Assert.Equal(0, finding.Index);
        if (fault == "count") { Assert.Equal(2L, finding.ActualCount); Assert.Equal(3L, finding.MinimumInteger); }
        Assert.Single(MapWaterDomain.Compile(new(Frame, [], [Ring((0, 0), (1, 0), (0, 1))])).Pieces);
    }

    [Theory]
    [InlineData(false, MapDocumentRefusal.WaterDomainZeroArea)]
    [InlineData(true, MapDocumentRefusal.WaterDomainSelfIntersects)]
    public void RingGeometry_NamesItsStoredSubject(bool crossing, MapDocumentRefusal reason)
    {
        var ring = crossing ? Ring((0, 0), (2, 2), (0, 2), (2, 0)) : Ring((0, 0), (1, 0), (2, 0));
        var finding = Refused(new(Frame, [], [ring]));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("Rings[0]", finding.FieldPath);
        Assert.Equal("ring", finding.SubjectKind);
        Assert.Equal(0, finding.Index);
        Assert.Single(MapWaterDomain.Compile(new(Frame, [], [crossing
            ? Ring((0, 0), (2, 0), (2, 2), (0, 2)) : Ring((0, 0), (1, 0), (0, 1))])).Pieces);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DomainCountBudget_ReportsTheActualCountAndAcceptsItsCap(bool vertices)
    {
        var rects = Enumerable.Range(0, MapWaterDomain.MaxPieces - (vertices ? 1 : 0))
            .Select(i => new MapWaterRect(i, 0, i + 1, 1)).ToArray();
        var doc = new MapWaterDomainDoc(Frame, rects, [vertices
            ? Ring((0, 2), (2, 2), (2, 3), (1, 4), (0, 3)) : Ring((0, 2), (1, 2), (0, 3))]);
        if (!vertices) doc = new(Frame, [], Enumerable.Range(0, MapWaterDomain.MaxPieces + 1)
            .Select(i => Ring((i * 2, 0), (i * 2 + 1, 0), (i * 2, 1))).ToArray());
        var errors = new List<string>();
        var findings = new List<MapDocumentFinding>();
        Assert.False(MapWaterDomain.WithinLimits(doc, errors, findings));
        var finding = Assert.Single(findings);
        Assert.Single(errors);
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, finding.Reason);
        Assert.Equal(vertices ? "WaterDomainVertices" : "WaterDomainPieces", finding.Budget);
        long limit = vertices ? MapWaterDomain.MaxPieceVertices : MapWaterDomain.MaxPieces;
        Assert.Equal(limit + 1, finding.ActualCount);
        Assert.Equal(limit, finding.Limit);
        var control = vertices ? doc with { Rings = [Ring((0, 2), (1, 2), (1, 3), (0, 3))] }
            : doc with { Rings = doc.Rings.Take(MapWaterDomain.MaxPieces).ToArray() };
        errors.Clear(); findings.Clear();
        Assert.True(MapWaterDomain.WithinLimits(control, errors, findings));
        Assert.Empty(errors);
        Assert.Empty(findings);
    }

    [Fact]
    public void UnrepresentableCorner_RetainsThePieceAndCorner()
    {
        // 2 * float.MaxValue overflows on rect 0's second corner. No cancellation or subnormal is involved.
        var finding = Refused(Valid, new(Vector3.Zero, 0, float.MaxValue));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, finding.Reason);
        Assert.Equal("Rects[0]", finding.FieldPath);
        Assert.Equal(0, finding.Index);
        Assert.Equal(1, finding.OtherIndex);
        Assert.Single(MapWaterDomain.Compile(Valid, new(Vector3.Zero, 0, 1)).Pieces);
    }

    [Fact]
    public void ExactCornerOverflow_RetainsTheCurrentPiece()
    {
        var doc = Valid with { Frame = Frame with { CellUnitMetres = new(2, 1) },
            Rects = [new(long.MaxValue - 1, 0, long.MaxValue, 1)] };
        var finding = Refused(doc);
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, finding.Reason);
        Assert.Equal("Rects[0]", finding.FieldPath);
        Assert.Equal(0, finding.Index);
        Assert.Single(MapWaterDomain.Compile(doc with { Rects = [new(0, 0, 1, 1)] }).Pieces);
    }

    [Fact]
    public void ForwardedLatticeRefusal_KeepsItsOriginalCauseAndIdentifiers()
    {
        var doc = new MapWaterDomainDoc(Frame, [], [new([default, MapLatticeAddress.Corner(1, 0), MapLatticeAddress.Corner(0, 1)])]);
        var error = Assert.Throws<MapDocumentException>(() => MapWaterDomain.Compile(doc));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
        Assert.Equal("denominator", error.FieldPath);
        Assert.Equal(0L, error.ActualInteger);
        var cause = Assert.IsType<MapDocumentException>(error.InnerException);
        Assert.Equal(error.Reason, cause.Reason);
        Assert.Equal(error.FieldPath, cause.FieldPath);
        Assert.Equal(error.ActualInteger, cause.ActualInteger);
        Assert.Single(MapWaterDomain.Compile(doc with { Rings = [Ring((0, 0), (1, 0), (0, 1))] }).Pieces);
    }

    [Fact]
    public void FailedTriangulation_RetainsItsStoredPieceIdentity()
    {
        var domain = MapWaterDomain.Compile(new(Frame, [new(3, 0, 4, 1)], [Ring((0, 0), (2, 0), (2, 1), (1, 1), (1, 2), (0, 2))]));
        var error = Assert.Throws<MapDocumentException>(() => domain.RejectTriangulationForTests(1));
        Assert.Equal(MapDocumentRefusal.WaterDomainTriangulationFailed, error.Reason);
        Assert.Equal("Rings[0]", error.FieldPath);
        Assert.Equal("ring", error.SubjectKind);
        Assert.Equal(0, error.Index);
        Assert.NotEmpty(domain.ConvexParts(1, new()));
    }

    static MapDocumentFinding Refused(MapWaterDomainDoc doc, MapWaterPlacement? placement = null)
    {
        var errors = new List<string>();
        var findings = new List<MapDocumentFinding>();
        Assert.Null(MapWaterDomain.TryCompile(doc, placement, errors, new(), findings));
        var finding = Assert.Single(findings);
        Assert.Equal(new[] { finding.Message }, errors);
        return finding;
    }

    static MapWaterRing Ring(params (long X, long Z)[] points) =>
        new(points.Select(p => MapLatticeAddress.Corner(p.X, p.Z)).ToArray());
}
