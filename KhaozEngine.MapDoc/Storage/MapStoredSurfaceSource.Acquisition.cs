using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

public sealed partial class MapStoredSurfaceSource
{
    public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope) => OpenAcquisition(scope, null);

    internal IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope, MapSurfaceAcquisitionWork? work) =>
        new Acquisition(this, new(scope, SnapshotId, RootSha256, work));

    sealed class Acquisition(MapStoredSurfaceSource source, MapSurfaceSessionState state) : IMapSurfaceAcquisitionSession
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
            return MapSurfaceQuery.FindAcquisition(source, source.Index, state.Scope, source.Directory, source.Page,
                Payload, state.Budget, state.Reserve);
        }

        public MapPatchRead ReadPatch(MapPatchKey key)
        {
            state.Reserve(key);
            return state.RecordRead(MapSurfaceAcquisitionRead.Read(source.Index, key, state.Budget,
                source.Directory, source.Page, Payload));
        }

        MapPatchRead Payload(MapSurfaceIndexEntry entry)
        {
            state.Serve(entry.Key, entry.IncidentRecords);
            if (state.Budget.Work is { } work) work.PayloadReads++;
            return source.Payload(entry);
        }

        public bool TryGetIncidentRecords(MapPatchKey acquiredKey, out IReadOnlyList<MapRecordRef>? records) =>
            state.TryGetIncidentRecords(acquiredKey, out records);

        public void Dispose() => state.Dispose();
    }
}
