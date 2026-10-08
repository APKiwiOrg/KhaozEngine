using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.MapDoc;

internal sealed class FilteredSurfaceSource : IMapSurfaceAcquisitionSource
{
    readonly IMapSurfaceAcquisitionSource _inner;
    readonly HashSet<MapPatchKey> _unloaded;

    internal FilteredSurfaceSource(IMapSurfaceSource inner, params MapPatchKey[] unloaded)
    {
        _inner = inner as IMapSurfaceAcquisitionSource
            ?? throw new ArgumentException("filtered fixture source requires bounded acquisition", nameof(inner));
        _unloaded = unloaded.ToHashSet();
    }

    public string SnapshotId => _inner.SnapshotId;
    public string RootSha256 => _inner.RootSha256;
    public IReadOnlyList<MapSurfaceRef> Surfaces => _inner.Surfaces;
    public MapPatchRead ReadPatch(MapPatchKey key)
    {
        var scope = new MapSurfaceScope(WorldFrame.Origin, Vector2.Zero, Vector2.Zero, null, null,
            Array.Empty<MapSurfaceRole>(), null, new MapQueryLimits());
        using IMapSurfaceAcquisitionSession session = OpenAcquisition(scope);
        if (session.FindPatches().Status == MapFindStatus.CapacityExceeded) throw new MapSurfaceCapacityException();
        return session.ReadPatch(key);
    }

    public MapPatchFindResult FindPatches(MapSurfaceScope scope)
    {
        using IMapSurfaceAcquisitionSession session = OpenAcquisition(scope);
        return session.FindPatches();
    }

    public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope) => new Acquisition(this, _inner.OpenAcquisition(scope));

    MapPatchRead Filter(MapPatchRead read) => !_unloaded.Contains(read.Key) ? read : read with
    {
        Status = MapPatchStatus.Unloaded,
        Patch = null,
        SemanticSha256 = null,
        Detail = "filtered fixture patch is unloaded",
    };

    sealed class Acquisition(FilteredSurfaceSource source, IMapSurfaceAcquisitionSession inner) : IMapSurfaceAcquisitionSession
    {
        public string SnapshotId => inner.SnapshotId;
        public string RootSha256 => inner.RootSha256;
        public int PagesRead => inner.PagesRead;
        public IReadOnlyList<MapPatchKey> ReservedPatchKeys => inner.ReservedPatchKeys;
        public bool TryGetSurface(string surfaceId, out MapSurfaceRef? surface) => inner.TryGetSurface(surfaceId, out surface);
        public MapPatchRead ReadPatch(MapPatchKey key) => source.Filter(inner.ReadPatch(key));

        public MapPatchFindResult FindPatches()
        {
            int before = inner.PagesRead;
            MapPatchFindResult found = inner.FindPatches();
            if (found.Status == MapFindStatus.CapacityExceeded) return found;
            MapPatchRead[] reads = found.Patches.Select(source.Filter).ToArray();
            var unavailable = new SortedDictionary<MapPatchKey, MapPatchRead>();
            foreach (MapPatchRead read in found.Unavailable.Select(source.Filter).Concat(reads.Where(p => p.Status == MapPatchStatus.Unloaded)))
                unavailable[read.Key] = read;
            var empty = found.KnownEmpty.ToList();
            try
            {
                foreach (MapPatchKey key in source._unloaded.Order())
                {
                    if (!empty.Any(range => Contains(range, key))) continue;
                    unavailable[key] = ReadPatch(key);
                    for (int i = empty.Count - 1; i >= 0; i--)
                    {
                        MapCoveredRange range = empty[i];
                        if (!Contains(range, key)) continue;
                        empty.RemoveAt(i);
                        MapSlotRect slots = range.Slots;
                        long maxX = checked(key.SlotX + 1), maxZ = checked(key.SlotZ + 1);
                        if (slots.MinX < key.SlotX) empty.Add(new(range.SurfaceId, new(slots.MinX, slots.MinZ, key.SlotX, slots.MaxZExclusive)));
                        if (maxX < slots.MaxXExclusive) empty.Add(new(range.SurfaceId, new(maxX, slots.MinZ, slots.MaxXExclusive, slots.MaxZExclusive)));
                        if (slots.MinZ < key.SlotZ) empty.Add(new(range.SurfaceId, new(key.SlotX, slots.MinZ, maxX, key.SlotZ)));
                        if (maxZ < slots.MaxZExclusive) empty.Add(new(range.SurfaceId, new(key.SlotX, maxZ, maxX, slots.MaxZExclusive)));
                    }
                }
            }
            catch (MapSurfaceCapacityException)
            {
                return found with
                {
                    Status = MapFindStatus.CapacityExceeded,
                    Patches = Array.Empty<MapPatchRead>(),
                    KnownEmpty = Array.Empty<MapCoveredRange>(),
                    Unavailable = Array.Empty<MapPatchRead>(),
                    PagesRead = inner.PagesRead - before,
                };
            }
            return found with
            {
                Status = unavailable.Count == 0 ? found.Status : MapFindStatus.Incomplete,
                Patches = Array.AsReadOnly(reads.Where(p => p.Status == MapPatchStatus.Present).ToArray()),
                KnownEmpty = Array.AsReadOnly(empty.ToArray()),
                Unavailable = Array.AsReadOnly(unavailable.Values.ToArray()),
                PagesRead = inner.PagesRead - before,
            };
        }

        public bool TryGetIncidentRecords(MapPatchKey acquiredKey, out IReadOnlyList<MapRecordRef>? records)
        {
            bool known = inner.TryGetIncidentRecords(acquiredKey, out records);
            if (!source._unloaded.Contains(acquiredKey)) return known;
            records = null;
            return false;
        }

        public void Dispose() => inner.Dispose();

        static bool Contains(MapCoveredRange range, MapPatchKey key) => range.SurfaceId == key.SurfaceId &&
            key.SlotX >= range.Slots.MinX && key.SlotX < range.Slots.MaxXExclusive &&
            key.SlotZ >= range.Slots.MinZ && key.SlotZ < range.Slots.MaxZExclusive;
    }
}
