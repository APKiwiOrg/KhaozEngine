using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Tests.Attachments.CoverageClaimFixtures;

namespace KhaozEngine.Tests.Attachments;

public class StandoffClaimCheckTests
{
    [Fact]
    public void Honest_garment_reports_the_highest_vertex_and_count_in_each_measured_region()
    {
        AttachmentSetDefinition shape = Piece("upper");
        AttachmentSetDefinition piece = new(shape.Parts, AttachmentLayer.Garment, shape.Covers, shape.Colliders,
            [new("lower", 0.125f), new("upper", 0.0625f)]);
        CoverageClaimMesh mesh = new(new Vector3[]
        {
            new(0.25f, 0.25f, 0.03125f), new(0.75f, 0.25f, 0.0625f), new(0.25f, 0.75f, 0f),
            new(1.25f, 0.25f, 0.0625f), new(1.75f, 0.25f, 0.0625f), new(1.25f, 0.75f, 0.0625f),
        }, new uint[] { 0, 1, 2, 3, 4, 5 });

        StandoffClaimReport report = StandoffClaimCheck.Verify(FlatBody, FlatMap, piece, [mesh]);

        Assert.Empty(report.UnderstatedRegions);
        Assert.Empty(report.UndeclaredRegions);
        Assert.Collection(report.Regions,
            upper => Assert.Equal(new StandoffClaimRegion("upper", 0.0625, 3, 0.0625f), upper),
            lower => Assert.Equal(new StandoffClaimRegion("lower", 0.0625, 3, 0.125f), lower));
    }

    [Theory]
    [InlineData(0.0625f, 0.03125f, "lower", null)]
    [InlineData(0.0625f, null, null, "lower")]
    [InlineData(0f, null, null, null)]
    [InlineData(-0.0625f, null, null, null)]
    public void Only_positive_excess_or_missing_standoff_is_reported(float height, float? declared,
        string? understated, string? undeclared)
    {
        AttachmentSetDefinition shape = Piece("upper");
        AttachmentSetDefinition piece = declared is float metres
            ? WithStandoff(shape, new("upper", 0.0625f), new("lower", metres))
            : WithStandoff(shape, new RegionStandoff("upper", 0.0625f));
        CoverageClaimMesh mesh = new(new Vector3[]
        {
            new(0.25f, 0.25f, 0.0625f), new(0.75f, 0.25f, 0.0625f), new(0.25f, 0.75f, 0.0625f),
            new(1.25f, 0.25f, height), new(1.75f, 0.25f, height), new(1.25f, 0.75f, height),
        }, new uint[] { 0, 1, 2, 3, 4, 5 });

        StandoffClaimReport report = StandoffClaimCheck.Verify(FlatBody, FlatMap, piece, [mesh]);

        Assert.Equal(understated is null ? Array.Empty<string>() : [understated], report.UnderstatedRegions);
        Assert.Equal(undeclared is null ? Array.Empty<string>() : [undeclared], report.UndeclaredRegions);
        Assert.Equal(new StandoffClaimRegion("lower", height, 3, declared), Assert.Single(report.Regions, region => region.Name == "lower"));
    }

    [Theory]
    [InlineData(0.5f, 0.09375f, 0.15625)]
    [InlineData(0.5f, -0.09375f, -0.15625)]
    [InlineData(1.09375f, 0f, 0.15625)]
    public void A_rigid_vertex_uses_the_nearest_body_triangle_even_at_an_edge_or_corner(float y, float z, double height)
    {
        // The first body part sits a metre below the second. The nearest point on the second has x = 2
        // and y clamped to [0, 1]. The offset lengths 0.125 and 0.09375 give a distance of 0.15625.
        Vector3[] lowerSquare = [new(1, 0, 0), new(2, 0, 0), new(2, 1, 0), new(1, 1, 0)];
        Vector3[] below = lowerSquare.Select(position => position - Vector3.UnitZ).ToArray();
        uint[] indices = [0, 1, 2, 0, 2, 3];
        BodyRegionMap body = new(Rig, ["upper", "lower"], [[new RegionRun(0, 2)], [new RegionRun(1, 2)]]);
        AttachmentSetDefinition piece = WithStandoff(Piece("upper"), new RegionStandoff("lower", 0.25f));
        CoverageClaimMesh rigid = new(new[] { new Vector3(2.125f, y, z) }, ReadOnlyMemory<uint>.Empty) { PartId = 1 };

        StandoffClaimReport report = StandoffClaimCheck.Verify([new(below, indices), new(lowerSquare, indices)], body, piece, [rigid]);

        Assert.Equal(new StandoffClaimRegion("lower", height, 1, 0.25f), Assert.Single(report.Regions));
    }

    [Fact]
    public void A_zero_area_triangle_contributes_its_segment_distance_without_a_back_side()
    {
        Vector3[] positions = [new(0, 0, 0), new(0.5f, 0, 0), new(1, 0, 0)];
        BodyRegionMap body = new(Rig, ["upper"], [[new RegionRun(0, 1)]]);
        CoverageClaimMesh pieceMesh = new(new[] { new Vector3(0.75f, 0, -0.0625f) }, ReadOnlyMemory<uint>.Empty);

        StandoffClaimReport report = StandoffClaimCheck.Verify([new(positions, new uint[] { 0, 1, 2 })], body,
            Piece("upper"), [pieceMesh]);

        Assert.Equal(new StandoffClaimRegion("upper", 0.0625, 1, null), Assert.Single(report.Regions));
    }

    [Fact]
    public void A_body_without_triangles_produces_no_regional_measurements()
    {
        BodyRegionMap body = new(Rig, ["upper"], []);
        AttachmentSetDefinition piece = WithStandoff(Piece("upper"), new RegionStandoff("upper", 0.0625f));

        StandoffClaimReport report = StandoffClaimCheck.Verify([], body, piece, [PlateOverUpper(0.0625f)]);

        Assert.Empty(report.Regions);
        Assert.Empty(report.UnderstatedRegions);
        Assert.Empty(report.UndeclaredRegions);
    }

    [Fact]
    public void Only_fixed_vertices_of_a_chained_mesh_are_measured()
    {
        AttachmentSetDefinition piece = WithStandoff(ChainedPiece(), new RegionStandoff("upper", 0.0625f));
        CoverageClaimMesh chained = new(new Vector3[]
        {
            new(0.25f, 0.25f, 0.0625f), new(0.75f, 0.25f, 0.03125f), new(1.25f, 0.75f, 2f),
        }, new uint[] { 0, 1, 2 }) { PartId = 12, Swinging = new[] { false, false, true } };
        CoverageClaimMesh rigid = new(new[] { new Vector3(0.5f, 0.5f, 0.03125f) }, ReadOnlyMemory<uint>.Empty) { PartId = 3 };

        StandoffClaimReport report = StandoffClaimCheck.Verify(FlatBody, FlatMap, piece, [rigid, chained]);

        Assert.Equal(new StandoffClaimRegion("upper", 0.0625, 3, 0.0625f), Assert.Single(report.Regions));
        Assert.Empty(report.UnderstatedRegions);
        Assert.Empty(report.UndeclaredRegions);
    }

    [Theory]
    [InlineData(0u, CoverageClaimRefusal.PieceMeshPartIdMissing)]
    [InlineData(99u, CoverageClaimRefusal.PieceMeshPartIdUnknown)]
    [InlineData(3u, CoverageClaimRefusal.PieceMeshPartIdDuplicate)]
    [InlineData(12u, CoverageClaimRefusal.SwingMaskMissing)]
    [InlineData(12u, CoverageClaimRefusal.PiecePartMeshMissing)]
    public void Invalid_motion_evidence_refuses_with_the_coverage_reason(uint partId, CoverageClaimRefusal reason)
    {
        AttachmentSetDefinition piece = ChainedPiece();
        CoverageClaimMesh chained = PlateOverUpper(0.0625f) with { PartId = 12, Swinging = new bool[4] };
        CoverageClaimMesh rigid = PlateOverUpper(0.0625f) with { PartId = 3 };
        CoverageClaimMesh[] invalid = reason switch
        {
            CoverageClaimRefusal.SwingMaskMissing => [rigid, chained with { Swinging = default }],
            CoverageClaimRefusal.PiecePartMeshMissing => [rigid],
            _ => [rigid, chained, rigid with { PartId = partId }],
        };
        Assert.NotEmpty(StandoffClaimCheck.Verify(FlatBody, FlatMap, piece, [rigid, chained]).Regions);
        var coverage = Assert.Throws<CoverageClaimDataException>(
            () => CoverageClaimCheck.Verify(FlatBody, FlatMap, piece, invalid, 2f));

        var standoff = Assert.Throws<CoverageClaimDataException>(
            () => StandoffClaimCheck.Verify(FlatBody, FlatMap, piece, invalid));

        Assert.Equal(reason, coverage.Reason);
        Assert.Equal(coverage.Reason, standoff.Reason);
        Assert.Equal(coverage.MeshKind, standoff.MeshKind);
        Assert.Equal(coverage.MeshIndex, standoff.MeshIndex);
        Assert.Equal(coverage.PartId, standoff.PartId);
        Assert.Equal(coverage.MeshKey, standoff.MeshKey);
    }

    [Theory]
    [MemberData(nameof(InvalidGeometry), MemberType = typeof(CoverageClaimFixtures))]
    public void Invalid_geometry_or_coverage_refuses_with_the_coverage_reason(string rule, CoverageClaimRefusal reason)
    {
        var (validBody, validPiece, validMeshes) = GeometryInputs(null);
        Assert.NotEmpty(StandoffClaimCheck.Verify(validBody, FlatMap, validPiece, validMeshes).Regions);
        var (bodyMeshes, piece, pieceMeshes) = GeometryInputs(rule);
        var coverage = Assert.Throws<CoverageClaimDataException>(
            () => CoverageClaimCheck.Verify(bodyMeshes, FlatMap, piece, pieceMeshes, 2f));

        var standoff = Assert.Throws<CoverageClaimDataException>(
            () => StandoffClaimCheck.Verify(bodyMeshes, FlatMap, piece, pieceMeshes));

        Assert.Equal(reason, coverage.Reason);
        Assert.Equal(coverage.Reason, standoff.Reason);
        Assert.Equal(coverage.MeshKind, standoff.MeshKind);
        Assert.Equal(coverage.MeshIndex, standoff.MeshIndex);
        Assert.Equal(coverage.ExpectedMeshCount, standoff.ExpectedMeshCount);
        Assert.Equal(coverage.ActualMeshCount, standoff.ActualMeshCount);
        Assert.Equal(coverage.ExpectedTriangleCount, standoff.ExpectedTriangleCount);
        Assert.Equal(coverage.ActualTriangleCount, standoff.ActualTriangleCount);
        Assert.Equal(coverage.IndexCount, standoff.IndexCount);
        Assert.Equal(coverage.IndexPosition, standoff.IndexPosition);
        Assert.Equal(coverage.VertexIndex, standoff.VertexIndex);
        Assert.Equal(coverage.VertexCount, standoff.VertexCount);
        Assert.Equal(coverage.RegionName, standoff.RegionName);
    }

    [Theory]
    [InlineData(true, "bodyMeshes")]
    [InlineData(false, "pieceMeshes")]
    public void A_nonempty_swing_mask_must_match_the_vertex_count(bool bodyMask, string parameter)
    {
        CoverageClaimMesh plate = PlateOverUpper(0.0625f);
        AttachmentSetDefinition piece = Piece("upper");
        CoverageClaimMesh target = bodyMask ? FlatBody[0] : plate;
        StandoffClaimReport Check(int flagCount)
        {
            CoverageClaimMesh masked = target with { Swinging = new bool[flagCount] };
            return StandoffClaimCheck.Verify(bodyMask ? [masked] : FlatBody, FlatMap, piece, bodyMask ? [plate] : [masked]);
        }

        Assert.NotEmpty(Check(target.Positions.Length).Regions);
        Assert.Throws<ArgumentException>(parameter, () => Check(target.Positions.Length - 1));
    }

    [Fact]
    public void A_standoff_claim_for_an_unknown_body_region_refuses()
    {
        AttachmentSetDefinition shape = Piece("upper");
        CoverageClaimMesh plate = PlateOverUpper(0.0625f);
        var known = WithStandoff(shape, new RegionStandoff("upper", 0.0625f));
        var unknown = WithStandoff(shape, new RegionStandoff("middle", 0.0625f));
        Assert.NotEmpty(StandoffClaimCheck.Verify(FlatBody, FlatMap, known, [plate]).Regions);

        var error = Assert.Throws<CoverageClaimDataException>(
            () => StandoffClaimCheck.Verify(FlatBody, FlatMap, unknown, [plate]));

        Assert.Equal(CoverageClaimRefusal.RegionUnknown, error.Reason);
        Assert.Equal("middle", error.RegionName);
    }

    static AttachmentSetDefinition WithStandoff(AttachmentSetDefinition piece, params RegionStandoff[] standoff) =>
        new(piece.Parts, piece.Layer, piece.Covers, piece.Colliders, standoff);
}
