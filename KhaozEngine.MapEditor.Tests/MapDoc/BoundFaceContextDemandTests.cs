using System;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class BoundFaceContextDemandTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OpeningBound_RefusesCellsNotDemandedForThatOwner(bool upper, bool otherOwnerDemanded)
    {
        var (view, footprint, opening) = OpeningFootprint(upper);
        var reference = new MapRecordRef(opening.Id, opening.Patch);
        var demands = new[] { new MapBoundFaceDemand(new(opening.Patch, 0), reference) };
        if (otherOwnerDemanded)
            demands = demands.Append(new MapBoundFaceDemand(new(opening.Patch, 1), new("other", opening.Patch))).ToArray();
        MapBoundFaceContext? context = MapBoundFaceContext.PrepareBounds(view, demands, 1, 4, null, out string? refusal);
        Assert.Null(refusal);
        Assert.NotNull(context);
        MapBoundRef bound = upper ? footprint.Upper : footprint.Lower;
        MapExactXz[] demanded = MapLatticeRanges.CellRect(view.Surfaces.Single().Frame, opening.Patch, 0);
        MapBoundFaceSet faces = MapCommonRefinement.BoundFaces(context, footprint, bound, demanded[0], demanded[1], new());
        Assert.Equal(MapRefinementStatus.Complete, faces.Status);
        Assert.Equal(2, faces.Faces.Count);
        Assert.All(faces.Faces, face => Assert.Equal((opening.Id, 0), (face.Key.OwnerId, face.Key.Primitive)));

        MapExactXz[] undemanded = MapLatticeRanges.CellRect(view.Surfaces.Single().Frame, opening.Patch, 1);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            MapCommonRefinement.BoundFaces(context, footprint, bound, undemanded[0], undemanded[1], new()));
        Assert.Contains("context demand", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FootprintContext_ValidatesEachBoundPatchOnceAcrossAllCellReads(bool directDemands)
    {
        var (view, footprint) = BoundFaceContextFixtures.EightByEight();
        var work = new MapBoundFaceWork();
        MapBoundFaceContext? context;
        if (directDemands)
        {
            var floor = footprint.SlotCells.Select(cell => new MapCellDemand(new("floor8", 0, 0), cell));
            var ceiling = Enumerable.Range(0, 16).SelectMany(z => Enumerable.Range(0, 16)
                .Select(x => new MapCellDemand(new("ceil8", 0, 0), z * 64 + x)));
            context = MapBoundFaceContext.Prepare(view, floor.Concat(ceiling), 2, 640, work, out string? refusal);
            Assert.Null(refusal);
        }
        else
        {
            MapFootprintPreparation prepared = MapCommonRefinement.PrepareFootprints(view, new[] { footprint }, new(), work);
            Assert.Null(prepared.Refusal);
            Assert.Empty(prepared.CellOutcomes);
            context = prepared.Context;
        }
        Assert.NotNull(context);
        // Two patches. The 8 by 8 floor and 16 by 16 roof each have two faces per cell.
        Assert.Equal((2, 2, 640L), (work.PatchValidations, work.Compiles, work.CompiledFaces));
        foreach (int cell in footprint.SlotCells)
        {
            MapCellRefinement result = MapCommonRefinement.RefineFootprintCell(context, footprint, cell, new());
            Assert.Equal(MapRefinementStatus.Complete, result.Status);
            Assert.Equal((new MapExactValue(1, 1), (MapExactValue?)new MapExactValue(1, 1), (MapExactValue?)new MapExactValue(3, 1)),
                (result.LowerArea, result.UpperArea, result.MinSeparation));
            Assert.Equal(2, work.PatchValidations);
        }
        Assert.Equal((2, 2, 640L, 0), (work.PatchValidations, work.Compiles, work.CompiledFaces, view.PatchClones));
    }

    [Fact]
    public void FaceFreeUpperBound_RetainsValidationEvidenceWithoutConsumingCompileBudget()
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(MixedResolutionFixtures.Surface("floor", 1, MapSurfaceRole.SupportFloor));
        set.Refs.Add(MixedResolutionFixtures.Surface("roof", 1, MapSurfaceRole.Ceiling));
        MapSurfacePatch floor = MixedResolutionFixtures.Patch(new("floor", 0, 0), 2, 1, (_, _) => 0);
        MapSurfacePatch roof = MixedResolutionFixtures.Patch(new("roof", 0, 0), 2, 1, (_, _) => 300);
        roof.SetPresent(0, 0, false);
        roof.SetPresent(1, 0, false);
        set.Patches.Add(floor.Key, floor);
        set.Patches.Add(roof.Key, roof);
        MixedResolutionFixtures.Room(floor, "room", "cells", MapSpaceKind.Exterior, floor.Key, new[] { 0, 1 }, "floor", "roof");
        var (view, footprint) = MixedResolutionFixtures.ViewWithFootprint(set);
        var limits = new MapRefinementLimits(MaxContextPatches: 1);
        var work = new MapBoundFaceWork();
        MapFootprintPreparation prepared = MapCommonRefinement.PrepareFootprints(view, new[] { footprint }, limits, work);
        Assert.Null(prepared.Refusal);
        Assert.Empty(prepared.CellOutcomes);
        Assert.NotNull(prepared.Context);
        // Both input patches need validation. Only the two-cell floor needs compilation.
        Assert.Equal((2, 1, 4L), (work.PatchValidations, work.Compiles, work.CompiledFaces));
        foreach (int cell in footprint.SlotCells)
        {
            MapCellRefinement result = MapCommonRefinement.RefineFootprintCell(prepared.Context, footprint, cell, limits);
            Assert.Equal(MapRefinementStatus.Complete, result.Status);
            Assert.Equal((new MapExactValue(1, 1), (MapExactValue?)new MapExactValue(0, 1)), (result.LowerArea, result.UpperArea));
            Assert.Empty(result.Faces);
        }
        Assert.Equal(2, work.PatchValidations);
    }

    [Fact]
    public void OpeningContext_ReusesPatchValidationForPhysicalAndPlaneCompilation()
    {
        var (view, footprint, opening) = OpeningFootprint(false);
        var reference = new MapRecordRef(opening.Id, opening.Patch);
        var work = new MapBoundFaceWork();
        MapBoundFaceContext? context = MapBoundFaceContext.PrepareBounds(view,
            new[] { new MapBoundFaceDemand(new(opening.Patch, 0), reference), new MapBoundFaceDemand(new(opening.Patch, 1), reference) },
            1, 4, work, out string? refusal);
        Assert.Null(refusal);
        Assert.NotNull(context);
        foreach (int cell in new[] { 0, 1 })
        {
            MapExactXz[] rect = MapLatticeRanges.CellRect(view.Surfaces.Single().Frame, opening.Patch, cell);
            MapBoundFaceSet result = MapCommonRefinement.BoundFaces(context, footprint, footprint.Lower, rect[0], rect[1], new());
            Assert.Equal((MapRefinementStatus.Complete, 2), (result.Status, result.Faces.Count));
        }
        Assert.Equal((1, 1, 4L), (work.PatchValidations, work.Compiles, work.CompiledFaces));
    }

    static (MapScopedSurfaces View, MapSpaceFootprint Footprint, MapHorizontalOpening Opening) OpeningFootprint(bool upper)
    {
        var (surface, patch, opening) = BoundaryFixtures.TwoCellOpening();
        patch.Records.Add(new MapHorizontalOpening("other", patch.Key, new[] { 1 }));
        var bound = new MapBoundRef(MapBoundKind.HorizontalOpening, null, new(opening.Id, patch.Key));
        var footprint = new MapSpaceFootprint("opening-cells", new("room", patch.Key), patch.Key, new[] { 0, 1 },
            upper ? new(MapBoundKind.SupportFloor, surface.Id, null) : bound,
            upper ? bound : new(MapBoundKind.OpenTop, null, null));
        var set = new MapSurfaceSet();
        set.Refs.Add(surface);
        set.Patches.Add(patch.Key, patch);
        return (MapScopedSurfaces.CompleteView(set), footprint, opening);
    }
}
