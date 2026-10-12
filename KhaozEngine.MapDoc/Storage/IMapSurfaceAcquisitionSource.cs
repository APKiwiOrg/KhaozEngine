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

/// <summary>Optional pre-visit observation for composing independently bounded sessions.</summary>
/// <remarks>When CanObservePageVisits is true, PageVisiting is raised synchronously before every directory or index
/// page visit, including cached and unavailable pages. Page traversal starts in session operations after
/// OpenAcquisition returns. A handler can throw MapSurfaceCapacityException to refuse
/// the visit before its callback or payload work. The capability remains fixed for the session lifetime. Adapters
/// can forward this contract without retaining private fields on a copied request.</remarks>
public interface IMapSurfacePageVisitSession : IMapSurfaceAcquisitionSession
{
    bool CanObservePageVisits { get; }
    event Action? PageVisiting;
}
