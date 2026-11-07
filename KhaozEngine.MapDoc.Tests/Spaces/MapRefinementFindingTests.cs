using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapRefinementFindingTests
{
    [Theory]
    [InlineData("lower", "MaxSourceFacesPerBound", 2)]
    [InlineData("upper", "MaxSourceFacesPerBound", 2)]
    [InlineData("pairs", "MaxPairChecks", 4)]
    [InlineData("faces", "MaxRefinementFaces", 2)]
    [InlineData("vertices", "MaxRefinementVertices", 4)]
    public void RefinementBudgets_RetainTheFirstMeasuredLimitAndAcceptItsBoundary(string fault, string budget, int count)
    {
        var lower = RefinementFixtures.UnitSquareSwNe("lower", 0);
        var upper = RefinementFixtures.UnitSquareSwNe("upper", 300);
        if (fault == "upper") lower = lower.Take(1).ToArray();
        MapCellRefinement Run(int limit)
        {
            var limits = fault switch
            {
                "lower" or "upper" => new MapRefinementLimits(MaxSourceFacesPerBound: limit),
                "pairs" => new(MaxPairChecks: limit),
                "faces" => new(MaxRefinementFaces: limit),
                _ => new(MaxRefinementVertices: limit),
            };
            return MapCommonRefinement.Refine(lower, upper, RefinementFixtures.Origin00, RefinementFixtures.One11, limits);
        }
        var refused = Run(count - 1);
        Assert.Equal(MapRefinementStatus.CapacityExceeded, refused.Status);
        var finding = Assert.IsType<MapDocumentFinding>(refused.Finding);
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, finding.Reason);
        Assert.Equal(budget, finding.Budget);
        Assert.Equal(count, finding.ActualCount);
        Assert.Equal(count - 1, finding.Limit);
        if (fault is "lower" or "upper") Assert.Equal(fault, finding.FieldPath);
        Assert.Empty(refused.Faces); Assert.Empty(refused.Vertices);
        Assert.Equal(MapRefinementStatus.Complete, Run(count).Status);
        Assert.Equal(MapRefinementStatus.Complete, Run(count + 1).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceCount_RetainsTheReadThatFailedThePredicate(bool upper)
    {
        var source = new ChangingCount(3);
        var refused = MapCommonRefinement.Refine(upper ? [] : source, upper ? source : null,
            RefinementFixtures.Origin00, RefinementFixtures.One11, new(MaxSourceFacesPerBound: 2));
        Assert.Equal(MapRefinementStatus.CapacityExceeded, refused.Status);
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, refused.Finding!.Reason);
        Assert.Equal(upper ? "upper" : "lower", refused.Finding.FieldPath);
        Assert.Equal(3L, refused.Finding.ActualCount);
        Assert.Equal(2L, refused.Finding.Limit);
        Assert.Equal(MapRefinementStatus.Complete, MapCommonRefinement.Refine(RefinementFixtures.UnitSquareSwNe("lower", 0), null,
            RefinementFixtures.Origin00, RefinementFixtures.One11, new(MaxSourceFacesPerBound: 2)).Status);
    }

    [Theory]
    [InlineData("patches", "MaxContextPatches", 1)]
    [InlineData("faces", "MaxContextFaces", 2)]
    public void PreparedContext_ForwardsItsConcreteBudgetThroughTheCellStatus(string fault, string budget, int count)
    {
        var view = LegacyExteriorFixtures.View("untagged");
        var footprint = LegacyExteriorFixtures.Footprint(view);
        MapCellRefinement Run(int limit) => MapCommonRefinement.RefineFootprintCell(view, footprint, 0,
            fault == "patches" ? new(MaxContextPatches: limit) : new(MaxContextFaces: limit));
        var refused = Run(count - 1);
        Assert.Equal(MapRefinementStatus.CapacityExceeded, refused.Status);
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, refused.Finding!.Reason);
        Assert.Equal(budget, refused.Finding.Budget);
        Assert.Equal(count, refused.Finding.ActualCount);
        Assert.Equal(count - 1, refused.Finding.Limit);
        Assert.Empty(refused.Faces); Assert.Empty(refused.Vertices);
        Assert.Equal(MapRefinementStatus.Complete, Run(count).Status);
        Assert.Equal(MapRefinementStatus.Complete, Run(count + 1).Status);
    }

    [Theory]
    [InlineData("cave", MapDocumentRefusal.LegacyExteriorSpaceRequired)]
    [InlineData("upper-tag", MapDocumentRefusal.LegacyExteriorOpenTopRequired)]
    [InlineData("foreign-lattice", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    public void BoundAndCellAdapters_KeepTheConcreteRecipeAndItsInvalidStatus(string variant, MapDocumentRefusal reason)
    {
        var view = LegacyExteriorFixtures.View(variant);
        var footprint = LegacyExteriorFixtures.Footprint(view);
        var rect = new MapBoundPreparation(view, new(), null).Rectangle(footprint, 0);
        var bound = MapCommonRefinement.BoundFaces(view, footprint, footprint.Lower, rect[0], rect[1], new());
        Assert.Equal(MapRefinementStatus.Invalid, bound.Status);
        Assert.Equal(reason, bound.Finding!.Reason);
        var cell = MapCommonRefinement.RefineFootprintCell(view, footprint, 0, new());
        Assert.Equal(MapRefinementStatus.Invalid, cell.Status);
        Assert.Equal(bound.Finding, cell.Finding);
        var wide = MapWideCellRefinement.FromBounded(cell);
        Assert.Equal(cell.Finding, wide.Finding);
        Assert.Equal(cell.Finding, wide.Narrow().Finding);
        var control = LegacyExteriorFixtures.View("tagged");
        Assert.Equal(MapRefinementStatus.Complete,
            MapCommonRefinement.RefineFootprintCell(control, LegacyExteriorFixtures.Footprint(control), 0, new()).Status);
    }

    [Fact]
    public void DegenerateClip_AndMissingLatticeKeepTheirInvalidProjection()
    {
        var boundary = new[] { MapWideXz.From(RefinementFixtures.Origin00), MapWideXz.From(new(new(1, 1), new(0, 1))) };
        var clip = MapCommonRefinement.RefineWide([], null, boundary, new());
        Assert.Equal(MapRefinementStatus.Invalid, clip.Status);
        Assert.Equal(MapDocumentRefusal.BoundsInvalid, clip.Finding!.Reason);
        var view = LegacyExteriorFixtures.View("untagged");
        var footprint = LegacyExteriorFixtures.Footprint(view);
        var missing = footprint with { Lattice = new("missing", 0, 0) };
        var cell = MapCommonRefinement.RefineFootprintCell(view, missing, 0, new());
        Assert.Equal(MapRefinementStatus.Invalid, cell.Status);
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, cell.Finding!.Reason);
        Assert.Equal("missing", cell.Finding.SurfaceId);
        Assert.Equal(MapRefinementStatus.Complete, MapCommonRefinement.RefineFootprintCell(view, footprint, 0, new()).Status);
        var controlBoundary = new[] { MapWideXz.From(RefinementFixtures.Origin00), MapWideXz.From(new(new(1, 1), new(0, 1))),
            MapWideXz.From(RefinementFixtures.One11), MapWideXz.From(new(new(0, 1), new(1, 1))) };
        Assert.Equal(MapRefinementStatus.Complete, MapCommonRefinement.RefineWide([], null, controlBoundary, new()).Status);
    }

    sealed class ChangingCount(int first) : IReadOnlyList<MapBoundFace>
    {
        bool _first = true;
        public int Count { get { int value = _first ? first : 0; _first = false; return value; } }
        public MapBoundFace this[int index] => throw new InvalidOperationException();
        public IEnumerator<MapBoundFace> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
