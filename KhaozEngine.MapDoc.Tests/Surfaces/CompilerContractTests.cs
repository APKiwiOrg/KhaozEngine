using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class CompilerContractTests
{
    [Theory]
    [InlineData(MapSurfaceRole.SupportFloor)]
    [InlineData(MapSurfaceRole.Ceiling)]
    public void PaintOverride_AlignedReplacementPreservesPhysicalGeometry(MapSurfaceRole role)
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 100, 200, 300, CompilerFixtures.Full, role);
        MapCompiledPatch target = MapSurfaceCompiler.Compile(surface, patch);
        var (paintSurface, paintPatch) = CompilerFixtures.OneCell(900, 900, 900, 900,
            CompilerFixtures.Full with { Underlay = 9, Overlay = 8, Flags = MapCellFlags.FeatherOverlay });
        paintSurface = paintSurface with { Id = "paint", Role = MapSurfaceRole.PaintOverride, PaintTargetSurfaceId = surface.Id };
        paintPatch.Key = new("paint", 0, 0);
        AssertValid(surface, patch, paintSurface, paintPatch);
        MapPaintCoverage[] originalPaint = target.Paint.ToArray();

        MapCompiledPatch replaced = MapSurfaceCompiler.ApplyPaintOverride(target, paintSurface, paintPatch);

        AssertPhysicalGeometry(target, replaced);
        Assert.Equal(target.Faces.Select(f => new MapPaintCoverage(f.Key, 9, 8, true, true)), replaced.Paint);
        Assert.Equal(originalPaint, target.Paint);
        Assert.All(originalPaint, p => Assert.Equal((1, 0, false, false), ((int)p.Underlay, (int)p.Overlay, p.OverlayCovers, p.Feather)));
    }

    [Fact]
    public void PaintOverride_MixedPaintCrossingALaterFaceRefusesAtomically()
    {
        var (target, surface, patch) = CrossingOverride();
        patch.Cells[3] = patch.Cells[3] with { Underlay = 10 };
        patch.Cells[7] = patch.Cells[7] with { Underlay = 10 };
        Assert.Empty(patch.ValidateLocal());
        MapPaintCoverage[] originalPaint = target.Paint.ToArray();
        MapCompiledFace[] originalFaces = target.Faces.ToArray();
        MapExactPoint[] originalVertices = target.ExactVertices.ToArray();
        MapSurfaceCell[] originalCells = patch.Cells.ToArray();
        ulong[] originalPresence = patch.Presence.ToArray();

        MapDocumentException error = Assert.Throws<MapDocumentException>(() =>
            MapSurfaceCompiler.ApplyPaintOverride(target, surface, patch));

        Assert.Contains("paint override boundary crosses physical face", error.Message);
        Assert.Equal(originalPaint, target.Paint);
        Assert.Equal(originalFaces, target.Faces);
        Assert.Equal(originalVertices, target.ExactVertices);
        Assert.Equal(originalCells, patch.Cells);
        Assert.Equal(originalPresence, patch.Presence);
    }

    [Fact]
    public void PaintOverride_PartialPresenceCrossingALaterFaceRefusesAtomically()
    {
        var (target, surface, patch) = CrossingOverride();
        patch.SetPresent(3, 0, false);
        patch.SetPresent(3, 1, false);
        Assert.Empty(patch.ValidateLocal());
        MapPaintCoverage[] originalPaint = target.Paint.ToArray();
        MapCompiledFace[] originalFaces = target.Faces.ToArray();
        MapExactPoint[] originalVertices = target.ExactVertices.ToArray();
        MapSurfaceCell[] originalCells = patch.Cells.ToArray();
        ulong[] originalPresence = patch.Presence.ToArray();

        MapDocumentException error = Assert.Throws<MapDocumentException>(() =>
            MapSurfaceCompiler.ApplyPaintOverride(target, surface, patch));

        Assert.Contains("paint override presence boundary crosses physical face", error.Message);
        Assert.Equal(originalPaint, target.Paint);
        Assert.Equal(originalFaces, target.Faces);
        Assert.Equal(originalVertices, target.ExactVertices);
        Assert.Equal(originalCells, patch.Cells);
        Assert.Equal(originalPresence, patch.Presence);
    }

    [Theory]
    [InlineData(long.MaxValue, 0)]
    [InlineData(long.MaxValue / 64, 63)]
    public void Compile_LocallyValidUnrepresentableCoordinatesRefuse(long slotX, int cellMinX)
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 100, 200, 300, CompilerFixtures.Full);
        patch.Key = new(surface.Id, slotX, 0);
        patch.CellMinX = cellMinX;
        Assert.Empty(patch.ValidateLocal());
        Assert.True((System.Int128)slotX * 64 + cellMinX + 1 > long.MaxValue);

        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.Compile(surface, patch));

        Assert.Contains("not representable", error.Message);
        Assert.DoesNotContain("CapacityExceeded", error.Message);
        Assert.DoesNotContain("budget", error.Message);
    }

    static (MapCompiledPatch Target, MapSurfaceRef Surface, MapSurfacePatch Patch) CrossingOverride()
    {
        var (surface, patch) = CompilerFixtures.Row(MapPresencePolicy.Native,
            (CompilerFixtures.Full, true), (CompilerFixtures.Full, true));
        MapCompiledPatch target = MapSurfaceCompiler.Compile(surface, patch);
        var paintSurface = surface with
        {
            Id = "paint",
            Frame = surface.Frame with { CellUnitMetres = new(1, 2) },
            Role = MapSurfaceRole.PaintOverride,
            PaintTargetSurfaceId = surface.Id,
        };
        var paintPatch = new MapSurfacePatch
        {
            Key = new("paint", 0, 0),
            Width = 4,
            Depth = 2,
            Heights = new int[15],
            Cells = Enumerable.Repeat(CompilerFixtures.Full with { Underlay = 9 }, 8).ToArray(),
            Presence = new ulong[] { 255 },
        };
        AssertValid(surface, patch, paintSurface, paintPatch);
        return (target, paintSurface, paintPatch);
    }

    static void AssertValid(MapSurfaceRef targetSurface, MapSurfacePatch targetPatch,
        MapSurfaceRef paintSurface, MapSurfacePatch paintPatch)
    {
        Assert.Empty(targetPatch.ValidateLocal());
        Assert.Empty(paintPatch.ValidateLocal());
        Assert.Empty(MapTopologyReferenceValidator.Validate(new[] { targetSurface, paintSurface }, new[] { targetPatch, paintPatch }));
    }

    static void AssertPhysicalGeometry(MapCompiledPatch expected, MapCompiledPatch actual)
    {
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.Role, actual.Role);
        Assert.Equal(expected.Anchor, actual.Anchor);
        Assert.Equal(expected.VertexIds, actual.VertexIds);
        Assert.Equal(expected.ExactVertices, actual.ExactVertices);
        Assert.Equal(expected.Offsets, actual.Offsets);
        Assert.Equal(expected.Faces, actual.Faces);
        Assert.Equal(expected.LegacyFallbackCells, actual.LegacyFallbackCells);
    }
}
