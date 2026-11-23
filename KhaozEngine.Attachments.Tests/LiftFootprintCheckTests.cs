using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Tests.Attachments.CoverageClaimFixtures;

namespace KhaozEngine.Tests.Attachments;

public class LiftFootprintCheckTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 3)]
    public void Verification_reports_a_wrong_normal_even_for_a_conservative_extra_cell(bool extraCell, int wrongTriangle)
    {
        var (bodyMeshes, body) = RidgeBody(0);
        AttachmentSetDefinition piece = Piece("upper");
        CoverageClaimMesh mesh = Triangle(1, new(-0.03125f, 0.03125f, 0.03125f),
            new(-0.0625f, 0.03125f, 0.03125f), new(-0.03125f, 0.0625f, 0.03125f));
        LiftFootprint measured = LiftFootprintMeasure.Measure(bodyMeshes, body, piece, [mesh]);
        LiftFootprintReport Verify(LiftFootprint declared) => LiftFootprintCheck.Verify(bodyMeshes, body, piece, [mesh], declared);
        Assert.True(Verify(measured).IsHonest);
        LiftFootprintPart part = Assert.Single(measured.Parts);
        LiftFootprintTriangle triangle = Assert.Single(part.Triangles);
        int measuredCell = Assert.Single(triangle.CellKeys.ToArray());
        double height = triangle.HeightMetres(0);
        if (extraCell)
            part = new(1, [new(false, [measuredCell, new BodyCell(3, 0).Key], [height, height])]);

        LiftFootprintBodyNormal[] normals = extraCell ? [new(1, Vector3.UnitZ), new(3, Vector3.UnitX)] : [new(1, Vector3.UnitZ)];
        LiftFootprint Build() => new(body.Sha256, measured.BodyTriangleCount, [part], normals);
        Assert.True(Verify(Build()).IsHonest);
        Vector3 correct = normals[^1].Normal;
        normals[^1] = new(wrongTriangle, -correct);

        LiftFootprintReport report = Verify(Build());

        Assert.False(report.IsHonest);
        Assert.Equal(new LiftFootprintNormalMismatch(wrongTriangle, correct, -correct), Assert.Single(report.NormalMismatches));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verification_names_a_missing_cell_or_a_height_below_the_measurement(bool omitCell)
    {
        AttachmentSetDefinition piece = Piece("upper");
        CoverageClaimMesh[] meshes = [SmallFootprintTriangle(1, 0.03125f)];
        LiftFootprint measured = LiftFootprintMeasure.Measure(FootprintBody, FootprintMap, piece, meshes);
        LiftFootprintReport Verify(LiftFootprint declared) => LiftFootprintCheck.Verify(FootprintBody, FootprintMap, piece, meshes, declared);
        Assert.True(Verify(measured).IsHonest);
        var cell = new BodyCell(0, 0);
        LiftFootprintTriangle triangle = omitCell ? default : new(false, [cell.Key], [0.015625]);
        LiftFootprint declared = new(measured.BodyRegionMapSha256, measured.BodyTriangleCount, [new(1, [triangle])]);

        LiftFootprintReport report = Verify(declared);

        Assert.False(report.IsHonest);
        Assert.True(report.BodyMatches);
        Assert.Empty(report.PartMismatches);
        if (omitCell)
        {
            Assert.Equal(new LiftFootprintUncoveredCell(1, 0, cell), Assert.Single(report.UncoveredCells));
            Assert.Empty(report.UnderstatedCells);
        }
        else
        {
            Assert.Equal(new LiftFootprintUnderstatedCell(1, 0, cell, 0.03125, 0.01563), Assert.Single(report.UnderstatedCells));
            Assert.Empty(report.UncoveredCells);
        }
    }

    [Theory]
    [InlineData(LiftFootprintPartMismatchReason.MissingPart, 1u, null)]
    [InlineData(LiftFootprintPartMismatchReason.ExtraPart, 2u, null)]
    [InlineData(LiftFootprintPartMismatchReason.TriangleCountMismatch, 1u, null)]
    [InlineData(LiftFootprintPartMismatchReason.FixedDeclaredSwinging, 1u, 0)]
    [InlineData(LiftFootprintPartMismatchReason.SwingingDeclaredFixed, 1u, 0)]
    public void Verification_names_each_kind_of_part_mismatch(LiftFootprintPartMismatchReason reason, uint partId, int? triangleIndex)
    {
        AttachmentSetDefinition piece = Piece("upper");
        CoverageClaimMesh mesh = SmallFootprintTriangle(1, 0.03125f);
        if (reason == LiftFootprintPartMismatchReason.SwingingDeclaredFixed)
            mesh = mesh with { Swinging = new[] { true, false, false } };

        LiftFootprint measured = LiftFootprintMeasure.Measure(FootprintBody, FootprintMap, piece, [mesh]);
        LiftFootprintReport Verify(LiftFootprint declared) => LiftFootprintCheck.Verify(FootprintBody, FootprintMap, piece, [mesh], declared);
        Assert.True(Verify(measured).IsHonest);
        LiftFootprintPart part = Assert.Single(measured.Parts);
        int[] keys = Assert.Single(part.Triangles).CellKeys.ToArray();
        LiftFootprintPart[] declaredParts = reason switch
        {
            LiftFootprintPartMismatchReason.MissingPart => [],
            LiftFootprintPartMismatchReason.ExtraPart => [part, new(2, part.Triangles)],
            LiftFootprintPartMismatchReason.TriangleCountMismatch => [new(1, [])],
            LiftFootprintPartMismatchReason.FixedDeclaredSwinging => [new(1, [new(true, keys, [])])],
            _ => [new(1, [new(false, keys, [0.03125])])],
        };

        LiftFootprintReport report = Verify(new(measured.BodyRegionMapSha256, measured.BodyTriangleCount, declaredParts));

        Assert.False(report.IsHonest);
        Assert.True(report.BodyMatches);
        Assert.Equal(new LiftFootprintPartMismatch(partId, reason, triangleIndex), Assert.Single(report.PartMismatches));
        Assert.Empty(report.UncoveredCells);
        Assert.Empty(report.UnderstatedCells);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verification_skips_cell_comparison_when_the_body_hash_or_count_differs(bool changeCount)
    {
        AttachmentSetDefinition piece = Piece("upper");
        CoverageClaimMesh[] meshes = [Triangle(1, new(0, 0, 0.03125f), new(0.0625f, 0, 0.03125f), new(0, 0.0625f, 0.03125f))];
        LiftFootprint measured = LiftFootprintMeasure.Measure(FootprintBody, FootprintMap, piece, meshes);
        LiftFootprintReport Verify(LiftFootprint declared) => LiftFootprintCheck.Verify(FootprintBody, FootprintMap, piece, meshes, declared);
        Assert.True(Verify(measured).IsHonest);
        var shortTriangle = new LiftFootprintTriangle(false, [0], [0]);
        var shortPart = new LiftFootprintPart(1, [shortTriangle]);
        LiftFootprintReport sameBody = Verify(new(measured.BodyRegionMapSha256, measured.BodyTriangleCount, [shortPart]));
        Assert.NotEmpty(sameBody.UncoveredCells);
        Assert.NotEmpty(sameBody.UnderstatedCells);

        LiftFootprint declared = new(changeCount ? measured.BodyRegionMapSha256 : FlatMap.Sha256,
            changeCount ? 2 : measured.BodyTriangleCount, [shortPart, new(2, [])]);
        LiftFootprintReport report = Verify(declared);

        Assert.False(report.IsHonest);
        Assert.False(report.BodyMatches);
        Assert.Equal(new LiftFootprintPartMismatch(2, LiftFootprintPartMismatchReason.ExtraPart, null), Assert.Single(report.PartMismatches));
        Assert.Empty(report.UncoveredCells);
        Assert.Empty(report.UnderstatedCells);
    }
}
