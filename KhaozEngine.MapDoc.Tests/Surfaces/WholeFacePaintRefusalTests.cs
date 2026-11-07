using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class WholeFacePaintRefusalTests
{
    [Theory]
    [InlineData("role", MapDocumentRefusal.SurfacePaintTargetInvalid, "Role")]
    [InlineData("enum", MapDocumentRefusal.SchemaEnumUnsupported, "Role")]
    [InlineData("target", MapDocumentRefusal.SurfacePaintTargetInvalid, "PaintTargetSurfaceId")]
    public void Whole_face_admission_keeps_the_incompatible_paint_field(string fault, MapDocumentRefusal reason, string field)
    {
        var (baseSurface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        var target = MapSurfaceCompiler.Compile(baseSurface, patch);
        var paint = baseSurface with { Id = "paint", Role = MapSurfaceRole.PaintOverride, PaintTargetSurfaceId = baseSurface.Id };
        patch.Key = new("paint", 0, 0);
        var invalid = fault switch
        {
            "role" => paint with { Role = MapSurfaceRole.SupportFloor },
            "enum" => paint with { Role = (MapSurfaceRole)255 },
            _ => paint with { PaintTargetSurfaceId = "other" },
        };
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.ApplyPaintOverride(target, invalid, patch));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("paint", error.SurfaceId);
        Assert.Equal(baseSurface.Id, error.OtherSurfaceId);
        Assert.Equal(field, error.FieldPath);
        if (fault == "target") Assert.Equal("other", error.ActualText);
        else
        {
            Assert.Equal(typeof(MapSurfaceRole), error.EnumType);
            Assert.Equal((long)invalid.Role, error.ActualInteger);
        }
        Assert.NotEmpty(MapSurfaceCompiler.ApplyPaintOverride(target, paint, patch).Paint);
    }

    [Fact]
    public void Exact_override_overflow_retains_the_paint_and_target()
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        var target = MapSurfaceCompiler.Compile(surface, patch);
        var paint = surface with { Id = "paint", Role = MapSurfaceRole.PaintOverride, PaintTargetSurfaceId = surface.Id };
        patch.Key = new("paint", 0, 0);
        var vertices = target.ExactVertices.ToArray();
        // The exact endpoints fit, but their edge difference is 2^64-1 and cannot fit int64.
        vertices[0] = vertices[0] with { X = new(long.MinValue, 1) };
        vertices[1] = vertices[1] with { X = new(long.MaxValue, 1) };
        var broken = new MapCompiledPatch(target.Key, target.Role, target.Anchor, target.VertexIds, vertices,
            target.Offsets, target.Faces, target.Paint, target.LegacyFallbackCells);
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.ApplyPaintOverride(broken, paint, patch));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal("paint", error.SurfaceId);
        Assert.Equal(surface.Id, error.OtherSurfaceId);
        Assert.Equal(target.Key, error.Patch);
        Assert.Equal("paint override", error.Operation);
        Assert.Equal(target.Paint, broken.Paint);
        Assert.NotEmpty(MapSurfaceCompiler.ApplyPaintOverride(target, paint, patch).Paint);
    }
}
