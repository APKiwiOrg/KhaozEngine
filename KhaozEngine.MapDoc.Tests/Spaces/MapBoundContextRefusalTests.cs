using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapBoundContextRefusalTests
{
    [Theory]
    [InlineData("Faces")]
    [InlineData("LocalCompiled")]
    [InlineData("Mask")]
    [InlineData("Compiled")]
    [InlineData("CompiledFace")]
    [InlineData("ValidatedPatch")]
    public void UnpreparedRead_NamesItsDemandAndPreparedControlSucceeds(string operation)
    {
        var (surface, patch, _) = BoundaryFixtures.TwoCellOpening();
        patch.SetPresent(0, 0, true);
        patch.Records.Clear();
        var set = new MapSurfaceSet();
        set.Refs.Add(surface);
        set.Patches.Add(patch.Key, patch);
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(set);
        MapBoundFaceContext empty = MapBoundFaceContext.Prepare(view, [], 1, 2, null, out _)!;
        var face = new MapFaceKey(surface.Id, patch.Key, 0, 0, 0, MapSide.Front);

        MapBoundContextException error = Assert.Throws<MapBoundContextException>(() => Read(empty));
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(MapDocumentRefusal.ContextDemandUnprepared, error.Reason);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal(operation, error.Operation);
        Assert.Equal(operation == "Faces" ? 0 : (int?)null, error.SlotCell);
        Assert.Equal(operation == "CompiledFace" ? face : (MapFaceKey?)null, error.Face);

        MapBoundFaceContext prepared = MapBoundFaceContext.Prepare(view, [new(patch.Key, 0)], 1, 2, null, out string? refusal)!;
        Assert.Null(refusal);
        Assert.NotNull(Read(prepared));

        object Read(MapBoundFaceContext context) => operation switch
        {
            "Faces" => context.Faces(new(patch.Key, 0)),
            "LocalCompiled" => context.LocalCompiled(patch.Key),
            "Mask" => context.Mask(patch.Key),
            "Compiled" => context.Compiled(patch.Key),
            "CompiledFace" => context.CompiledFace(face),
            "ValidatedPatch" => context.ValidatedPatch(patch.Key),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    [Theory]
    [InlineData(true, MapDocumentRefusal.ReferenceMissing)]
    [InlineData(false, MapDocumentRefusal.PatchUnavailable)]
    public void DemandPatch_SeparatesUndeclaredSurfaceFromUnavailablePayload(bool undeclared, MapDocumentRefusal reason)
    {
        var (surface, patch, _) = BoundaryFixtures.TwoCellOpening();
        var set = new MapSurfaceSet();
        if (!undeclared) set.Refs.Add(surface);
        var demand = new MapCellDemand(patch.Key, 0);
        MapDocumentException error = Assert.Throws<MapDocumentException>(() =>
            MapBoundFaceContext.Prepare(MapScopedSurfaces.CompleteView(set), [demand], 1, 2, null, out _));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(surface.Id, error.SurfaceId);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal(undeclared ? null : (MapPatchStatus?)MapPatchStatus.Unloaded, error.PatchStatus);

        if (undeclared) set.Refs.Add(surface);
        set.Patches.Add(patch.Key, patch);
        Assert.NotNull(MapBoundFaceContext.Prepare(MapScopedSurfaces.CompleteView(set), [demand], 1, 2, null, out _));
    }

    [Theory]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("patch", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("cell", MapDocumentRefusal.SpaceOpeningOwnershipInvalid)]
    public void DemandOpening_NamesTheFirstFailedOwnershipRule(string defect, MapDocumentRefusal reason)
    {
        var (surface, patch, opening) = BoundaryFixtures.TwoCellOpening();
        var set = new MapSurfaceSet();
        set.Refs.Add(surface);
        set.Patches.Add(patch.Key, patch);
        int index = patch.Records.FindIndex(record => record.Id == opening.Id);
        if (defect == "missing") patch.Records.RemoveAt(index);
        else patch.Records[index] = defect == "patch" ? opening with { Patch = new("other", 0, 0) }
            : opening with { SlotCells = new[] { 1 } };
        var reference = new MapRecordRef(opening.Id, patch.Key);
        var demand = new MapBoundFaceDemand(new(patch.Key, 0), reference);
        MapDocumentException error = Assert.Throws<MapDocumentException>(() =>
            MapBoundFaceContext.PrepareBounds(MapScopedSurfaces.CompleteView(set), [demand], 1, 2, null, out _));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(opening.Id, error.RecordId);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal(0, error.SlotCell);
        Assert.Equal(defect == "patch" ? new MapPatchKey("other", 0, 0) : (MapPatchKey?)null, error.ActualAnchor);

        if (defect == "missing") patch.Records.Add(opening);
        else patch.Records[index] = opening;
        Assert.NotNull(MapBoundFaceContext.PrepareBounds(MapScopedSurfaces.CompleteView(set), [demand], 1, 2, null, out _));
    }

    [Theory]
    [InlineData(-1, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData(4096, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData(64, MapDocumentRefusal.SurfaceAddressOutOfPatch)]
    [InlineData(0, MapDocumentRefusal.SpaceOpeningCellsInvalid)]
    public void OpeningFaceCount_RejectsCellsOutsideTheAbsentPatchDomain(int slot, MapDocumentRefusal reason)
    {
        var (_, patch, _) = BoundaryFixtures.TwoCellOpening();
        if (slot == 0) patch.SetPresent(0, 0, true);
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapBoundFaceContext.OpeningFaceCount(patch, slot));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal(slot, error.SlotCell);
        patch.SetPresent(0, 0, false);
        patch.SetPresent(patch.Width - 1, patch.Depth - 1, false);
        // The last authored cell of this patch is an accepted absent opening cell.
        int last = (patch.CellMinZ + patch.Depth - 1) * 64 + patch.CellMinX + patch.Width - 1;
        Assert.Equal(2, MapBoundFaceContext.OpeningFaceCount(patch, last));
    }

    [Fact]
    public void FootprintRectangle_MissingLatticeNamesItsSurfaceAndPatch()
    {
        var (view, footprint) = BoundFaceContextFixtures.EightByEight();
        var missing = footprint with { Lattice = new("missing", 0, 0) };
        var preparation = new MapBoundPreparation(view, new(), null);
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => preparation.Rectangle(missing, 0));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal("missing", error.SurfaceId);
        Assert.Equal(missing.Lattice, error.Patch);
        Assert.Equal(2, preparation.Rectangle(footprint, 0).Length);
    }
}
