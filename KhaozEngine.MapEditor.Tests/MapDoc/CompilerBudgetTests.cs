using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class CompilerBudgetTests
{
    [Fact]
    public void Compile_RefusesBeforeExceedingTheFaceBudget()
    {
        var (s, p) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        p.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.South, 64));                          // 66 children plus 1
        Assert.Contains("66", Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.Compile(s, p, maxFaces: 66)).Message);
        Assert.Equal(67, MapSurfaceCompiler.Compile(s, p, maxFaces: 67).Faces.Count);
    }

    [Fact]
    public void CountFaces_MatchesTheCompilerAndAMaskedCompileKeepsExactlyItsCells()
    {
        var (s, p) = CompilerFixtures.OneCell(0, 100, 50, 160, CompilerFixtures.Full);
        p.EdgeSubdivisions.Add(new MapEdgeSubdivision(0, 0, MapCellEdge.East, 64));
        p.EdgeSubdivisions.Add(new MapEdgeSubdivision(0, 0, MapCellEdge.North, 64));
        Assert.Equal(130L, MapSurfaceCompiler.CountFaces(s, p, MapSlotCellMask.All));               // 129 fan children plus 1, as compiled
        MapSurfaceRef thirds = CompilerFixtures.ThirdsSurface();
        MapSurfacePatch patch = CompilerFixtures.ThirdsPatch();
        MapSlotCellMask one = MapSlotCellMask.Of(new[] { 62 });
        Assert.Equal((6L, 2L), (MapSurfaceCompiler.CountFaces(thirds, patch, MapSlotCellMask.All), MapSurfaceCompiler.CountFaces(thirds, patch, one)));
        MapCompiledPatch full = MapSurfaceCompiler.Compile(thirds, patch), masked = MapSurfaceCompiler.Compile(thirds, patch, one, MapSurfaceCompiler.MaxFacesPerPatch);
        Assert.Equal(full.Faces.Where(f => f.Key.Primitive == 62).Select(f => (f.Key, full.ExactTriangle(f))), masked.Faces.Select(f => (f.Key, masked.ExactTriangle(f))));
    }
}
