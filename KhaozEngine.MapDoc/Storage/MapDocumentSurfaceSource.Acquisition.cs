using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

public sealed partial class MapDocumentSurfaceSource
{
    public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope) => OpenAcquisition(scope, null);

    internal IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope, MapSurfaceAcquisitionWork? work) =>
        new Acquisition(this, new(scope, SnapshotId, RootSha256, work));

    sealed class Acquisition(MapDocumentSurfaceSource source, MapSurfaceSessionState state) : IMapSurfaceAcquisitionSession
    {
        public string SnapshotId => state.SnapshotId;
        public string RootSha256 => state.RootSha256;
        public int PagesRead => state.Budget.Reads;
        public IReadOnlyList<MapPatchKey> ReservedPatchKeys => state.SnapshotReservedKeys();

        public bool TryGetSurface(string surfaceId, out MapSurfaceRef? surface) =>
            state.TryGetSurface(source._surfaceLookup, surfaceId, out surface);

        public MapPatchFindResult FindPatches()
        {
            state.BeginFind();
            int before = state.Budget.Reads;
            MapPatchFindResult known = Find(source._index);
            if (ReferenceEquals(source._index, source._residentIndex) || known.Status == MapFindStatus.CapacityExceeded) return known;
            MapPatchFindResult resident = Find(source._residentIndex);
            if (resident.Status == MapFindStatus.CapacityExceeded) return Capacity(before);
            try
            {
                var patches = new SortedDictionary<MapPatchKey, MapPatchRead>();
                foreach (MapPatchRead patch in known.Patches.Concat(resident.Patches)) patches.TryAdd(patch.Key, patch);
                var empty = new List<MapCoveredRange>();
                foreach (MapCoveredRange range in known.KnownEmpty)
                {
                    state.Budget.BeforeRange();
                    var rects = new List<MapSlotRect> { range.Slots };
                    foreach (MapPatchRead patch in patches.Values.Where(p => p.Key.SurfaceId == range.SurfaceId))
                        MapSurfaceQuery.Subtract(rects, MapSurfaceAcquisitionRead.KeyRange(patch.Key), state.Budget);
                    empty.AddRange(rects.Select(r => new MapCoveredRange(range.SurfaceId, r)));
                }
                return known with
                {
                    Patches = Array.AsReadOnly(patches.Values.ToArray()),
                    KnownEmpty = Array.AsReadOnly(empty.ToArray()),
                    PagesRead = state.Budget.Reads - before,
                };
            }
            catch (MapSurfaceCapacityException) { return Capacity(before); }
        }

        MapPatchFindResult Find(MapSurfaceStorageIndex index) => MapSurfaceQuery.FindAcquisition(source, index, state.Scope,
            (dir, budget) => Directory(index, dir, budget),
            (_, page, budget) => Page(index, page, budget),
            entry => Payload(entry), state.Budget, state.Reserve);

        MapPatchFindResult Capacity(int before) => MapSurfaceQuery.Capacity(source, state.Scope, state.Budget) with
        {
            PagesRead = state.Budget.Reads - before,
        };

        public MapPatchRead ReadPatch(MapPatchKey key)
        {
            state.Reserve(key);
            int before = state.Budget.Reads;
            MapPatchRead known = Read(source._index, key);
            if (!ReferenceEquals(source._index, source._residentIndex) && known.Status is MapPatchStatus.KnownEmpty or MapPatchStatus.Unloaded)
            {
                MapPatchRead resident = Read(source._residentIndex, key);
                if (resident.Status == MapPatchStatus.Present) known = resident;
            }
            return state.RecordRead(known with { PagesRead = state.Budget.Reads - before });
        }

        MapPatchRead Read(MapSurfaceStorageIndex index, MapPatchKey key) => MapSurfaceAcquisitionRead.Read(index, key, state.Budget,
            (dir, budget) => Directory(index, dir, budget),
            (_, page, budget) => Page(index, page, budget), Payload);

        static MapPatchStatus Directory(MapSurfaceStorageIndex index, MapDirectoryPageRef dir, MapPageBudget budget)
        {
            if (budget.Work is { } work) work.DirectoryCallbacks++;
            return index.DirectoryPages.ContainsKey(dir.Sha256) ? MapPatchStatus.Present : MapPatchStatus.Unloaded;
        }

        static MapPatchStatus Page(MapSurfaceStorageIndex index, MapIndexPageRef page, MapPageBudget budget)
        {
            if (budget.Work is { } work) work.IndexCallbacks++;
            return index.IndexPages.ContainsKey(page.Sha256) ? MapPatchStatus.Present : MapPatchStatus.Unloaded;
        }

        MapPatchRead Payload(MapSurfaceIndexEntry entry)
        {
            IReadOnlyList<MapRecordRef>? incidents = source._index.ByKey.TryGetValue(entry.Key, out MapSurfaceIndexEntry? stored)
                ? stored.IncidentRecords : null;
            state.Serve(entry.Key, incidents);
            if (state.Budget.Work is { } work) work.PayloadReads++;
            return source._surfaces.Patches.TryGetValue(entry.Key, out MapSurfacePatch? patch)
                ? new(entry.Key, MapPatchStatus.Present, patch.Clone(), entry.SemanticSha256, null, 0)
                : new(entry.Key, entry.Loaded ? MapPatchStatus.KnownEmpty : MapPatchStatus.Unloaded, null, null, null, 0);
        }

        public bool TryGetIncidentRecords(MapPatchKey acquiredKey, out IReadOnlyList<MapRecordRef>? records) =>
            state.TryGetIncidentRecords(acquiredKey, out records);

        public void Dispose() => state.Dispose();
    }
}
