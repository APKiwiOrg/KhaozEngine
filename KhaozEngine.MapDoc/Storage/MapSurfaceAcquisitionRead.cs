using System;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Reads one reserved key through budgeted indexed callbacks, never an unbounded status scan.</summary>
internal static class MapSurfaceAcquisitionRead
{
    internal static MapPatchRead Read(MapSurfaceStorageIndex index, MapPatchKey key, MapPageBudget budget,
        Func<MapDirectoryPageRef, MapPageBudget, MapPatchStatus> directory,
        Func<MapDirectoryPageRef, MapIndexPageRef, MapPageBudget, MapPatchStatus> page,
        Func<MapSurfaceIndexEntry, MapPatchRead> payload)
    {
        int before = budget.Reads;
        budget.BeforeRange();
        MapSlotRect slots = KeyRange(key);
        foreach (MapDirectoryPageRef dir in index.Covering(key.SurfaceId, slots, budget))
        {
            budget.BeforeVisit();
            MapPatchStatus ds = directory(dir, budget);
            if (ds != MapPatchStatus.Present) return Result(ds, "directory unavailable");
            foreach (MapIndexPageRef ip in index.DirectoryPages[dir.Sha256])
            {
                if (!ip.Covers.Contains(key.SlotX, key.SlotZ)) continue;
                budget.BeforeVisit();
                MapPatchStatus ps = page(dir, ip, budget);
                if (ps != MapPatchStatus.Present) return Result(ps, "index unavailable");
            }
        }
        return index.ByKey.TryGetValue(key, out MapSurfaceIndexEntry? entry)
            ? payload(entry) with { PagesRead = budget.Reads - before }
            : Result(MapPatchStatus.KnownEmpty, null);

        MapPatchRead Result(MapPatchStatus status, string? detail) => new(key, status, null, null, detail, budget.Reads - before);
    }

    internal static MapSlotRect KeyRange(MapPatchKey key)
    {
        try { return new(key.SlotX, key.SlotZ, checked(key.SlotX + 1), checked(key.SlotZ + 1)); }
        catch (OverflowException) { throw new MapExactOverflowException(); }
    }
}
