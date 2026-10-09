using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceCompilerTests
{
    static MapCompiledPatch Compile((MapSurfaceRef Surface, MapSurfacePatch Patch) x) => MapSurfaceCompiler.Compile(x.Surface, x.Patch);

    [Theory, InlineData((byte)0), InlineData((byte)1), InlineData((byte)2), InlineData((byte)3)]
    public void NativeDiagonalHalfWithoutOverlayStillForcesSplit(byte r)
    {
        Assert.Equal(r % 2 == 0, MapSurfaceTopology.SplitSwNe(0, 100, 100, 100, MapOverlayCut.DiagonalHalf, r, MapCellTopology.Auto));
        MapCompiledPatch mesh = Compile(CompilerFixtures.OneCell(0, 100, 100, 100, CompilerFixtures.Full with { Cut = MapOverlayCut.DiagonalHalf, Rotation = r }));
        var sw = new MapVertexId("one", MapLatticeAddress.Corner(0, 0));
        var ne = new MapVertexId("one", MapLatticeAddress.Corner(1, 1));
        Assert.Equal(2, mesh.Faces.Count);
        Assert.All(mesh.Faces, f => Assert.Equal(r % 2 == 0, CompilerFixtures.Has(mesh, f, sw) && CompilerFixtures.Has(mesh, f, ne)));
        Assert.All(mesh.Faces, f => Assert.True(f.Normal.Y > 0));
    }

    [Fact]
    public void CeilingWindingReversesFloorWinding()
    {
        MapCompiledPatch floor = Compile(CompilerFixtures.OneCell(0, 100, 50, 160, CompilerFixtures.Full));
        MapCompiledPatch ceiling = Compile(CompilerFixtures.OneCell(0, 100, 50, 160, CompilerFixtures.Full, MapSurfaceRole.Ceiling));
        Assert.Equal(floor.Faces.Select(f => (f.A, f.C, f.B)), ceiling.Faces.Select(f => (f.A, f.B, f.C)));
        Assert.All(floor.Faces, f => Assert.True(f.Normal.Y > 0));
        Assert.All(ceiling.Faces, f => Assert.True(f.Normal.Y < 0));
    }

    [Fact]
    public void PhysicalApertureHasNoFacesAndNoFallback()
    {
        MapCompiledPatch mesh = Compile(CompilerFixtures.Row(MapPresencePolicy.Native, (CompilerFixtures.Full, true), (CompilerFixtures.Full, false)));
        Assert.Equal(2, mesh.Faces.Count);
        Assert.All(mesh.Faces, f => Assert.Equal(0, f.Key.Primitive));
        Assert.Empty(mesh.LegacyFallbackCells);
    }

    [Fact]
    public void ImportedNoDrawAndVoidKeepPresenceFlagsAndTaggedFallback()
    {
        var noDraw = CompilerFixtures.Full with { Flags = MapCellFlags.NoDraw };
        var voidCell = CompilerFixtures.Full with { Underlay = 0 };
        var (s, p) = CompilerFixtures.Row(MapPresencePolicy.LegacyTileWorld, (CompilerFixtures.Full, true), (noDraw, true), (voidCell, true));
        MapCompiledPatch mesh = MapSurfaceCompiler.Compile(s, p);
        Assert.Equal(new[] { 1, 2 }, mesh.LegacyFallbackCells.Select(c => c.SlotCell));
        Assert.Equal(new MapSubmissionAnchor(0, 0, 0), mesh.Anchor);
        Assert.All(mesh.Faces, f => Assert.Equal(0, f.Key.Primitive));
        Assert.True(p.IsPresent(1, 0) && p.IsPresent(2, 0));
        Assert.Equal(MapCellFlags.NoDraw, p.Cells[1].Flags);
    }

    [Fact]
    public void OverlayCut_NeverOpensOrClosesAPhysicalAperture()
    {
        var (s, p) = CompilerFixtures.Row(MapPresencePolicy.Native, (CompilerFixtures.Full, true), (CompilerFixtures.Full, false));
        MapCompiledPatch before = MapSurfaceCompiler.Compile(s, p);
        p.Cells[0] = p.Cells[0] with { Overlay = 5, Cut = MapOverlayCut.CornerQuarter, Rotation = 1 };
        p.Cells[1] = p.Cells[1] with { Overlay = 5, Cut = MapOverlayCut.CornerThreeQuarter };
        MapCompiledPatch after = MapSurfaceCompiler.Compile(s, p);
        Assert.Equal((new MapExactValue(1, 1), new MapExactValue(0, 1)), (CompilerFixtures.Area(after, 0), CompilerFixtures.Area(after, 1)));
        Assert.Equal(CompilerFixtures.Area(before, 0), CompilerFixtures.Area(after, 0));
        Assert.False(p.IsPresent(1, 0));
        Assert.Contains(after.Paint, c => c.OverlayCovers && c.Overlay == 5);
        Assert.DoesNotContain(before.Paint, c => c.OverlayCovers);
    }

    [Fact]
    public void SubmissionAnchorsAreWholeMetresAndOffsetsExact()
    {
        MapCompiledPatch mesh = MapSurfaceCompiler.Compile(CompilerFixtures.ThirdsSurface(), CompilerFixtures.ThirdsPatch());
        Assert.Equal(new MapSubmissionAnchor(-1, 2, 0), mesh.Anchor);
        int i = mesh.VertexIds.ToList().IndexOf(new MapVertexId("thirds", MapLatticeAddress.Corner(-2, 1)));
        Assert.Equal(new Vector3(0.33333334f, 1.5f, 0.33333334f), mesh.Offsets[i]);
        Assert.Equal(new MapExactValue(7, 2), MapSurfaceCompiler.ExactHeight(CompilerFixtures.ThirdsSurface(), CompilerFixtures.ThirdsPatch(), new(-2, 3), new(1, 3)));
    }

    [Theory, InlineData(2, 5, 1, 2), InlineData(3, 6, 1, 3)]
    public void RimSubdivision_FansFromTheCentroidWithExactAreaAndUniqueKeys(int k, int faces, int firstNumerator, int denominator)
    {
        var (s, p) = CompilerFixtures.OneCell(100, 100, 100, 100, CompilerFixtures.Full);
        p.EdgeSubdivisions.Add(new MapEdgeSubdivision(0, 0, MapCellEdge.South, k));       // patch boundary, legal
        MapCompiledPatch mesh = MapSurfaceCompiler.Compile(s, p);
        Assert.Equal(faces, mesh.Faces.Count);                                            // k+2 children on the south parent, 1 on the other
        Assert.Contains(new MapVertexId("one", MapLatticeAddress.Create(firstNumerator, 0, denominator)), mesh.VertexIds);
        Assert.Contains(new MapVertexId("one", MapLatticeAddress.Create(2, 1, 3)), mesh.VertexIds);   // centroid of (Sw, Se, Ne)
        Assert.Equal(faces, mesh.Faces.Select(f => f.Key).Distinct().Count());
        Assert.Equal(new MapExactValue(1, 1), CompilerFixtures.Area(mesh, 0));
    }

    [Fact]
    public void RimSubdivision_TwoEdgesOfOneTriangleAt64StayDisjointAndCollisionFree()
    {
        var (s, p) = CompilerFixtures.OneCell(0, 100, 50, 160, CompilerFixtures.Full);   // splits NW-SE, the (Se, Ne, Nw) triangle owns East and North
        p.EdgeSubdivisions.Add(new MapEdgeSubdivision(0, 0, MapCellEdge.East, 64));
        p.EdgeSubdivisions.Add(new MapEdgeSubdivision(0, 0, MapCellEdge.North, 64));
        MapCompiledPatch mesh = MapSurfaceCompiler.Compile(s, p);
        byte parent = mesh.Faces.GroupBy(f => f.Key.ParentTriangle).Single(g => g.Count() > 1).Key;
        var children = mesh.Faces.Where(f => f.Key.ParentTriangle == parent).ToList();
        Assert.Equal(129, children.Count);                                                 // 3 + 63 + 63
        Assert.Equal(Enumerable.Range(0, 129).Select(i => (ushort)i), children.Select(f => f.Key.Child));
        Assert.Equal(130, mesh.Faces.Select(f => f.Key).Distinct().Count());
        Assert.All(children, f => Assert.True(CompilerFixtures.ExactArea(mesh, f).Sign > 0));
        Assert.Equal(new MapExactValue(1, 2), children.Aggregate(new MapExactValue(0, 1), (a, f) => a.Add(CompilerFixtures.ExactArea(mesh, f))));
        Assert.Equal(129, CompilerFixtures.ChildEdges(mesh, 0, parent).Distinct().Count());   // 64 east, 64 north, 1 diagonal, each once
        Assert.All(mesh.Paint.Where(c => c.Face.ParentTriangle == parent), c => Assert.Equal(1, c.Underlay));
        Assert.All(children, f => Assert.Equal(CompilerFixtures.ParentPlaneHeight(mesh, f), CompilerFixtures.CentreHeight(mesh, f)));
    }

    [Fact]
    public void EdgeSubdivision_RefusesInteriorAndOutOfRangeSplits()
    {
        var (s, p) = CompilerFixtures.Row(MapPresencePolicy.Native, (CompilerFixtures.Full, true), (CompilerFixtures.Full, true));
        p.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.East, 2));
        Assert.Contains("interior", Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.Compile(s, p)).Message);
        var (s1, p1) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        p1.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.South, 65));
        Assert.Contains("segments", Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.Compile(s1, p1)).Message);
    }
}
