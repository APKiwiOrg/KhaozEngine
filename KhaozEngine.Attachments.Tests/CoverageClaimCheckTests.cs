using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Tests.Attachments.CoverageClaimFixtures;

namespace KhaozEngine.Tests.Attachments;

public class CoverageClaimCheckTests
{
    const float Limit = 0.05f;

    [Theory]
    [InlineData(null, 0u)]
    [InlineData(null, 99u)]
    [InlineData(false, 0u)]
    [InlineData(true, 0u)]
    public void A_plate_covers_its_region_only_when_it_is_fixed(bool? swinging, uint partId)
    {
        CoverageClaimMesh plate = PlateOverUpper(0.01f) with { PartId = partId };
        if (swinging is bool flag)
            plate = plate with { Swinging = new[] { flag, flag, flag, flag } };
        CoverageClaimReport report = CoverageClaimCheck.Verify(FlatBody, FlatMap, Piece("upper"), [plate], Limit);

        Assert.Equal(swinging != true, report.Honest);
        Assert.Equal(swinging == true ? 2 : 0, report.IgnoredSwingingTriangleCount);
        CoverageClaimRegion upper = Assert.Single(report.Regions);
        Assert.Equal("upper", upper.Name);
        Assert.Equal(2, upper.TriangleCount);
        Assert.Equal(0, upper.DegenerateCount);
        Assert.Equal(swinging == true
            ? new[] { new CoverageClaimTriangle(0, 0), new CoverageClaimTriangle(0, 1) }
            : Array.Empty<CoverageClaimTriangle>(), upper.Uncovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_plate_covers_its_fixed_half_when_one_vertex_moves_the_other_half(bool claimLower)
    {
        Vector3[] positions = FlatPositions.Select(position => position + new Vector3(0, 0, 0.01f)).ToArray();
        // Both lower triangles share vertex 5. All other vertices stay fixed.
        var plate = new CoverageClaimMesh(positions, FlatIndices) { Swinging = new[] { false, false, false, false, false, true } };
        AttachmentSetDefinition piece = claimLower ? Piece("upper", "lower") : Piece("upper");

        CoverageClaimReport report = CoverageClaimCheck.Verify(FlatBody, FlatMap, piece, [plate], Limit);

        Assert.Equal(!claimLower, report.Honest);
        Assert.Equal(2, report.IgnoredSwingingTriangleCount);
        Assert.Equal(claimLower ? new[] { "upper", "lower" } : new[] { "upper" }, report.Regions.Select(region => region.Name));
        Assert.Empty(report.Regions[0].Uncovered);
        if (claimLower)
            Assert.Equal(new[] { new CoverageClaimTriangle(0, 2), new CoverageClaimTriangle(0, 3) }, report.Regions[1].Uncovered);
    }

    [Fact]
    public void Verify_requires_swing_evidence_for_the_chained_part_of_a_format_four_piece()
    {
        // #1879: reordering meshes must not let a rigid plate's mask stand in for a chained plate's mask.
        AttachmentSetDefinition piece = ChainedPiece();
        CoverageClaimMesh fixedPlate = PlateOverUpper(0.02f) with { PartId = 3, Swinging = new bool[4] };
        CoverageClaimMesh chainedPlate = PlateOverUpper(0.01f) with { PartId = 12 };
        CoverageClaimMesh masked = chainedPlate with { Swinging = new[] { true, true, true, true } };
        Assert.True(CoverageClaimCheck.Verify(FlatBody, FlatMap, piece, [masked, fixedPlate], Limit).Honest);

        var error = Assert.Throws<CoverageClaimDataException>(() =>
            CoverageClaimCheck.Verify(FlatBody, FlatMap, piece, [chainedPlate, fixedPlate], Limit));

        Assert.Equal(CoverageClaimRefusal.SwingMaskMissing, error.Reason);
        Assert.Equal(CoverageClaimMeshKind.Piece, error.MeshKind);
        Assert.Equal(0, error.MeshIndex);
        Assert.Equal(12u, error.PartId);
        Assert.Equal("parts/chain.glb", error.MeshKey);
    }

    [Fact]
    public void Verify_requires_one_mesh_per_definition_part_to_locate_swing_evidence()
    {
        // #1879: a motion-bearing piece supplies geometry for every part, including rigid parts.
        AttachmentSetDefinition piece = ChainedPiece();
        CoverageClaimMesh fixedPlate = PlateOverUpper(Limit * 2) with { PartId = 3 };
        CoverageClaimMesh masked = PlateOverUpper(0.01f) with { PartId = 12, Swinging = new[] { true, true, true, true } };
        Assert.False(CoverageClaimCheck.Verify(FlatBody, FlatMap, piece, [fixedPlate, masked], Limit).Honest);

        var error = Assert.Throws<CoverageClaimDataException>(() =>
            CoverageClaimCheck.Verify(FlatBody, FlatMap, piece, [masked], Limit));

        Assert.Equal(CoverageClaimRefusal.PiecePartMeshMissing, error.Reason);
        Assert.Equal(CoverageClaimMeshKind.Piece, error.MeshKind);
        Assert.Null(error.MeshIndex);
        Assert.Equal(3u, error.PartId);
        Assert.Equal("parts/fixed.glb", error.MeshKey);
    }

    [Theory]
    [InlineData(0u, CoverageClaimRefusal.PieceMeshPartIdMissing, null)]
    [InlineData(99u, CoverageClaimRefusal.PieceMeshPartIdUnknown, null)]
    [InlineData(12u, CoverageClaimRefusal.PieceMeshPartIdDuplicate, "parts/chain.glb")]
    public void Verify_refuses_invalid_piece_mesh_part_ids(uint partId, CoverageClaimRefusal reason, string? meshKey)
    {
        // #1879: each mesh in a motion-bearing piece identifies a different definition part.
        AttachmentSetDefinition piece = ChainedPiece();
        CoverageClaimMesh fixedPlate = PlateOverUpper(0.02f) with { PartId = 3, Swinging = new bool[4] };
        CoverageClaimMesh masked = PlateOverUpper(0.01f) with { PartId = 12, Swinging = new[] { true, true, true, true } };
        Assert.True(CoverageClaimCheck.Verify(FlatBody, FlatMap, piece, [masked, fixedPlate], Limit).Honest);

        var error = Assert.Throws<CoverageClaimDataException>(() =>
            CoverageClaimCheck.Verify(FlatBody, FlatMap, piece, [masked, fixedPlate with { PartId = partId }], Limit));

        Assert.Equal(reason, error.Reason);
        Assert.Equal(CoverageClaimMeshKind.Piece, error.MeshKind);
        Assert.Equal(1, error.MeshIndex);
        Assert.Equal(partId, error.PartId);
        Assert.Equal(meshKey, error.MeshKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verify_refuses_a_nonempty_swing_mask_with_the_wrong_vertex_count(bool bodyMask)
    {
        CoverageClaimMesh plate = PlateOverUpper(0.01f);
        AttachmentSetDefinition piece = Piece("upper");
        CoverageClaimMesh maskTarget = bodyMask ? FlatBody[0] : plate;
        CoverageClaimReport VerifyWithMask(bool[] flags)
        {
            CoverageClaimMesh masked = maskTarget with { Swinging = flags };
            return CoverageClaimCheck.Verify(bodyMask ? [masked] : FlatBody, FlatMap, piece, bodyMask ? [plate] : [masked], Limit);
        }

        Assert.True(VerifyWithMask(new bool[maskTarget.Positions.Length]).Honest);
        Assert.Throws<ArgumentException>(bodyMask ? "bodyMeshes" : "pieceMeshes",
            () => VerifyWithMask(new bool[maskTarget.Positions.Length - 1]));
    }

    [Fact]
    public void An_empty_swing_mask_keeps_a_mesh_equal_to_one_without_a_mask()
    {
        CoverageClaimMesh plate = PlateOverUpper(0.01f);
        CoverageClaimMesh emptyMask = plate with { PartId = 0, Swinging = Array.Empty<bool>() };
        CoverageClaimMesh flagged = plate with { Swinging = new bool[plate.Positions.Length] };

        Assert.Equal(plate, emptyMask);
        Assert.Equal(plate.GetHashCode(), emptyMask.GetHashCode());
        Assert.NotEqual(plate, flagged);
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
    [InlineData(float.Epsilon, true)]
    [InlineData(float.MaxValue, true)]
    [InlineData(0f, false)]
    [InlineData(-0.05f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void The_distance_must_be_finite_and_positive(float distance, bool admitted)
    {
        CoverageClaimReport Check() => CoverageClaimCheck.Verify(FlatBody, FlatMap, Piece("upper"), [PlateOverUpper(0f)], distance);

        if (admitted)
            Assert.True(Check().Honest);
        else
            Assert.Throws<ArgumentOutOfRangeException>(() => Check());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_ray_along_a_mirrored_seam_hits_the_piece_whichever_corner_leads(bool seamVertexFirst)
    {
        // A body triangle with its apex on the mirror plane x = 0 and a mirrored base, so its centroid ray lies in that
        // plane. The piece is mirrored too, its two halves sharing a seam edge on x = 0 about a centimetre out. With the
        // off seam vertex first, rounding once pushed the hit just outside both halves and the claim read dishonest.
        Vector3[] body = [new(0f, 1.07432425f, 0.189967826f), new(-0.0392811075f, 1.06269586f, 0.183984891f), new(0.0392811075f, 1.06269586f, 0.183984891f)];
        BodyRegionMap map = new(Rig, ["upper"], [[new RegionRun(0, 1)]]);
        Vector3[] piece =
        [
            new(0.0839095637f, 1.06517303f, 0.199883938f), new(-0.0839095637f, 1.06517303f, 0.199883938f),
            new(0f, 1.1243242f, 0.202806234f), new(0f, 1.01269591f, 0.195597202f),
        ];
        uint[] indices = seamVertexFirst ? [2, 3, 0, 3, 2, 1] : [0, 2, 3, 1, 3, 2];

        CoverageClaimReport report = CoverageClaimCheck.Verify([new(body, new uint[] { 0, 1, 2 })], map, Piece("upper"), [new(piece, indices)], Limit);

        Assert.True(report.Honest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_ray_through_a_shared_piece_vertex_hits_the_piece_whichever_corner_leads(bool sharedVertexFirst)
    {
        // The body triangle's centroid is the origin, right under the vertex four piece triangles share.
        Vector3[] body = [new(-1, -1, 0), new(2, -1, 0), new(-1, 2, 0)];
        BodyRegionMap map = new(Rig, ["upper"], [[new RegionRun(0, 1)]]);
        Vector3[] piece = [new(0, 0, 0.01f), new(1, 0, 0.01f), new(0, 1, 0.01f), new(-1, 0, 0.01f), new(0, -1, 0.01f)];
        uint[] indices = sharedVertexFirst
            ? [0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1]
            : [1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1, 0];

        CoverageClaimReport report = CoverageClaimCheck.Verify([new(body, new uint[] { 0, 1, 2 })], map, Piece("upper"), [new(piece, indices)], Limit);

        Assert.True(report.Honest);
    }

    [Fact]
    public void A_ray_past_the_piece_outline_misses()
    {
        // The plate stops at x = 0.6, a clear margin short of the first upper triangle's centroid at x = 2/3.
        CoverageClaimReport report = CoverageClaimCheck.Verify(FlatBody, FlatMap, Piece("upper"), [PlateOverUpper(0.01f, right: 0.6f)], Limit);

        Assert.Equal(new[] { new CoverageClaimTriangle(0, 0) }, report.Regions[0].Uncovered);
    }

    [Theory]
    [MemberData(nameof(InvalidGeometry), MemberType = typeof(CoverageClaimFixtures))]
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

    static CoverageClaimReport Check(string? rule)
    {
        var (bodyMeshes, piece, pieceMeshes) = GeometryInputs(rule);
        return CoverageClaimCheck.Verify(bodyMeshes, FlatMap, piece, pieceMeshes, Limit);
    }
}
