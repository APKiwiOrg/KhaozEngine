using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class CommonRefinementTests
{
    static MapExactXz X(long xn, long xd, long zn, long zd) => RefinementFixtures.X(xn, xd, zn, zd);
    static MapCellRefinement Cell((MapScopedSurfaces View, MapSpaceFootprint Footprint) f, int slotCell, MapRefinementLimits? limits = null)
        => MapCommonRefinement.RefineFootprintCell(f.View, f.Footprint, slotCell, limits ?? new MapRefinementLimits());

    [Fact]
    public void HalfFloorUnderThirdCeiling_RefinesOneFootprintCellExactly()
    {
        MapCellRefinement r = Cell(MixedResolutionFixtures.HalfUnderThird(far: false), 1);
        Assert.Equal(MapRefinementStatus.Complete, r.Status);
        Assert.Equal(new[] { X(1, 3, 0, 1), X(1, 2, 0, 1), X(2, 3, 0, 1), X(1, 2, 1, 6), X(2, 3, 1, 6), X(1, 3, 1, 3), X(1, 2, 1, 3), X(2, 3, 1, 3) }, r.Vertices);
        Assert.Equal((5, 6), (r.Faces.Count, r.PairChecks));
        Assert.Equal(new[] { 0, 0, 1, 1, 1 }, r.Faces.Select(f => f.Lower.Primitive));
        Assert.All(r.Faces, f => Assert.Equal(1, f.Upper!.Value.Primitive));
        Assert.Equal(new MapExactValue[] { new(1, 72), new(1, 72), new(1, 72), new(1, 36), new(1, 24) }, r.Faces.Select(MapCommonRefinement.Area).Order());
        Assert.Equal((new MapExactValue(1, 9), new MapExactValue(1, 9), (MapExactValue?)new MapExactValue(1, 9)), (r.CellArea, r.LowerArea, r.UpperArea));
        Assert.Equal(new MapExactValue(1, 1), r.MinSeparation);
    }
    [Fact]
    public void HalfUnderThird_FarCopyRefinesToTheSameExactOffsets()
    {
        MapCellRefinement near = Cell(MixedResolutionFixtures.HalfUnderThird(far: false), 1), far = Cell(MixedResolutionFixtures.HalfUnderThird(far: true), 1);
        var shift = new MapExactValue(32000, 1);
        Assert.Equal(near.Vertices, far.Vertices.Select(v => new MapExactXz(v.X.Subtract(shift), v.Z.Subtract(shift))));
        Assert.Equal((near.Faces.Count, near.PairChecks, near.MinSeparation), (far.Faces.Count, far.PairChecks, far.MinSeparation));
    }
    [Fact]
    public void CommonRefinement_IncludesEdgeCrossings()
    {
        MapCellRefinement r = MapCommonRefinement.Refine(RefinementFixtures.UnitSquareNwSe("lo", 0), RefinementFixtures.UnitSquareSwNe("up", 300),
            RefinementFixtures.Origin00, RefinementFixtures.One11, new());
        Assert.Equal(new[] { X(0, 1, 0, 1), X(1, 1, 0, 1), X(1, 2, 1, 2), X(0, 1, 1, 1), X(1, 1, 1, 1) }, r.Vertices);
        Assert.Equal((4, 4), (r.Faces.Count, r.PairChecks));
    }
    [Fact]
    public void Refine_CoincidentDiagonalsAndTJunctionsAddNoSliverFaces()
    {
        MapCellRefinement same = MapCommonRefinement.Refine(RefinementFixtures.UnitSquareSwNe("lo", 0), RefinementFixtures.UnitSquareSwNe("up", 300),
            RefinementFixtures.Origin00, RefinementFixtures.One11, new());
        Assert.Equal((2, 4, 4), (same.Faces.Count, same.Vertices.Count, same.PairChecks));
        MapCellRefinement tee = MapCommonRefinement.Refine(RefinementFixtures.UnitSquareSwNe("lo", 0), RefinementFixtures.FanFromSouthMidpoint("up", 300),
            RefinementFixtures.Origin00, RefinementFixtures.One11, new());
        Assert.Equal(new[] { X(0, 1, 0, 1), X(1, 2, 0, 1), X(1, 1, 0, 1), X(1, 3, 1, 3), X(0, 1, 1, 1), X(1, 1, 1, 1) }, tee.Vertices);
        Assert.Equal((5, 6), (tee.Faces.Count, tee.PairChecks));
        Assert.Equal(new MapExactValue[] { new(1, 12), new(1, 6), new(1, 4), new(1, 6), new(1, 3) }, tee.Faces.Select(MapCommonRefinement.Area));
        Assert.Equal(new MapExactValue(1, 1), tee.Faces.Aggregate(new MapExactValue(0, 1), (a, f) => a.Add(MapCommonRefinement.Area(f))));
    }
    [Fact]
    public void Refine_OpenTopKeepsLowerPolygonsWithoutSeparation()
    {
        MapCellRefinement r = MapCommonRefinement.Refine(RefinementFixtures.UnitSquareSwNe("lo", 0), null,
            X(1, 4, 0, 1), X(3, 4, 1, 1), new());
        Assert.Equal((MapRefinementStatus.Complete, 2, 0), (r.Status, r.Faces.Count, r.PairChecks));
        Assert.All(r.Faces, f => Assert.Equal(((MapFaceKey?)null, (MapExactValue?)null), (f.Upper, f.MinSeparation)));
        Assert.Equal((new MapExactValue(1, 2), (MapExactValue?)null, (MapExactValue?)null), (r.LowerArea, r.UpperArea, r.MinSeparation));
    }
    [Fact]
    public void Refine_ClassifiesInvalidCapacityAndRepresentabilityOutcomesWithoutPartialResults()
    {
        static void Empty(MapCellRefinement r, MapRefinementStatus status, string? detail)
        {
            Assert.Equal((status, 0, 0), (r.Status, r.Faces.Count, r.Vertices.Count));
            if (detail is not null) Assert.Contains(detail, r.Detail);
        }
        Empty(MapCommonRefinement.Refine(RefinementFixtures.CollinearTriangle(), RefinementFixtures.UnitSquareSwNe("up", 300), RefinementFixtures.Origin00, RefinementFixtures.One11, new()),
            MapRefinementStatus.Invalid, "degenerate");
        Empty(MapCommonRefinement.Refine(RefinementFixtures.UnitSquareSwNe("lo", 0), null, RefinementFixtures.Origin00, X(0, 1, 1, 1), new()),
            MapRefinementStatus.Invalid, "cell");
        Empty(MapCommonRefinement.Refine(RefinementFixtures.LowerUnitTriangle(), RefinementFixtures.PrimeSliverUpper(long.MaxValue), RefinementFixtures.Origin00, RefinementFixtures.One11, new()),
            MapRefinementStatus.NotRepresentable, "overflow");                  // the crossing x = p/(2p-1) needs a denominator above long.MaxValue
        var near = MixedResolutionFixtures.HalfUnderThird(far: false);
        foreach (MapRefinementLimits tight in new MapRefinementLimits[] { new(MaxSourceFacesPerBound: 3), new(MaxPairChecks: 5), new(MaxRefinementFaces: 4), new(MaxRefinementVertices: 7) })
            Empty(Cell(near, 1, tight), MapRefinementStatus.CapacityExceeded, null);
    }
    [Fact]
    public void SixtyFourthFloorUnderOneMetreRoof_MeetsAndRefusesItsDeclaredBudgets()
    {
        var f = MixedResolutionFixtures.SixtyFourthUnderMetre();
        MapCellRefinement ok = Cell(f, 0);
        Assert.Equal((MapRefinementStatus.Complete, 8192, 16384, 4225), (ok.Status, ok.Faces.Count, ok.PairChecks, ok.Vertices.Count));
        Assert.Equal(new MapExactValue(3, 1), ok.MinSeparation);
        foreach (MapRefinementLimits tight in new MapRefinementLimits[] { new(MaxSourceFacesPerBound: 8191), new(MaxPairChecks: 16383), new(MaxRefinementFaces: 8191), new(MaxRefinementVertices: 4224) })
            Assert.Equal(MapRefinementStatus.CapacityExceeded, Cell(f, 0, tight).Status);
    }
    [Fact]
    public void BoundaryTouch_DropsTouchingFacesAndUsesOnlyThePositiveAreaSlot()
    {
        MapScopedSurfaces s = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.BoundaryTouch()), AcquisitionBoundFixtures.TouchScope);
        MapSpaceFootprint ledge = s.RecordsIn(new("third", 1, 0)).OfType<MapSpaceFootprint>().Single();
        MapCellRefinement r = MapCommonRefinement.RefineFootprintCell(s, ledge, 31, new());
        Assert.Equal(MapRefinementStatus.Complete, r.Status);
        Assert.All(r.Faces, f => Assert.Equal(new MapPatchKey("half", 0, 0), f.Lower.Patch));
        var (lower, upper, min, max) = RefinementFixtures.LedgeBoundFaces(s);
        MapCellRefinement withTouch = MapCommonRefinement.Refine(lower.Append(RefinementFixtures.TouchingFace()).ToList(), upper, min, max, new());
        Assert.Equal(r.Vertices, withTouch.Vertices);
        Assert.Equal(r.Faces.Select(f => (f.Lower, f.Upper)), withTouch.Faces.Select(f => (f.Lower, f.Upper)));
    }
    [Fact]
    public void AdversarialUnits_RefineFootprintCellIsNotRepresentable()
    {
        MapDocument doc = AcquisitionBoundFixtures.AdversarialUnits();
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(doc.Surfaces);
        MapSpaceFootprint strange = view.RecordsIn(new("odd", 0, 0)).OfType<MapSpaceFootprint>().Single();
        MapCellRefinement r = MapCommonRefinement.RefineFootprintCell(view, strange, 5, new());
        Assert.Equal((MapRefinementStatus.NotRepresentable, 0), (r.Status, r.Faces.Count));
        Assert.Contains("overflow", r.Detail);
    }
    [Fact]
    public void Refine_ChargesEveryAttemptedPairBeforeBoundingBoxRejection()
    {
        var lower = RefinementFixtures.UnitSquareGrid16("lo", 0);
        var upper = RefinementFixtures.UnitSquareGrid16("up", 100);
        foreach (MapRefinementLimits refused in new MapRefinementLimits[] { new(MaxPairChecks: 8192), new(), new(MaxPairChecks: 262_143) })   // the default 65,536 refuses too
        {
            MapCellRefinement r = MapCommonRefinement.Refine(lower, upper, RefinementFixtures.Origin00, RefinementFixtures.One11, refused);
            Assert.Equal((MapRefinementStatus.CapacityExceeded, 0, 0, 0), (r.Status, r.Faces.Count, r.Vertices.Count, r.PairChecks));
            Assert.Contains("pair checks", r.Detail);
        }
        MapCellRefinement ok = MapCommonRefinement.Refine(RefinementFixtures.UnitSquareGrid2("lo", 0), RefinementFixtures.UnitSquareGrid2("up", 100), RefinementFixtures.Origin00, RefinementFixtures.One11, new(MaxPairChecks: 64));
        Assert.Equal((MapRefinementStatus.Complete, 8, 9, 64), (ok.Status, ok.Faces.Count, ok.Vertices.Count, ok.PairChecks));
        Assert.Equal(new MapExactValue(1, 1), ok.MinSeparation);
    }
}
