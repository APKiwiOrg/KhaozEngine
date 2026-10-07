using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>A pinned producer that supports one bounded acquisition across discovery and explicit reads.</summary>
public interface IMapSurfaceAcquisitionSource : IMapSurfaceSource
{
    IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope);
}

/// <summary>One copied request, unique-key allowance and shared work budget against a pinned generation.</summary>
public interface IMapSurfaceAcquisitionSession : IDisposable
{
    string SnapshotId { get; }
    string RootSha256 { get; }
    int PagesRead { get; }
    IReadOnlyList<MapPatchKey> ReservedPatchKeys { get; }
    bool TryGetSurface(string surfaceId, out MapSurfaceRef? surface);
    MapPatchFindResult FindPatches();
    MapPatchRead ReadPatch(MapPatchKey key);
    bool TryGetIncidentRecords(MapPatchKey acquiredKey, out IReadOnlyList<MapRecordRef>? records);
}
