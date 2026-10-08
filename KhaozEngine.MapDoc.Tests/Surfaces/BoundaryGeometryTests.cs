using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class BoundaryGeometryTests
{
    [Fact]
    public void WallStrip_RuledQuadsSplitLowerStartToUpperEnd()
    {
        MapCompiledStrip s = BoundaryFixtures.CompileStrip(new[] { 0, 0 }, new[] { 300, 300 }, MapStripFacing.Front);
        Assert.Equal(new[] { (0, (byte)0), (0, (byte)1) }, s.Faces.Select(f => (f.Key.Primitive, f.Key.ParentTriangle)));
        Assert.All(s.Faces, f => Assert.Equal(new Vector3(0, 0, -1), f.Normal));
    }

    [Fact]
    public void WallStrip_ZeroHeightEndDegeneratesToOneTriangle()
        => Assert.Single(BoundaryFixtures.CompileStrip(new[] { 0, 0 }, new[] { 0, 300 }, MapStripFacing.Front).Faces);

    [Fact]
    public void WallStrip_LowerAboveUpperRefuses()
        => Assert.Contains("lower", Assert.Throws<MapDocumentException>(() => BoundaryFixtures.CompileStrip(new[] { 0, 400 }, new[] { 300, 300 }, MapStripFacing.Front)).Message);

    [Fact]
    public void TwoSidedLintelEmitsOppositeFacesUnderOneOwner()
    {
        MapCompiledStrip s = BoundaryFixtures.CompileStrip(new[] { 0, 0 }, new[] { 300, 300 }, MapStripFacing.TwoSided);
        Assert.Equal(4, s.Faces.Select(f => f.Key).Distinct().Count());
        Assert.All(s.Faces, f => Assert.Equal("w", f.Key.OwnerId));
        Assert.Equal(2, s.Faces.Count(f => f.Key.Side == MapSide.Back && f.Normal == new Vector3(0, 0, 1)));
    }

    [Theory, InlineData(2, true, 0, null), InlineData(2, false, 0, "subdivide"), InlineData(2, true, 1, "height"),
     InlineData(3, true, 0, null), InlineData(64, true, 0, null)]
    public void Seam_ExactSharedVerticesAgreeAcrossLattices(int k, bool subdivided, int middleOffset, string? finding)
    {
        var (view, seam) = BoundaryFixtures.CoarseFineSeam(k, subdivided, middleOffset);
        IReadOnlyList<string> findings = MapSeamValidator.Validate(seam, view);
        if (finding is null) Assert.Empty(findings); else Assert.Contains(findings, f => f.Contains(finding));
    }

    [Fact]
    public void Seam_AcrossNegativeSlotEdgeSharesExactVertices()
    {
        var (view, neg, pos) = BoundaryFixtures.NegativeSlotPair();
        Assert.Empty(MapSeamValidator.ValidateCornerDependencies(pos.Patch, view));
        MapCompiledPatch a = MapSurfaceCompiler.Compile(neg.Surface, neg.Patch), b = MapSurfaceCompiler.Compile(pos.Surface, pos.Patch);
        var shared = MapLatticeAddress.Corner(0, 0);
        Assert.Equal(CompilerFixtures.ExactAt(a, "neg", shared), CompilerFixtures.ExactAt(b, "neg", shared));
        Assert.Equal((new MapSubmissionAnchor(-1, 0, 0), new MapSubmissionAnchor(0, 0, 0)), (a.Anchor, b.Anchor));
        pos.Patch.Heights[0] = 21;
        Assert.Contains(MapSeamValidator.ValidateCornerDependencies(pos.Patch, view), f => f.Contains("owner"));
    }

    [Fact]
    public void CornerDependency_OnAFineBoundaryResolvesTheCoarseSubdivisionVertex()
    {
        var (view, fine) = BoundaryFixtures.FineCornerOnCoarseRim();
        Assert.Empty(MapSeamValidator.ValidateCornerDependencies(fine, view));
        fine.Heights[1] += 1;                                          // fine corner (1, 0) of the patch's south row
        Assert.Contains(MapSeamValidator.ValidateCornerDependencies(fine, view), f => f.Contains("owner"));
        fine.Heights[1] -= 1;
        Assert.Contains(MapSeamValidator.ValidateCornerDependencies(fine, BoundaryFixtures.ViewWithoutCoarse()), f => f.Contains("unresolved"));
    }

    [Fact]
    public void HorizontalOpeningIsAMembershipPlaneNotGeometry()
    {
        var (surface, patch, opening) = BoundaryFixtures.TwoCellOpening();
        MapOpeningPlane plane = MapOpeningBoundary.Compile(surface, patch, opening);
        Assert.Equal((4, 4, 4), (plane.Triangles.Count, plane.ExactTriangles.Count, plane.Keys.Distinct().Count()));
        Assert.All(plane.Triangles, t => Assert.True(t.Normal.Y > 0));
        Assert.DoesNotContain(MapSurfaceCompiler.Compile(surface, patch).Faces, f => f.Key.Primitive is 0 or 1);
    }
}
