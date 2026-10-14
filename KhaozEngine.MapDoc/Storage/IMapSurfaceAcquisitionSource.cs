using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>A pinned producer that supports one bounded acquisition across discovery and explicit reads.</summary>
/// <remarks>Effective prefab composition opens one session per native pose group beside its generated part. Every part
/// shares one MapQueryLimits ceiling for page visits, metadata inspections and rectangle additions, so composition admits
/// that work before it happens through the optional observation capabilities below. With more than one native group,
/// each native session must report IMapSurfacePageVisitSession.CanObservePageVisits. With more than one part of any kind,
/// each native session must report IMapSurfaceWorkSession.CanObserveWork. Otherwise composition refuses before any
/// traversal with CapacityExceeded and a diagnostic naming the missing capability. Raising MapQueryLimits cannot cure that
/// refusal. A producer or adapter cures it by raising or forwarding the observation events. One native session that is
/// the only part keeps its own whole allowance without observation.</remarks>
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

/// <summary>Optional before-work observation of metadata inspections and rectangle additions.</summary>
/// <remarks>When CanObserveWork is true, MetadataInspecting is raised synchronously before every surface-ref inspection,
/// surface lookup and lookup-node bounds check, including excluded roles and rejected nodes. RangeAdding is raised
/// synchronously before every rectangle seed, split addition and merge addition, including work that visits no page.
/// A handler can throw MapSurfaceCapacityException to refuse that work before it happens. The capability remains fixed
/// for the session lifetime. Adapters forward these events as they forward PageVisiting, without retaining private
/// fields on a copied request.</remarks>
public interface IMapSurfaceWorkSession : IMapSurfaceAcquisitionSession
{
    bool CanObserveWork { get; }
    event Action? MetadataInspecting;
    event Action? RangeAdding;
}
