using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>One session for discovery, breadth-first record acquisition and bound preflight.</summary>
internal sealed class MapEffectiveAcquisition
{
    readonly IMapSurfaceAcquisitionSession _session;
    readonly MapEffectiveContext _context;
    readonly MapPageBudget _boundBudget;
    readonly MapSurfaceScope _scope;
    readonly MapAcquisitionWork? _work;
    readonly MapAcquiredFacts _facts = new();
    readonly SortedSet<MapPatchKey> _reserved = new();
    readonly SortedSet<Need> _pending = new();
    readonly HashSet<MapRecordRef> _scheduled = new();
    readonly string _snapshot, _root;
    int _recordReads;
    bool _unknownIncidents;

    MapEffectiveAcquisition(IMapSurfaceAcquisitionSession session, MapSurfaceScope scope, MapAcquisitionWork? work, MapEffectiveContext context)
    {
        _session = session;
        _context = context;
        _boundBudget = new(scope.Limits.MaxPageReads, source: scope.Limits);
        _scope = scope;
        _work = work;
        _snapshot = session.SnapshotId;
        _root = session.RootSha256;
        if (string.IsNullOrWhiteSpace(_snapshot)) throw new MapDocumentException("invalid acquisition snapshot");
        MapSurfaceStorageLayout.RequireDigest(_root);
    }
    internal static MapScopedAcquisition.Result Run(IMapSurfaceAcquisitionSource source, MapSurfaceScope scope, MapAcquisitionWork? work, MapEffectiveContext context)
    {
        using IMapSurfaceAcquisitionSession session = source.OpenAcquisition(scope);
        var acquisition = new MapEffectiveAcquisition(session, scope, work, context);
        if (source.SnapshotId != session.SnapshotId || source.RootSha256 != session.RootSha256)
            throw new MapDocumentException("acquisition session differs from its pinned source");
        return acquisition.Run();
    }
    MapScopedAcquisition.Result Run()
    {
        using var demandScope = DemandDiagnostics.Enter(this, "MapEffectiveAcquisition");
        string phase = "discovery";
        try
        {
            Discover();
            phase = "record expansion";
            ExpandRecords();
            phase = "bound slots";
            IReadOnlyList<MapPatchKey> bounds = CollectBounds();
            foreach (MapPatchKey key in bounds) Read(key, recordAnchor: false);
            return Finish(_facts.Complete ? MapAcquireStatus.Complete : MapAcquireStatus.Incomplete,
                _facts.Complete ? null : _unknownIncidents ? "incomplete incident records" : "incomplete surface acquisition", _facts);
        }
        catch (MapSurfaceCapacityException ex) { return Finish(MapAcquireStatus.CapacityExceeded, ex.Capacity is not null ? phase + " " + ex.Message : ex.Diagnostic ?? phase + " capacity exceeded", new()) with { Capacity = ex.Capacity }; }
        catch (MapExactOverflowException) { return Finish(MapAcquireStatus.NotRepresentable, "exact value overflow", new()); }
    }
    MapScopedAcquisition.Result Finish(MapAcquireStatus status, string? detail, MapAcquiredFacts facts) =>
        new(status, detail, facts, _snapshot, _root, _session.PagesRead, _recordReads);

    IReadOnlyList<MapPatchKey> CollectBounds()
    {
        var keys = new SortedSet<MapPatchKey>();
        var window = _scope.WorldRectangle();
        foreach (var footprint in _facts.OwnedRecords.OfType<MapSpaceFootprint>())
            foreach (int cell in footprint.SlotCells)
            {
                _boundBudget.BeforeMetadata();
                var bounds = MapEffectiveGeometry.Bounds(MapEffectiveGeometry.CellPolygon(_context, footprint.Lattice, cell));
                if (!window.Touches(bounds)) continue;
                foreach (var bound in new[] { footprint.Lower, footprint.Upper })
                {
                    if (bound.SurfaceId is not { } id) continue;
                    Surface(id);
                    var range = MapEffectiveGeometry.CellRange(_context, id, new(bounds.MinX, bounds.MinZ), new(bounds.MaxX, bounds.MaxZ));
                    Collect(id, range);
                }
            }
        // Incident records can introduce an offsite transformed floor. Support queries still inspect its column, so
        // capture empty slots as evidence rather than treating an undiscovered local key as unloaded.
        foreach (var surface in _facts.Surfaces.Where(s => s.Role == MapSurfaceRole.SupportFloor &&
            _context.Transform(MapPrefabChildKind.Surface, s.Id) is not null))
        {
            _boundBudget.BeforeMetadata();
            Collect(surface.Id, MapEffectiveGeometry.CellRange(_context, surface.Id,
                new(window.MinX, window.MinZ), new(window.MaxX, window.MaxZ)), knownCoverage: true);
        }
        return Array.AsReadOnly(keys.ToArray());

        void Collect(string id, MapCellRect range, bool knownCoverage = false)
        {
            var slots = MapSurfaceRanges.Slots(range);
            long gross;
            try { gross = checked((slots.MaxXExclusive - slots.MinX) * (slots.MaxZExclusive - slots.MinZ)); }
            catch (OverflowException) { throw new MapSurfaceCapacityException(); }
            if (DemandDiagnostics.Check(this, "MapEffectiveAcquisition.MaxCandidatePatches.1", "MaxCandidatePatches", 0, gross, _scope.Limits.MaxCandidatePatches, gross > _scope.Limits.MaxCandidatePatches, CapacityDemandLifetime.Transient, allowanceSource: _scope.Limits)) throw new MapSurfaceCapacityException(nameof(MapQueryLimits.MaxCandidatePatches), gross, _scope.Limits.MaxCandidatePatches);
            for (long z = slots.MinZ; z < slots.MaxZExclusive; z++)
                for (long x = slots.MinX; x < slots.MaxXExclusive; x++)
                {
                    var key = new MapPatchKey(id, x, z);
                    if (knownCoverage && _facts.IsKnownEmpty(key)) continue;
                    if (!_reserved.Contains(key)) keys.Add(key);
                    if (DemandDiagnostics.Check(this, "MapEffectiveAcquisition.MaxCandidatePatches.2", "MaxCandidatePatches", _reserved.Count, keys.Count, _scope.Limits.MaxCandidatePatches, keys.Count > _scope.Limits.MaxCandidatePatches - _reserved.Count, CapacityDemandLifetime.Inventory, allowanceSource: _scope.Limits)) throw new MapSurfaceCapacityException(nameof(MapQueryLimits.MaxCandidatePatches), (long)keys.Count + _reserved.Count, _scope.Limits.MaxCandidatePatches);
                }
        }
    }

    void Discover()
    {
        int before = _session.PagesRead;
        MapPatchFindResult found = _session.FindPatches();
        CheckPages(before, found.PagesRead);
        if (found.SnapshotId != _snapshot || found.Scope is null || found.Scope.Digest != _scope.Digest || !Enum.IsDefined(found.Status))
            throw new MapDocumentException("incoherent acquisition discovery snapshot or scope");
        foreach (MapPatchKey key in ReservationSnapshot()) _reserved.Add(key);
        if (found.Status == MapFindStatus.CapacityExceeded) throw new MapSurfaceCapacityException(found.Diagnostic, found.Capacity);
        foreach (MapCoveredRange range in found.KnownEmpty)
        {
            Surface(range.SurfaceId);
            _facts.AddEmpty(range);
        }
        foreach (MapPatchRead read in found.Patches)
        {
            if (read.Status != MapPatchStatus.Present || !_reserved.Contains(read.Key))
                throw new MapDocumentException("unreserved discovery payload");
            Surface(read.Key.SurfaceId);
            _facts.AddRead(read, ownsPayload: true);
        }
        foreach (MapPatchRead read in found.Unavailable)
        {
            MapAcquiredFacts.ValidateKey(read.Key);
            if (read.Status is not (MapPatchStatus.Missing or MapPatchStatus.Corrupt or MapPatchStatus.Unloaded) || read.Patch is not null)
                throw new MapDocumentException("incoherent unavailable discovery fact");
            Surface(read.Key.SurfaceId);
            if (_reserved.Contains(read.Key)) _facts.AddRead(read);
            else _facts.AddUnavailable("patch", read.Key, read.Status);
        }
        foreach (MapPatchKey key in _reserved)
        {
            if (!_facts.HasRead(key))
            {
                if (!found.KnownEmpty.Any(r => r.SurfaceId == key.SurfaceId && r.Slots.Contains(key.SlotX, key.SlotZ)))
                    throw new MapDocumentException("reserved discovery key has no fact");
                _facts.AddRead(new(key, MapPatchStatus.KnownEmpty, null, null, null, 0));
            }
        }
        ReconcileReservations();
        foreach (MapPatchKey key in _reserved) Expand(key, 0);
    }
    void ExpandRecords()
    {
        while (_pending.Count != 0)
        {
            Need need = _pending.Min;
            _pending.Remove(need);
            MapRecordRef reference = need.Reference;
            if (_facts.HasRecord(reference)) continue;
            if (!_facts.HasRead(reference.Anchor))
            {
                if (DemandDiagnostics.Check(this, "MapEffectiveAcquisition.MaxRecordDepth.1", "MaxRecordDepth", 0, need.Depth, _scope.Limits.MaxRecordDepth, need.Depth > _scope.Limits.MaxRecordDepth, CapacityDemandLifetime.Transient, allowanceSource: _scope.Limits)) throw new MapSurfaceCapacityException(nameof(MapQueryLimits.MaxRecordDepth), need.Depth, _scope.Limits.MaxRecordDepth);
                Read(reference.Anchor, recordAnchor: true);
                Expand(reference.Anchor, need.Depth);
            }
            if (!_facts.HasRecord(reference))
            {
                MapPatchStatus status = _facts.Status(reference.Anchor);
                _facts.AddUnavailable(reference.Id, reference.Anchor,
                    status is MapPatchStatus.Present or MapPatchStatus.KnownEmpty ? MapPatchStatus.Missing : status);
            }
        }
    }
    void Expand(MapPatchKey key, int depth)
    {
        if (_facts.OwnedPatch(key) is { } patch)
            foreach (MapTopologyRecord record in patch.Records.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                CaptureGeometrySurfaces(record);
                IEnumerable<MapRecordRef> refs = record is MapSpaceDoc space
                    ? new[] { space.Parent, space.AliasOf }.OfType<MapRecordRef>()
                    : MapSurfaceReferences.Records(record);
                foreach (MapRecordRef reference in refs) Schedule(reference, depth + 1);
            }
        if (_facts.Status(key) is not (MapPatchStatus.Present or MapPatchStatus.KnownEmpty)) return;
        bool known = _session.TryGetIncidentRecords(key, out IReadOnlyList<MapRecordRef>? incidents);
        if (!known)
        {
            if (incidents is not null) throw new MapDocumentException("unknown incidents carry records");
            _unknownIncidents = true;
            _facts.AddUnavailable("patch", key, MapPatchStatus.Unloaded);
        }
        else
        {
            if (incidents is null) throw new MapDocumentException("known incidents have no record list");
            foreach (MapRecordRef reference in incidents.ToArray()) Schedule(reference, depth + 1);
        }
        ReconcileReservations();
    }
    void Schedule(MapRecordRef reference, int depth)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.Id)) throw new MapDocumentException("invalid acquired record reference");
        MapAcquiredFacts.ValidateKey(reference.Anchor);
        if (_scheduled.Add(reference)) _pending.Add(new(reference, depth));
    }
    void Read(MapPatchKey key, bool recordAnchor)
    {
        if (!_reserved.Contains(key) && DemandDiagnostics.Check(this, "MapEffectiveAcquisition.MaxCandidatePatches.3", "MaxCandidatePatches", _reserved.Count, 1, _scope.Limits.MaxCandidatePatches, _reserved.Count >= _scope.Limits.MaxCandidatePatches, CapacityDemandLifetime.Inventory, allowanceSource: _scope.Limits)) throw new MapSurfaceCapacityException(nameof(MapQueryLimits.MaxCandidatePatches), (long)_reserved.Count + 1, _scope.Limits.MaxCandidatePatches);
        if (recordAnchor && DemandDiagnostics.Check(this, "MapEffectiveAcquisition.MaxRecordReads.1", "MaxRecordReads", _recordReads, 1, _scope.Limits.MaxRecordReads, _recordReads >= _scope.Limits.MaxRecordReads, CapacityDemandLifetime.Cumulative, allowanceSource: _scope.Limits)) throw new MapSurfaceCapacityException(nameof(MapQueryLimits.MaxRecordReads), (long)_recordReads + 1, _scope.Limits.MaxRecordReads);
        Surface(key.SurfaceId);
        _reserved.Add(key);
        int before = _session.PagesRead;
        if (recordAnchor) _recordReads++;
        MapPatchRead read = _session.ReadPatch(key);
        CheckPages(before, read.PagesRead);
        ReconcileReservations();
        if (read.Key != key) throw new MapDocumentException("acquisition returned a different patch key");
        _facts.AddRead(read, ownsPayload: true);
    }
    MapSurfaceRef Surface(string id)
    {
        if (_facts.TrySurface(id, out MapSurfaceRef? captured)) return captured!;
        if (!_session.TryGetSurface(id, out MapSurfaceRef? found) || found is null || found.Id != id)
            throw new MapDocumentException("required surface is undeclared: " + id);
        _facts.AddSurface(found);
        ReconcileReservations();
        if (found.PaintTargetSurfaceId is { } target) Surface(target);
        return _facts.TrySurface(id, out captured) ? captured! : throw new MapDocumentException("surface capture failed");
    }
    void CaptureGeometrySurfaces(MapTopologyRecord record)
    {
        switch (record)
        {
            case MapBoundaryChain chain:
                if (chain.SourcePatch is { } source) Surface(source.SurfaceId);
                foreach (MapChainVertex vertex in chain.Vertices) Surface(vertex.Vertex.SurfaceId);
                break;
            case MapSurfaceSeam seam:
                Surface(seam.First.Patch.SurfaceId); Surface(seam.Second.Patch.SurfaceId);
                Surface(seam.First.From.SurfaceId); Surface(seam.First.To.SurfaceId);
                Surface(seam.Second.From.SurfaceId); Surface(seam.Second.To.SurfaceId);
                foreach (var pair in seam.Pairs) { Surface(pair.First.SurfaceId); Surface(pair.Second.SurfaceId); }
                break;
            case MapCavePortal portal:
                foreach (MapLatticeVertex vertex in portal.Interval) Surface(vertex.SurfaceId);
                break;
            case MapHorizontalOpening opening: Surface(opening.Patch.SurfaceId); break;
            case MapSpaceFootprint footprint: Surface(footprint.Lattice.SurfaceId); break;
        }
    }
    MapPatchKey[] ReservationSnapshot()
    {
        MapPatchKey[] snapshot = _session.ReservedPatchKeys.ToArray();
        foreach (MapPatchKey key in snapshot) MapAcquiredFacts.ValidateKey(key);
        if (DemandDiagnostics.Check(this, "MapEffectiveAcquisition.MaxCandidatePatches.4", "MaxCandidatePatches", 0, snapshot.Length, _scope.Limits.MaxCandidatePatches, snapshot.Length > _scope.Limits.MaxCandidatePatches, CapacityDemandLifetime.Inventory, allowanceSource: _scope.Limits) || !snapshot.SequenceEqual(snapshot.Distinct().OrderBy(k => k)))
            throw new MapDocumentException("incoherent acquisition reservations");
        return snapshot;
    }
    void ReconcileReservations()
    {
        if (_session.SnapshotId != _snapshot || _session.RootSha256 != _root || !ReservationSnapshot().SequenceEqual(_reserved))
            throw new MapDocumentException("acquisition changed its pinned facts or reservations");
    }
    void CheckPages(int before, int delta)
    {
        if (delta < 0 || _session.PagesRead < before || _session.PagesRead - before != delta)
            throw new MapDocumentException("incoherent acquisition page observations");
    }
    readonly record struct Need(MapRecordRef Reference, int Depth) : IComparable<Need>
    {
        public int CompareTo(Need other)
        {
            int depth = Depth.CompareTo(other.Depth), anchor = Reference.Anchor.CompareTo(other.Reference.Anchor);
            return depth != 0 ? depth : anchor != 0 ? anchor : StringComparer.Ordinal.Compare(Reference.Id, other.Reference.Id);
        }
    }
}
