using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class CoverageClaimCheckTests
{
    const string Rig = "rig-v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const float Limit = 0.05f;

    // A flat body in the z = 0 plane facing +z: "upper" is the square x 0 to 1, "lower" the square x 1 to 2, two
    // triangles each. The map lists upper first, the reverse of ordinal order, so report order is the map's.
    static readonly Vector3[] FlatPositions =
        [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(0, 1, 0), new(1, 1, 0), new(2, 1, 0)];
    static readonly uint[] FlatIndices = [0, 1, 4, 0, 4, 3, 1, 2, 5, 1, 5, 4];
    static readonly BodyRegionMap FlatMap = new(Rig, ["upper", "lower"], [[new RegionRun(0, 2), new RegionRun(1, 2)]]);
    static readonly CoverageClaimMesh[] FlatBody = [new(FlatPositions, FlatIndices)];

    [Fact]
    public void Claiming_only_the_region_under_the_piece_is_honest()
    {
        CoverageClaimReport report = CoverageClaimCheck.Verify(FlatBody, FlatMap, Piece("upper"), [PlateOverUpper(0.01f)], Limit);

        Assert.True(report.Honest);
        CoverageClaimRegion upper = Assert.Single(report.Regions);
        Assert.Equal("upper", upper.Name);
        Assert.Equal(2, upper.TriangleCount);
        Assert.Equal(0, upper.DegenerateCount);
        Assert.Equal(0, upper.UncoveredCount);
        Assert.Empty(upper.Uncovered);
    }

    [Fact]
    public void Claiming_a_bare_region_reports_every_triangle_of_it_uncovered()
    {
        CoverageClaimReport report = CoverageClaimCheck.Verify(FlatBody, FlatMap, Piece("upper", "lower"), [PlateOverUpper(0.01f)], Limit);

        Assert.False(report.Honest);
        Assert.Equal(new[] { "upper", "lower" }, report.Regions.Select(region => region.Name));
        Assert.Equal(0, report.Regions[0].UncoveredCount);
        CoverageClaimRegion lower = report.Regions[1];
        Assert.Equal(2, lower.TriangleCount);
        Assert.Equal(2, lower.UncoveredCount);
        Assert.Equal(new[] { new CoverageClaimTriangle(0, 2), new CoverageClaimTriangle(0, 3) }, lower.Uncovered);
    }

    [Theory]
    [InlineData("on the skin", true)]
    [InlineData("inside the distance", true)]
    [InlineData("at the distance", true)]
    [InlineData("just past the distance", false)]
    [InlineData("two metres away", false)]
    [InlineData("behind the skin", false)]
    public void Cover_counts_only_from_the_skin_out_to_the_distance(string where, bool covered)
    {
        float height = where switch
        {
            "on the skin" => 0f,
            "inside the distance" => 0.01f,
            "at the distance" => Limit,
            "just past the distance" => MathF.BitIncrement(Limit),
            "two metres away" => 2f,
            _ => -0.01f,
        };

        CoverageClaimReport report = CoverageClaimCheck.Verify(FlatBody, FlatMap, Piece("upper"), [PlateOverUpper(height)], Limit);

        Assert.Equal(covered, report.Honest);
        Assert.Equal(covered ? 0 : 2, report.Regions[0].UncoveredCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_piece_covers_whichever_way_its_triangles_wind(bool facingTheSkin)
    {
        CoverageClaimReport report = CoverageClaimCheck.Verify(FlatBody, FlatMap, Piece("upper"), [PlateOverUpper(0.01f, facingTheSkin)], Limit);

        Assert.True(report.Honest);
    }

    [Fact]
    public void A_piece_with_no_layer_claims_nothing()
    {
        AttachmentSetDefinition unlayered = new([new RigidAttachmentPartDefinition(1, new("parts/rigid.glb", Hash), "root", Matrix4x4.Identity)]);

        CoverageClaimReport report = CoverageClaimCheck.Verify(FlatBody, FlatMap, unlayered, [], Limit);

        Assert.True(report.Honest);
        Assert.Empty(report.Regions);
    }

    [Fact]
    public void A_degenerate_body_triangle_is_counted_and_never_uncovered()
    {
        // A third upper triangle along the x axis has zero area and so no outward normal.
        uint[] indices = [0, 1, 4, 0, 4, 3, 0, 1, 2, 1, 2, 5, 1, 5, 4];
        BodyRegionMap map = new(Rig, ["upper", "lower"], [[new RegionRun(0, 3), new RegionRun(1, 2)]]);

        CoverageClaimReport report = CoverageClaimCheck.Verify([new(FlatPositions, indices)], map, Piece("upper"), [PlateOverUpper(0.01f)], Limit);

        Assert.True(report.Honest);
        CoverageClaimRegion upper = Assert.Single(report.Regions);
        Assert.Equal(3, upper.TriangleCount);
        Assert.Equal(1, upper.DegenerateCount);
        Assert.Equal(0, upper.UncoveredCount);
    }

    [Fact]
    public void An_uncovered_triangle_names_its_body_part()
    {
        Vector3[] square = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)];
        uint[] indices = [0, 1, 3, 0, 3, 2];
        Vector3[] beside = square.Select(position => position + Vector3.UnitX).ToArray();
        BodyRegionMap map = new(Rig, ["upper", "lower"], [[new RegionRun(0, 2)], [new RegionRun(1, 2)]]);

        CoverageClaimReport report = CoverageClaimCheck.Verify([new(square, indices), new(beside, indices)], map,
            Piece("upper", "lower"), [PlateOverUpper(0.01f)], Limit);

        Assert.Equal(0, report.Regions[0].UncoveredCount);
        Assert.Equal(new[] { new CoverageClaimTriangle(1, 0), new CoverageClaimTriangle(1, 1) }, report.Regions[1].Uncovered);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(-0.05f)]
    public void The_distance_must_be_finite_and_positive(float distance)
    {
        Assert.True(CoverageClaimCheck.Verify(FlatBody, FlatMap, Piece("upper"), [PlateOverUpper(0.01f)], Limit).Honest);

        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageClaimCheck.Verify(FlatBody, FlatMap, Piece("upper"), [PlateOverUpper(0.01f)], distance));
    }

    [Theory]
    [InlineData("a second body mesh", CoverageClaimRefusal.BodyMeshCountMismatch)]
    [InlineData("a body triangle short", CoverageClaimRefusal.BodyTriangleCountMismatch)]
    [InlineData("a covered region the body lacks", CoverageClaimRefusal.RegionUnknown)]
    [InlineData("a body index count off three", CoverageClaimRefusal.IndexCountNotTriangles)]
    [InlineData("a piece index count off three", CoverageClaimRefusal.IndexCountNotTriangles)]
    [InlineData("a body index past its positions", CoverageClaimRefusal.IndexOutOfRange)]
    [InlineData("a piece index past its positions", CoverageClaimRefusal.IndexOutOfRange)]
    [InlineData("a body position not a number", CoverageClaimRefusal.PositionNotFinite)]
    [InlineData("a piece position infinite", CoverageClaimRefusal.PositionNotFinite)]
    public void Verify_refuses_input_that_breaks_a_rule(string rule, CoverageClaimRefusal reason)
    {
        Assert.True(Check(null).Honest);

        var error = Assert.Throws<CoverageClaimDataException>(() => Check(rule));

        Assert.IsAssignableFrom<MalformedDataException>(error);
        Assert.Equal(reason, error.Reason);
        CoverageClaimMeshKind kind = rule.Contains("piece", StringComparison.Ordinal) ? CoverageClaimMeshKind.Piece : CoverageClaimMeshKind.Body;
        switch (reason)
        {
            case CoverageClaimRefusal.BodyMeshCountMismatch:
                Assert.Equal(1, error.ExpectedMeshCount);
                Assert.Equal(2, error.ActualMeshCount);
                break;
            case CoverageClaimRefusal.BodyTriangleCountMismatch:
                Assert.Equal(CoverageClaimMeshKind.Body, error.MeshKind);
                Assert.Equal(0, error.MeshIndex);
                Assert.Equal(4, error.ExpectedTriangleCount);
                Assert.Equal(3, error.ActualTriangleCount);
                break;
            case CoverageClaimRefusal.RegionUnknown:
                Assert.Equal("middle", error.RegionName);
                break;
            case CoverageClaimRefusal.IndexCountNotTriangles:
                Assert.Equal(kind, error.MeshKind);
                Assert.Equal(0, error.MeshIndex);
                Assert.Equal(kind == CoverageClaimMeshKind.Body ? 11 : 5, error.IndexCount);
                break;
            case CoverageClaimRefusal.IndexOutOfRange:
                Assert.Equal(kind, error.MeshKind);
                Assert.Equal(0, error.MeshIndex);
                Assert.Equal(4, error.IndexPosition);
                Assert.Equal(99u, error.VertexIndex);
                Assert.Equal(kind == CoverageClaimMeshKind.Body ? 6 : 4, error.VertexCount);
                break;
            default:
                Assert.Equal(kind, error.MeshKind);
                Assert.Equal(0, error.MeshIndex);
                Assert.Equal(2u, error.VertexIndex);
                break;
        }
    }

    // The passing flat body check, with exactly one rule broken when rule is not null.
    static CoverageClaimReport Check(string? rule)
    {
        Vector3[] bodyPositions = (Vector3[])FlatPositions.Clone();
        uint[] bodyIndices = (uint[])FlatIndices.Clone();
        CoverageClaimMesh plate = PlateOverUpper(0.01f);
        Vector3[] piecePositions = plate.Positions.ToArray();
        uint[] pieceIndices = plate.Indices.ToArray();
        AttachmentSetDefinition piece = Piece("upper");
        int bodyMeshes = 1;
        switch (rule)
        {
            case "a second body mesh": bodyMeshes = 2; break;
            case "a body triangle short": bodyIndices = bodyIndices[..9]; break;
            case "a covered region the body lacks": piece = Piece("upper", "middle"); break;
            case "a body index count off three": bodyIndices = bodyIndices[..11]; break;
            case "a piece index count off three": pieceIndices = pieceIndices[..5]; break;
            case "a body index past its positions": bodyIndices[4] = 99; break;
            case "a piece index past its positions": pieceIndices[4] = 99; break;
            case "a body position not a number": bodyPositions[2] = new(float.NaN, 0, 0); break;
            case "a piece position infinite": piecePositions[2] = new(0, float.PositiveInfinity, 0); break;
        }
        CoverageClaimMesh body = new(bodyPositions, bodyIndices);
        return CoverageClaimCheck.Verify(Enumerable.Repeat(body, bodyMeshes).ToArray(), FlatMap, piece, [new(piecePositions, pieceIndices)], Limit);
    }

    // A plate over the upper square only, wide enough to cover its edges, at the given height above the skin. Its
    // triangles face away from the skin unless facingTheSkin, the way a garment's inner surface is wound.
    static CoverageClaimMesh PlateOverUpper(float height, bool facingTheSkin = false)
    {
        Vector3[] positions = [new(-1, -1, height), new(1, -1, height), new(1, 2, height), new(-1, 2, height)];
        uint[] indices = facingTheSkin ? [0, 2, 1, 0, 3, 2] : [0, 1, 2, 0, 2, 3];
        return new(positions, indices);
    }

    static AttachmentSetDefinition Piece(params string[] covers) =>
        new([new RigidAttachmentPartDefinition(1, new("parts/rigid.glb", Hash), "root", Matrix4x4.Identity) { Region = covers[0] }],
            AttachmentLayer.Armour, covers);
}
