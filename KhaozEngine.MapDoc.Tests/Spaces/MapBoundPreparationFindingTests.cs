using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapBoundPreparationFindingTests
{
    [Fact]
    public void ContextRefusal_KeepsItsFindingWhenLaterFootprintsExceedAnotherBudget()
    {
        var set = new MapSurfaceSet();
        var surface = MixedResolutionFixtures.Surface("floor", 1, MapSurfaceRole.SupportFloor);
        set.Refs.Add(surface);
        var first = MixedResolutionFixtures.Patch(new(surface.Id, 0, 0), 1, 1, (_, _) => 0);
        var later = MixedResolutionFixtures.Patch(new(surface.Id, 1, 0), 1, 1, (_, _) => 0);
        first.Records.Add(new MapSpaceDoc("space-a", MapSpaceKind.Exterior, null, null, [], [], [], []));
        later.Records.Add(new MapSpaceDoc("space-b", MapSpaceKind.Exterior, null, null, [], [], [], []));
        set.Patches.Add(first.Key, first);
        set.Patches.Add(later.Key, later);
        var footprints = new[] { Footprint("a", first.Key), Footprint("b", later.Key) };
        var view = MapScopedSurfaces.CompleteView(set);

        // W6-R1c regression: draining the second footprint must not replace the first refusal's context.
        var refused = MapCommonRefinement.PrepareFootprints(view, footprints,
            new(MaxContextFaces: 1, MaxContextPatches: 1), null);
        Assert.Null(refused.Context);
        var finding = Assert.IsType<MapDocumentFinding>(refused.Finding);
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, finding.Reason);
        Assert.Equal("MaxContextFaces", finding.Budget);
        Assert.Equal(1L, finding.Limit);
        Assert.Equal(2L, finding.ActualCount);
        Assert.Equal(first.Key, finding.Patch);
        Assert.Equal(refused.Refusal, finding.Message);
        var laterFinding = Assert.IsType<MapDocumentFinding>(refused.CellOutcomes[("b", 0)].Finding);
        Assert.Equal("MaxContextPatches", laterFinding.Budget);
        Assert.Equal(later.Key, laterFinding.Patch);

        var control = MapCommonRefinement.PrepareFootprints(view, footprints,
            new(MaxContextFaces: 4, MaxContextPatches: 2), null);
        Assert.NotNull(control.Context);
        Assert.Null(control.Refusal);
        Assert.Null(control.Finding);

        static MapSpaceFootprint Footprint(string id, MapPatchKey patch) => new(id, new("space-" + id, patch), patch, [0],
            new(MapBoundKind.SupportFloor, patch.SurfaceId, null), new(MapBoundKind.OpenTop, null, null));
    }

    [Theory]
    [InlineData("x", MapDocumentRefusal.BoundsInvalid, "Bounds.X")]
    [InlineData("z", MapDocumentRefusal.BoundsInvalid, "Bounds.Z")]
    [InlineData("legacy", MapDocumentRefusal.LegacyExteriorOpenTopRequired, "Upper.Kind")]
    [InlineData("kind", MapDocumentRefusal.SchemaEnumUnsupported, "Upper.Kind")]
    [InlineData("surface", MapDocumentRefusal.ReferenceMissing, "Upper.SurfaceId")]
    public void BoundAdmission_RetainsTheActualRuleAndItsExistingStatus(string fault, MapDocumentRefusal reason, string path)
    {
        var view = LegacyExteriorFixtures.View("untagged");
        var original = LegacyExteriorFixtures.Footprint(view) with { Upper = new(MapBoundKind.Ceiling, "plane-0", null) };
        var footprint = original with
        {
            Upper = fault switch
            {
                "legacy" => new(MapBoundKind.LegacyExteriorV1, "plane-0", null),
                "kind" => new((MapBoundKind)255, "plane-0", null),
                "surface" => new(MapBoundKind.Ceiling, "missing", null),
                _ => original.Upper,
            }
        };
        var preparation = new MapBoundPreparation(view, new(), null);
        var rect = preparation.Rectangle(original, 0);
        var min = rect[0]; var max = rect[1];
        var plan = preparation.Bound(footprint, footprint.Upper,
            fault == "x" ? min with { X = max.X } : fault == "z" ? min with { Z = max.Z } : min, max);
        Assert.Equal(fault == "surface" ? MapRefinementStatus.MissingGeometry : MapRefinementStatus.Invalid, plan.Status);
        var finding = Assert.IsType<MapDocumentFinding>(plan.Finding);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(path, finding.FieldPath);
        Assert.Equal(footprint.Id, finding.RecordId);
        Assert.Equal(footprint.Space.Id, finding.SpaceId);
        Assert.Empty(plan.Demands);
        if (fault == "kind")
        {
            Assert.Equal(typeof(MapBoundKind), finding.EnumType);
            Assert.Equal(255L, finding.ActualInteger);
        }
        if (fault == "surface") Assert.Equal("missing", finding.SurfaceId);
        Assert.Equal(MapRefinementStatus.Complete, preparation.Bound(original, original.Upper, rect[0], max).Status);
    }

    [Theory]
    [InlineData("reference", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("record", MapDocumentRefusal.PatchUnavailable)]
    [InlineData("kind", MapDocumentRefusal.ReferenceKindMismatch)]
    [InlineData("surface", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("patch", MapDocumentRefusal.PatchUnavailable)]
    public void OpeningAdmission_PreservesMissingGeometryForEachConcreteFailure(string fault, MapDocumentRefusal reason)
    {
        var (surface, patch, opening) = BoundaryFixtures.TwoCellOpening();
        var reference = new MapRecordRef(opening.Id, patch.Key);
        var footprint = new MapSpaceFootprint("footprint", new("space", patch.Key), patch.Key, [0],
            new(MapBoundKind.SupportFloor, surface.Id, null), new(MapBoundKind.HorizontalOpening, null, reference));
        var set = new MapSurfaceSet();
        set.Refs.Add(surface); set.Patches.Add(patch.Key, patch);
        if (fault == "reference") footprint = footprint with { Upper = footprint.Upper with { Opening = null } };
        if (fault == "record") patch.Records.Clear();
        if (fault == "kind") patch.Records[0] = new MapSpaceDoc(opening.Id, MapSpaceKind.Exterior, null, null, [], [], [], []);
        if (fault == "surface") patch.Records[0] = opening with { Patch = new("missing", 0, 0) };
        if (fault == "patch") patch.Records[0] = opening with { Patch = new(surface.Id, 1, 0) };
        var preparation = new MapBoundPreparation(MapScopedSurfaces.CompleteView(set), new(), null);
        var rect = preparation.Rectangle(footprint, 0);
        var plan = preparation.Bound(footprint, footprint.Upper, rect[0], rect[1]);
        Assert.Equal(MapRefinementStatus.MissingGeometry, plan.Status);
        var finding = Assert.IsType<MapDocumentFinding>(plan.Finding);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(fault == "reference" ? "Upper.Opening" : null, finding.FieldPath);
        if (fault != "reference") Assert.Equal(opening.Id, finding.RelatedRecordId);
        if (fault == "kind")
        {
            Assert.Equal(typeof(MapHorizontalOpening), finding.ExpectedRecordType);
            Assert.Equal(typeof(MapSpaceDoc), finding.RecordType);
        }
        Assert.Empty(plan.Demands);
        patch.Records.Clear(); patch.Records.Add(opening);
        footprint = footprint with { Upper = footprint.Upper with { Opening = reference } };
        preparation = new(MapScopedSurfaces.CompleteView(set), new(), null);
        var control = preparation.Bound(footprint, footprint.Upper, rect[0], rect[1]);
        Assert.Equal(MapRefinementStatus.Complete, control.Status);
        Assert.Single(control.Demands);
        Assert.Null(control.Finding);
    }

    [Fact]
    public void MissingBoundPatch_RetainsThePatchReadAndAcceptsTheResidentCell()
    {
        var view = LegacyExteriorFixtures.View("untagged");
        var footprint = LegacyExteriorFixtures.Footprint(view) with { Upper = new(MapBoundKind.Ceiling, "plane-0", null) };
        var preparation = new MapBoundPreparation(view, new(), null);
        var plan = preparation.Bound(footprint, footprint.Upper, new(new(64, 1), new(-1, 1)), new(new(65, 1), new(0, 1)));
        Assert.Equal(MapRefinementStatus.MissingGeometry, plan.Status);
        var finding = Assert.IsType<MapDocumentFinding>(plan.Finding);
        Assert.Equal(MapDocumentRefusal.PatchUnavailable, finding.Reason);
        Assert.Equal(new MapPatchKey("plane-0", 1, 0), finding.Patch);
        Assert.Equal(MapPatchStatus.Unloaded, finding.PatchStatus);
        var rect = preparation.Rectangle(footprint, 0);
        Assert.Equal(MapRefinementStatus.Complete, preparation.Bound(footprint, footprint.Upper, rect[0], rect[1]).Status);
    }

    [Theory]
    [InlineData("range", false, false)]
    [InlineData("range", true, false)]
    [InlineData("faces", false, false)]
    [InlineData("faces", true, false)]
    [InlineData("context", false, false)]
    [InlineData("context", true, false)]
    [InlineData("context", false, true)]
    public void BoundBudgets_KeepTheMeasuredFindingAtTheLimitAndOneBelow(string fault, bool openingBound, bool water)
    {
        var (surface, patch, opening) = BoundaryFixtures.TwoCellOpening();
        if (fault == "range" && openingBound)
        {
            opening = opening with { SlotCells = [0] };
            patch.Records[0] = opening;
        }
        if (!openingBound)
        {
            patch.Records.Clear();
            patch.SetPresent(0, 0, true);
        }
        var set = new MapSurfaceSet(); set.Refs.Add(surface); set.Patches.Add(patch.Key, patch);
        var footprint = new MapSpaceFootprint("footprint", new("space", patch.Key), patch.Key, [0],
            new(MapBoundKind.SupportFloor, surface.Id, null), openingBound
                ? new(MapBoundKind.HorizontalOpening, null, new(opening.Id, patch.Key)) : new(MapBoundKind.Ceiling, surface.Id, null));
        var view = MapScopedSurfaces.CompleteView(set);
        MapBoundPreparation.Plan Run(int limit)
        {
            var limits = fault == "context" ? new MapRefinementLimits(MaxContextPatches: limit)
                : new MapRefinementLimits(MaxSourceFacesPerBound: limit);
            var preparation = new MapBoundPreparation(view, limits, null, water ? new MapWaterWorkMeter(new()) : null);
            var rect = preparation.Rectangle(footprint, 0);
            var max = fault == "range" ? rect[1] with { X = rect[1].X.Add(new(1, 1)) } : rect[1];
            return preparation.Bound(footprint, footprint.Upper, rect[0], max);
        }
        int refusedLimit = fault == "context" ? 0 : 1;
        var refused = Run(refusedLimit);
        Assert.Equal(MapRefinementStatus.CapacityExceeded, refused.Status);
        var finding = Assert.IsType<MapDocumentFinding>(refused.Finding);
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, finding.Reason);
        Assert.Equal(fault == "context" ? "MaxContextPatches" : "MaxSourceFacesPerBound", finding.Budget);
        Assert.Equal((long)refusedLimit + 1, finding.ActualCount);
        Assert.Equal(refusedLimit, finding.Limit);
        Assert.Empty(refused.Demands);
        Assert.Equal(MapRefinementStatus.Complete, Run(refusedLimit + 1).Status);
        Assert.Equal(MapRefinementStatus.Complete, Run(refusedLimit + 2).Status);
    }

    [Theory]
    [InlineData("cave", MapDocumentRefusal.LegacyExteriorSpaceRequired)]
    [InlineData("upper-tag", MapDocumentRefusal.LegacyExteriorOpenTopRequired)]
    [InlineData("foreign-lattice", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    public void InvalidLowerRecipe_PreservesItsOriginalFindingAndInvalidProjection(string variant, MapDocumentRefusal reason)
    {
        var view = LegacyExteriorFixtures.View(variant);
        var footprint = LegacyExteriorFixtures.Footprint(view);
        var preparation = new MapBoundPreparation(view, new(), null);
        var rect = preparation.Rectangle(footprint, 0);
        var plan = preparation.Bound(footprint, footprint.Lower, rect[0], rect[1]);
        Assert.Equal(MapRefinementStatus.Invalid, plan.Status);
        Assert.Equal(reason, plan.Finding!.Reason);
        Assert.Equal(footprint.Id, plan.Finding.RecordId);
        var control = LegacyExteriorFixtures.View("tagged");
        footprint = LegacyExteriorFixtures.Footprint(control);
        preparation = new(control, new(), null); rect = preparation.Rectangle(footprint, 0);
        Assert.Equal(MapRefinementStatus.Complete, preparation.Bound(footprint, footprint.Lower, rect[0], rect[1]).Status);
    }
}
