using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Session-owned admission, ordering and served metadata, independent of shared source caches.</summary>
internal sealed class MapSurfaceSessionState
{
    readonly SortedSet<MapPatchKey> _reserved = new();
    readonly Dictionary<MapPatchKey, IReadOnlyList<MapRecordRef>?> _served = new();
    bool _found;
    bool _disposed;

    internal MapSurfaceSessionState(MapSurfaceScope scope, string snapshotId, string rootSha256, MapSurfaceAcquisitionWork? work)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        Scope = scope with
        {
            Roles = Array.AsReadOnly(scope.Roles.ToArray()),
            SpaceIds = scope.SpaceIds is null ? null : Array.AsReadOnly(scope.SpaceIds.ToArray()),
            Limits = scope.Limits with { },
        };
        SnapshotId = snapshotId;
        RootSha256 = rootSha256;
        Budget = new(Scope.Limits.MaxPageReads, work);
    }

    internal MapSurfaceScope Scope { get; }
    internal string SnapshotId { get; }
    internal string RootSha256 { get; }
    internal MapPageBudget Budget { get; }

    internal void BeginFind()
    {
        RequireAlive();
        if (_found) throw new InvalidOperationException("surface acquisition discovery already performed");
        _found = true;
    }

    internal void RequireFound()
    {
        RequireAlive();
        if (!_found) throw new InvalidOperationException("surface acquisition requires discovery first");
    }

    internal void Reserve(MapPatchKey key)
    {
        RequireFound();
        if (_reserved.Contains(key)) return;
        if (_reserved.Count >= Scope.Limits.MaxCandidatePatches) throw new MapSurfaceCapacityException();
        _reserved.Add(key);
    }

    internal IReadOnlyList<MapPatchKey> SnapshotReservedKeys()
    {
        RequireAlive();
        return Array.AsReadOnly(_reserved.ToArray());
    }

    internal bool TryGetSurface(IReadOnlyDictionary<string, MapSurfaceRef> lookup, string surfaceId, out MapSurfaceRef? surface)
    {
        RequireAlive();
        Budget.BeforeMetadata();
        if (Budget.Work is { } work) work.SurfaceLookups++;
        surface = lookup.TryGetValue(surfaceId, out MapSurfaceRef? found) ? found with
        {
            IndoorSpan = found.IndoorSpan is { } span ? span with { DomainTags = Array.AsReadOnly(span.DomainTags.ToArray()) } : null,
        } : null;
        return surface is not null;
    }

    internal void Serve(MapPatchKey key, IReadOnlyList<MapRecordRef>? incidents) => _served[key] = incidents;

    internal MapPatchRead RecordRead(MapPatchRead read)
    {
        _served.TryAdd(read.Key, read.Status == MapPatchStatus.KnownEmpty ? Array.Empty<MapRecordRef>() : null);
        return read;
    }

    internal bool TryGetIncidentRecords(MapPatchKey key, out IReadOnlyList<MapRecordRef>? records)
    {
        RequireFound();
        if (!_served.TryGetValue(key, out IReadOnlyList<MapRecordRef>? incidents))
            throw new InvalidOperationException("surface acquisition key has not been served");
        records = incidents is null ? null : Array.AsReadOnly(incidents.ToArray());
        return records is not null;
    }

    internal void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _served.Clear();
        _reserved.Clear();
        if (Budget.Work is { } work) work.Disposals++;
    }

    void RequireAlive()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(IMapSurfaceAcquisitionSession));
    }
}
