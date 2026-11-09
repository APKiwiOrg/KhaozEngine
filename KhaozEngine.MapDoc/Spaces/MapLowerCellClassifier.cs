using System;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>The ordered D9 policy for physical lower cells and explicit non-capture legacy coverage.</summary>
public static class MapLowerCellClassifier
{
    public static MapLowerCellClassification Classify(MapScopedSurfaces view, MapSpaceFootprint footprint,
        long cellX, long cellZ)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(footprint);
        if (footprint.Lower.Kind is not (MapBoundKind.SupportFloor or MapBoundKind.LegacyExteriorV1))
            throw new ArgumentException("lower cell requires a floor bound", nameof(footprint));
        MapPatchKey key = MapPatchKey.ForCell(footprint.Lower.SurfaceId ?? footprint.Lattice.SurfaceId, cellX, cellZ);
        int slot = SlotCell(cellX, cellZ);
        bool found = view.TryRecord(footprint.Space, out MapTopologyRecord? record, out MapPatchStatus spaceStatus, borrow: true);
        if (!found ||
            record is not MapSpaceDoc space)
            return Failure(MapLowerCellClass.Unavailable, Context(footprint, key, slot,
                !found ? MapDocumentRefusal.PatchUnavailable : MapDocumentRefusal.ReferenceKindMismatch,
                $"space '{footprint.Space.Id}' {footprint.Space.Anchor}: {spaceStatus}") with
            {
                Patch = footprint.Space.Anchor,
                PatchStatus = spaceStatus,
                RelatedRecordId = footprint.Space.Id,
                ExpectedRecordType = typeof(MapSpaceDoc),
                RecordType = record?.GetType()
            });
        MapSurfaceRef? surface = view.Surfaces.FirstOrDefault(s => s.Id == footprint.Lower.SurfaceId);
        if (MapLegacyExteriorRecipe.CheckFinding(footprint, space, surface) is { } recipe)
            return Failure(MapLowerCellClass.InvalidRecipe, recipe);
        if (surface is null) return Failure(MapLowerCellClass.Unavailable, Context(footprint, key, slot,
            MapDocumentRefusal.ReferenceMissing, $"surface '{footprint.Lower.SurfaceId}' {key}: missing"));
        if (!view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out MapPatchStatus status))
            return Failure(MapLowerCellClass.Unavailable, Context(footprint, key, slot,
                MapDocumentRefusal.PatchUnavailable, $"patch {key}: {status}") with
            { PatchStatus = status });
        if (status == MapPatchStatus.KnownEmpty) return Result(MapLowerCellClass.KnownHole, $"patch {key}: {status}");
        if (patch is null) return Failure(MapLowerCellClass.Unavailable, Context(footprint, key, slot,
            MapDocumentRefusal.PatchUnavailable, $"patch {key}: {status} without payload") with
        { PatchStatus = status });
        return ClassifyPatch(footprint, surface, patch, key, slot);

        MapLowerCellClassification Result(MapLowerCellClass kind, string? detail) => new(kind, key, slot, null, detail);
        MapLowerCellClassification Failure(MapLowerCellClass kind, MapDocumentFinding finding) =>
            Result(kind, finding.Message) with { Finding = finding };
    }

    // Fault values enter the same patch classification as a borrowed, admitted snapshot. No snapshot is mutated.
    internal static MapLowerCellClassification ClassifyPatchWithFaultForTests(MapSpaceFootprint footprint,
        MapSurfaceRef surface, MapSurfacePatch patch, int slot) => ClassifyPatch(footprint, surface, patch, patch.Key, slot);

    static MapLowerCellClassification ClassifyPatch(MapSpaceFootprint footprint, MapSurfaceRef surface,
        MapSurfacePatch patch, MapPatchKey key, int slot)
    {
        int x = slot % 64 - patch.CellMinX, z = slot / 64 - patch.CellMinZ;
        if (x < 0 || x >= patch.Width || z < 0 || z >= patch.Depth)
            return Result(MapLowerCellClass.KnownHole, $"patch {key}: cell outside rectangle");
        try
        {
            if (!patch.IsPresent(x, z)) return Result(MapLowerCellClass.KnownHole, $"patch {key}: presence 0");
            if (MapSurfaceCompiler.Fallback(surface, patch.Cells[z * patch.Width + x]))
            {
                if (footprint.Lower.Kind == MapBoundKind.SupportFloor) return Result(MapLowerCellClass.UntaggedLegacy, null);
                int h00 = patch.Height(x, z), h10 = patch.Height(x + 1, z);
                int h01 = patch.Height(x, z + 1), h11 = patch.Height(x + 1, z + 1);
                if (h00 is < short.MinValue or > short.MaxValue || h10 is < short.MinValue or > short.MaxValue ||
                    h01 is < short.MinValue or > short.MaxValue || h11 is < short.MinValue or > short.MaxValue)
                {
                    string field = h00 is < short.MinValue or > short.MaxValue ? "h00" : h10 is < short.MinValue or > short.MaxValue
                        ? "h10" : h01 is < short.MinValue or > short.MaxValue ? "h01" : "h11";
                    int height = field == "h00" ? h00 : field == "h10" ? h10 : field == "h01" ? h01 : h11;
                    return Result(MapLowerCellClass.InvalidRecipe, "legacy recipe: height", Context(footprint, key, slot,
                        MapDocumentRefusal.ValueOutsideRange, "legacy recipe: height") with
                    { FieldPath = field, ActualInteger = height, MinimumInteger = short.MinValue, MaximumInteger = short.MaxValue });
                }
                return new(MapLowerCellClass.LegacyNonCapture, key, slot,
                    new MapLegacyCellTag(key, slot, MapLegacyExteriorRecipe.PolicyId), null);
            }
            return Result(MapLowerCellClass.Physical, null);
        }
        catch (MapDocumentException error)
        { return Result(MapLowerCellClass.Unavailable, $"patch {key}: Corrupt ({error.Message})", MapDocumentFinding.FromException(error)); }

        MapLowerCellClassification Result(MapLowerCellClass kind, string? detail, MapDocumentFinding? finding = null) =>
            new(kind, key, slot, null, detail) { Finding = finding };
    }

    static MapDocumentFinding Context(MapSpaceFootprint footprint, MapPatchKey key, int slot,
        MapDocumentRefusal reason, string detail) => new(reason, detail)
        { RecordId = footprint.Id, SpaceId = footprint.Space.Id, SurfaceId = footprint.Lower.SurfaceId, Patch = key, SlotCell = slot };

    internal static int SlotCell(long x, long z) => (int)((z % 64 + 64) % 64 * 64 + (x % 64 + 64) % 64);
}
