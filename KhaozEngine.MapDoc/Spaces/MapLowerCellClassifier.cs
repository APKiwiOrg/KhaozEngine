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
        if (!view.TryRecord(footprint.Space, out MapTopologyRecord? record, out MapPatchStatus spaceStatus, borrow: true) ||
            record is not MapSpaceDoc space)
            return Result(MapLowerCellClass.Unavailable, $"space '{footprint.Space.Id}' {footprint.Space.Anchor}: {spaceStatus}");
        MapSurfaceRef? surface = view.Surfaces.FirstOrDefault(s => s.Id == footprint.Lower.SurfaceId);
        if (MapLegacyExteriorRecipe.Check(footprint, space, surface) is { } recipe)
            return Result(MapLowerCellClass.InvalidRecipe, recipe);
        if (surface is null) return Result(MapLowerCellClass.Unavailable, $"surface '{footprint.Lower.SurfaceId}' {key}: missing");
        if (!view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out MapPatchStatus status))
            return Result(MapLowerCellClass.Unavailable, $"patch {key}: {status}");
        if (status == MapPatchStatus.KnownEmpty) return Result(MapLowerCellClass.KnownHole, $"patch {key}: {status}");
        if (patch is null) return Result(MapLowerCellClass.Unavailable, $"patch {key}: {status} without payload");
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
                    return Result(MapLowerCellClass.InvalidRecipe, "legacy recipe: height");
                return new(MapLowerCellClass.LegacyNonCapture, key, slot,
                    new MapLegacyCellTag(key, slot, MapLegacyExteriorRecipe.PolicyId), null);
            }
            return Result(MapLowerCellClass.Physical, null);
        }
        catch (MapDocumentException error) { return Result(MapLowerCellClass.Unavailable, $"patch {key}: Corrupt ({error.Message})"); }

        MapLowerCellClassification Result(MapLowerCellClass kind, string? detail) => new(kind, key, slot, null, detail);
    }

    internal static int SlotCell(long x, long z) => (int)((z % 64 + 64) % 64 * 64 + (x % 64 + 64) % 64);
}
