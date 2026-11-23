using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Attachments;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Attachments.CoverageClaimFixtures;

namespace KhaozEngine.Tests.Attachments;

public class LiftFootprintMeasureTests(ITestOutputHelper output)
{
    [Fact]
    public void The_raised_hem_changes_only_its_own_cells_on_the_body_triangle()
    {
        LiftFootprint measured = LiftFootprintMeasure.Measure(FootprintBody, FootprintMap, Piece("upper"), [HemGarment()]);
        Assert.Equal(FootprintMap.Sha256, measured.BodyRegionMapSha256);
        Assert.Equal(1, measured.BodyTriangleCount);

        LiftFootprintPart part = Assert.Single(measured.Parts);
        Assert.Equal(1u, part.PartId);
        Assert.Equal(2, part.TriangleCount);
        LiftFootprintTriangle panel = part.Triangles[0];
        LiftFootprintTriangle hem = part.Triangles[1];
        int near = BodyCell.At(0, 0.25, 0.055).Key;
        int far = BodyCell.At(0, 0.2, 0.65).Key;

        Assert.Equal(0.03125, HeightAt(hem, near));
        Assert.Equal(0.01563, HeightAt(panel, near));
        Assert.Equal(0.01563, HeightAt(panel, far));
        Assert.DoesNotContain(far, hem.CellKeys.ToArray());
        Assert.Equal(0.03125, Assert.Single(measured.RegionHeights(FootprintMap)));
    }

    [Fact]
    public void A_triangle_with_one_swinging_vertex_records_cells_without_heights()
    {
        CoverageClaimMesh swinging = SmallFootprintTriangle(12, 0.5f) with { Swinging = new[] { false, true, false } };
        LiftFootprint measured = LiftFootprintMeasure.Measure(FootprintBody, FootprintMap, ChainedPiece(),
            [swinging, SmallFootprintTriangle(3, 0.015625f)]);

        Assert.Collection(measured.Parts, part => Assert.Equal(3u, part.PartId), part => Assert.Equal(12u, part.PartId));
        LiftFootprintTriangle triangle = Assert.Single(measured.PartById(12)!.Triangles);
        Assert.True(triangle.Swinging);
        Assert.Equal(0, Assert.Single(triangle.CellKeys.ToArray()));
        Assert.True(triangle.HeightUnits.IsEmpty);
    }

    [Theory]
    [InlineData(0.03125f, 0.03125)]
    [InlineData(-0.03125f, 0)]
    public void A_rigid_part_records_its_local_cell_including_at_negative_signed_height(float height, double expected)
    {
        var (bodyMeshes, body) = RidgeBody(0);
        // The two coincident up-facing triangles are eligible. The earliest is flattened index 1.
        CoverageClaimMesh mesh = Triangle(1, new(-0.03125f, 0.03125f, height / 2), new(-0.0625f, 0.03125f, height),
            new(-0.03125f, 0.0625f, height / 2));
        LiftFootprint measured = LiftFootprintMeasure.Measure(bodyMeshes, body, Piece("upper"), [mesh]);

        Assert.Equal(bodyMeshes.Length, measured.BodyTriangleCount);
        LiftFootprintTriangle triangle = Assert.Single(Assert.Single(measured.Parts).Triangles);
        Assert.False(triangle.Swinging);
        Assert.Equal(new BodyCell(1, 0), BodyCell.FromKey(Assert.Single(triangle.CellKeys.ToArray())));
        Assert.Equal(expected, triangle.HeightMetres(0));
    }

    [Fact]
    public void Measurement_stores_the_wound_face_normal_of_every_referenced_body_triangle()
    {
        var (bodyMeshes, body) = RidgeBody(0);
        CoverageClaimMesh patch = new(new Vector3[]
        {
            new(-0.125f, 0.125f, 0.03125f), new(-0.25f, 0.125f, 0.03125f), new(-0.125f, 0.25f, 0.03125f),
            new(0.03125f, 0.125f, -0.125f), new(0.03125f, 0.25f, -0.125f), new(0.03125f, 0.125f, -0.25f),
        }, new uint[] { 0, 1, 2, 3, 4, 5 })
        {
            PartId = 1,
            Swinging = new[] { false, false, false, true, false, false },
        };

        LiftFootprint measured = LiftFootprintMeasure.Measure(bodyMeshes, body, Piece("upper"), [patch]);

        Assert.Equal(new LiftFootprintBodyNormal[] { new(1, Vector3.UnitZ), new(3, Vector3.UnitX) }, measured.BodyNormals);
    }

    [Fact]
    public void A_body_with_only_degenerate_triangles_keeps_the_piece_triangle_but_has_no_cells()
    {
        CoverageClaimMesh collapsed = FootprintBody[0] with { Indices = new uint[] { 0, 0, 0 } };
        LiftFootprint measured = LiftFootprintMeasure.Measure([collapsed], FootprintMap, Piece("upper"),
            [SmallFootprintTriangle(1, 0.03125f)]);

        Assert.Equal(1, measured.BodyTriangleCount);
        Assert.Empty(measured.BodyNormals);
        LiftFootprintTriangle triangle = Assert.Single(Assert.Single(measured.Parts).Triangles);
        Assert.True(triangle.CellKeys.IsEmpty);
    }

    [Theory]
    [InlineData(CoverageClaimRefusal.FootprintMeshPartIdMissing, 0u, 0)]
    [InlineData(CoverageClaimRefusal.FootprintMeshPartIdUnknown, 2u, 0)]
    [InlineData(CoverageClaimRefusal.FootprintMeshPartIdDuplicate, 1u, 1)]
    [InlineData(CoverageClaimRefusal.FootprintPartMeshMissing, 1u, null)]
    public void Every_part_needs_one_mesh_with_its_definition_id(CoverageClaimRefusal reason, uint partId, int? meshIndex)
    {
        AttachmentSetDefinition piece = Piece("upper");
        CoverageClaimMesh mesh = SmallFootprintTriangle(1, 0.015625f);
        Assert.Single(LiftFootprintMeasure.Measure(FootprintBody, FootprintMap, piece, [mesh]).Parts);
        CoverageClaimMesh[] invalid = reason switch
        {
            CoverageClaimRefusal.FootprintMeshPartIdDuplicate => [mesh, mesh],
            CoverageClaimRefusal.FootprintPartMeshMissing => [],
            _ => [mesh with { PartId = partId }],
        };

        var error = Assert.Throws<CoverageClaimDataException>(
            () => LiftFootprintMeasure.Measure(FootprintBody, FootprintMap, piece, invalid));

        Assert.Equal(reason, error.Reason);
        Assert.Equal(CoverageClaimMeshKind.Piece, error.MeshKind);
        Assert.Equal(partId, error.PartId);
        Assert.Equal(meshIndex, error.MeshIndex);
        Assert.Equal(partId == 1 ? piece.Parts[0].Mesh.Key : null, error.MeshKey);
    }

    [Fact]
    public void A_fixed_triangle_refuses_heights_above_the_footprint_limit()
    {
        AttachmentSetDefinition piece = Piece("upper");
        LiftFootprint Measure(float height) =>
            LiftFootprintMeasure.Measure(FootprintBody, FootprintMap, piece, [SmallFootprintTriangle(1, height)]);
        Assert.Equal(0.25, Assert.Single(Assert.Single(Measure(0.25f).Parts).Triangles).HeightMetres(0));

        var error = Assert.Throws<CoverageClaimDataException>(() => Measure(0.3125f));

        Assert.Equal(CoverageClaimRefusal.FootprintHeightAboveLimit, error.Reason);
        Assert.Equal(CoverageClaimMeshKind.Piece, error.MeshKind);
        Assert.Equal(0, error.MeshIndex);
        Assert.Equal(piece.Parts[0].Id, error.PartId);
        Assert.Equal(piece.Parts[0].Mesh.Key, error.MeshKey);
        Assert.Equal(0, error.TriangleIndex);
        Assert.Equal(0.3125, error.HeightMetres);
    }

    [Fact]
    public void A_triangle_whose_sampling_grid_cannot_be_indexed_refuses()
    {
        AttachmentSetDefinition piece = Piece("upper");
        CoverageClaimMesh mesh = SmallFootprintTriangle(1, 0f);
        Assert.Single(Assert.Single(LiftFootprintMeasure.Measure(FootprintBody, FootprintMap, piece, [mesh]).Parts).Triangles);
        Vector3[] positions = mesh.Positions.ToArray();
        positions[1].X = 4_194_304;
        CoverageClaimMesh oversized = mesh with { Positions = positions };

        var error = Assert.Throws<CoverageClaimDataException>(
            () => LiftFootprintMeasure.Measure(FootprintBody, FootprintMap, piece, [oversized]));

        Assert.Equal(CoverageClaimRefusal.FootprintSamplingGridTooLarge, error.Reason);
        Assert.Equal(CoverageClaimMeshKind.Piece, error.MeshKind);
        Assert.Equal(0, error.MeshIndex);
        Assert.Equal(piece.Parts[0].Id, error.PartId);
        Assert.Equal(piece.Parts[0].Mesh.Key, error.MeshKey);
        Assert.Equal(0, error.TriangleIndex);
        Assert.True(error.LongestEdgeMetres > 4_000_000);
    }

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void Report_measurement_time_for_the_raised_hem_fixture()
    {
        AttachmentSetDefinition garment = Piece("upper");
        var meshes = new[] { HemGarment() };
        long start = Stopwatch.GetTimestamp();
        LiftFootprint measured = LiftFootprintMeasure.Measure(FootprintBody, FootprintMap, garment, meshes);
        output.WriteLine("Raised hem measurement: {0:F3} ms", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        output.WriteLine("Raised hem footprint SHA-256: {0}", Convert.ToHexStringLower(SHA256.HashData(LiftFootprintFile.Write(measured))));
    }

    static double HeightAt(LiftFootprintTriangle triangle, int key)
    {
        int index = Array.IndexOf(triangle.CellKeys.ToArray(), key);
        Assert.InRange(index, 0, triangle.CellKeys.Length - 1);
        return triangle.HeightMetres(index);
    }
}
