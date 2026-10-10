using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Local page traversal. Capacity refusal publishes no partial acquisition.</summary>
internal static class MapSurfaceQuery
{
    internal static MapPatchFindResult Find(IMapSurfaceSource source, MapSurfaceStorageIndex index, MapSurfaceScope scope,
        Func<MapDirectoryPageRef, MapPageBudget, MapPatchStatus> directory,
        Func<MapDirectoryPageRef, MapIndexPageRef, MapPageBudget, MapPatchStatus> page,
        Func<MapSurfaceIndexEntry, MapPatchRead> payload, MapPageBudget? sharedBudget = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        var budget = sharedBudget ?? new MapPageBudget(scope.Limits.MaxPageReads);
        try { return FindCore(source, index, scope, directory, page, payload, budget, null); }
        catch (MapSurfaceCapacityException) { return Capacity(source, scope, budget); }
        catch (Exception ex) when (ex is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException("surface scope not representable", ex);
        }
    }

    internal static MapPatchFindResult FindAcquisition(IMapSurfaceSource source, MapSurfaceStorageIndex index, MapSurfaceScope scope,
        Func<MapDirectoryPageRef, MapPageBudget, MapPatchStatus> directory,
        Func<MapDirectoryPageRef, MapIndexPageRef, MapPageBudget, MapPatchStatus> page,
        Func<MapSurfaceIndexEntry, MapPatchRead> payload, MapPageBudget budget, Action<MapPatchKey> reserve)
    {
        int before = budget.Reads;
        try { return FindCore(source, index, scope, directory, page, payload, budget, reserve) with { PagesRead = budget.Reads - before }; }
        catch (MapSurfaceCapacityException) { return Capacity(source, scope, budget) with { PagesRead = budget.Reads - before }; }
    }

    static MapPatchFindResult FindCore(IMapSurfaceSource source, MapSurfaceStorageIndex index, MapSurfaceScope scope,
        Func<MapDirectoryPageRef, MapPageBudget, MapPatchStatus> directory,
        Func<MapDirectoryPageRef, MapIndexPageRef, MapPageBudget, MapPatchStatus> page,
        Func<MapSurfaceIndexEntry, MapPatchRead> payload, MapPageBudget budget, Action<MapPatchKey>? reserve)
    {
        var candidates = new SortedDictionary<MapPatchKey, MapSurfaceIndexEntry>();
        var empty = new List<MapCoveredRange>();
        var unavailable = new List<MapPatchRead>();
        MapExactRect world = scope.WorldRectangle();
        foreach (MapSurfaceRef surface in source.Surfaces)
        {
            budget.BeforeMetadata();
            if (!scope.Roles.Contains(surface.Role)) continue;
            if (reserve is not null) budget.BeforeRange();
            MapCellRect cells = MapSurfaceRanges.Cells(world, surface.Frame);
            MapSlotRect slots;
            try { slots = MapSurfaceRanges.Slots(cells); }
            catch (OverflowException) when (reserve is not null) { throw new MapExactOverflowException(); }
            if (reserve is null) budget.BeforeRange();
            var emptyRects = new List<MapSlotRect> { slots };
            foreach (MapDirectoryPageRef dir in index.Covering(surface.Id, slots, budget))
            {
                budget.BeforeVisit();
                MapPatchStatus ds = directory(dir, budget);
                if (ds != MapPatchStatus.Present)
                {
                    Unavailable(surface.Id, dir.Covers, ds, unavailable, emptyRects, budget);
                    continue;
                }
                foreach (MapIndexPageRef ip in index.DirectoryPages[dir.Sha256])
                {
                    if (!ip.Covers.Overlaps(slots)) continue;
                    budget.BeforeVisit();
                    MapPatchStatus ps = page(dir, ip, budget);
                    if (ps != MapPatchStatus.Present)
                    {
                        Unavailable(surface.Id, ip.Covers, ps, unavailable, emptyRects, budget);
                        continue;
                    }
                    foreach (MapSurfaceIndexEntry stored in index.IndexPages[ip.Sha256])
                    {
                        if (!slots.Contains(stored.Key.SlotX, stored.Key.SlotZ)) continue;
                        Subtract(emptyRects, KeyRange(stored.Key), budget);
                        MapSurfaceIndexEntry entry = index.ByKey[stored.Key];
                        if (!entry.Cells.Overlaps(cells) || !HeightAndSpace(entry, surface, scope)) continue;
                        if (candidates.Count >= scope.Limits.MaxCandidatePatches) throw new MapSurfaceCapacityException();
                        candidates.Add(entry.Key, entry);
                    }
                }
            }
            empty.AddRange(emptyRects.Select(r => new MapCoveredRange(surface.Id, r)));
        }
        var patches = new List<MapPatchRead>();
        foreach (MapSurfaceIndexEntry entry in candidates.Values)
        {
            reserve?.Invoke(entry.Key);
            MapPatchRead read = payload(entry);
            if (read.Status == MapPatchStatus.Present) patches.Add(read);
            else if (read.Status == MapPatchStatus.KnownEmpty)
            {
                budget.BeforeRange();
                empty.Add(new(entry.Key.SurfaceId, KeyRange(entry.Key)));
            }
            else unavailable.Add(read);
        }
        return new(unavailable.Count == 0 ? MapFindStatus.Complete : MapFindStatus.Incomplete, scope, source.SnapshotId,
            Array.AsReadOnly(patches.ToArray()), Array.AsReadOnly(empty.ToArray()), Array.AsReadOnly(unavailable.ToArray()), budget.Reads);

        MapSlotRect KeyRange(MapPatchKey key) => reserve is not null ? MapSurfaceAcquisitionRead.KeyRange(key) :
            new(key.SlotX, key.SlotZ, checked(key.SlotX + 1), checked(key.SlotZ + 1));
    }
    internal static MapPatchFindResult Capacity(IMapSurfaceSource source, MapSurfaceScope scope, MapPageBudget budget) =>
        new(MapFindStatus.CapacityExceeded, scope, source.SnapshotId, Array.Empty<MapPatchRead>(),
            Array.Empty<MapCoveredRange>(), Array.Empty<MapPatchRead>(), budget.Reads);
    static bool HeightAndSpace(MapSurfaceIndexEntry entry, MapSurfaceRef surface, MapSurfaceScope scope)
    {
        MapExactValue min = surface.Frame.Metres(new(entry.MinHeightUnits, 1)), max = surface.Frame.Metres(new(entry.MaxHeightUnits, 1));
        return !(scope.MinY is { } low && max.CompareTo(MapExactValue.FromSingle(low)) < 0) &&
            !(scope.MaxY is { } high && min.CompareTo(MapExactValue.FromSingle(high)) > 0) &&
            (scope.SpaceIds is null || entry.SpaceIds.Any(scope.SpaceIds.Contains) ||
                surface.IndoorSpan is { } span && scope.SpaceIds.Contains(span.ParentSpace.Id));
    }
    static void Unavailable(string surface, MapSlotRect covers, MapPatchStatus status, List<MapPatchRead> unavailable,
        List<MapSlotRect> empty, MapPageBudget budget)
    {
        unavailable.Add(new(new(surface, covers.MinX, covers.MinZ), status, null, null, "covering page unavailable", 0));
        Subtract(empty, covers, budget);
    }
    internal static void Subtract(List<MapSlotRect> ranges, MapSlotRect cut, MapPageBudget budget)
    {
        for (int i = ranges.Count - 1; i >= 0; i--)
        {
            MapSlotRect r = ranges[i];
            if (!r.Overlaps(cut)) continue;
            ranges.RemoveAt(i);
            long x0 = Math.Max(r.MinX, cut.MinX), x1 = Math.Min(r.MaxXExclusive, cut.MaxXExclusive);
            long z0 = Math.Max(r.MinZ, cut.MinZ), z1 = Math.Min(r.MaxZExclusive, cut.MaxZExclusive);
            if (r.MinX < x0) Add(r.MinX, r.MinZ, x0, r.MaxZExclusive);
            if (x1 < r.MaxXExclusive) Add(x1, r.MinZ, r.MaxXExclusive, r.MaxZExclusive);
            if (r.MinZ < z0) Add(x0, r.MinZ, x1, z0);
            if (z1 < r.MaxZExclusive) Add(x0, z1, x1, r.MaxZExclusive);
        }
        void Add(long minX, long minZ, long maxX, long maxZ)
        {
            budget.BeforeRange();
            ranges.Add(new(minX, minZ, maxX, maxZ));
        }
    }
}
