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
        _boundBudget = new(scope.Limits.MaxPageReads);
        _scope = scope;
        _work = work;
        _snapshot = session.SnapshotId;
        _root = session.RootSha256;
        if (string.IsNullOrWhiteSpace(_snapshot))
            throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "invalid acquisition snapshot")
            { FieldPath = "SnapshotId", ActualSnapshot = _snapshot };
        MapSurfaceStorageLayout.RequireDigest(_root);
    }
    internal static MapScopedAcquisition.Result Run(IMapSurfaceAcquisitionSource source, MapSurfaceScope scope, MapAcquisitionWork? work, MapEffectiveContext context)
    {
        using IMapSurfaceAcquisitionSession session = source.OpenAcquisition(scope);
        var acquisition = new MapEffectiveAcquisition(session, scope, work, context);
        // Each producer value is read once, so the refusal describes the comparison that failed.
        string expectedSnapshot = source.SnapshotId, actualSnapshot = session.SnapshotId;
        bool snapshotDiffers = expectedSnapshot != actualSnapshot;
        string? expectedRoot = snapshotDiffers ? null : source.RootSha256, actualRoot = snapshotDiffers ? null : session.RootSha256;
        if (snapshotDiffers || expectedRoot != actualRoot)
            throw new MapDocumentException(MapDocumentRefusal.AcquisitionContractMismatch, "acquisition session differs from its pinned source")
            {
                FieldPath = snapshotDiffers ? "SnapshotId" : "RootSha256",
                ExpectedSnapshot = snapshotDiffers ? expectedSnapshot : null,
                ActualSnapshot = snapshotDiffers ? actualSnapshot : null,
                ExpectedHash = expectedRoot,
                ActualHash = actualRoot,
            };
        return acquisition.Run();
    }
    MapScopedAcquisition.Result Run()
    {
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
        catch (MapSurfaceCapacityException ex) { return Finish(MapAcquireStatus.CapacityExceeded, ex.Diagnostic ?? phase + " capacity exceeded", new()); }
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
            if (gross > _scope.Limits.MaxCandidatePatches) throw new MapSurfaceCapacityException();
            for (long z = slots.MinZ; z < slots.MaxZExclusive; z++)
                for (long x = slots.MinX; x < slots.MaxXExclusive; x++)
                {
                    var key = new MapPatchKey(id, x, z);
                    if (knownCoverage && _facts.IsKnownEmpty(key)) continue;
                    if (!_reserved.Contains(key)) keys.Add(key);
                    if (keys.Count > _scope.Limits.MaxCandidatePatches - _reserved.Count) throw new MapSurfaceCapacityException();
                }
        }
    }

    void Discover()
    {
        int before = _session.PagesRead;
        MapPatchFindResult found = _session.FindPatches();
        CheckPages(before, found.PagesRead);
        if (found.SnapshotId != _snapshot || found.Scope is null || found.Scope.Digest != _scope.Digest || !Enum.IsDefined(found.Status))
        {
            const string message = "incoherent acquisition discovery snapshot or scope";
            if (found.SnapshotId != _snapshot)
                throw new MapDocumentException(MapDocumentRefusal.AcquisitionSnapshotChanged, message)
                { FieldPath = "SnapshotId", ExpectedSnapshot = _snapshot, ActualSnapshot = found.SnapshotId };
            if (found.Scope is null)
                throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, message) { FieldPath = "Scope" };
            if (found.Scope.Digest != _scope.Digest)
                throw new MapDocumentException(MapDocumentRefusal.AcquisitionContractMismatch, message)
                { FieldPath = "Scope.Digest", ExpectedHash = _scope.Digest, ActualHash = found.Scope.Digest };
            throw new MapDocumentException(MapDocumentRefusal.SchemaEnumUnsupported, message)
            { FieldPath = "Status", EnumType = typeof(MapFindStatus), ActualInteger = (long)found.Status };
        }
        foreach (MapPatchKey key in ReservationSnapshot()) _reserved.Add(key);
        if (found.Status == MapFindStatus.CapacityExceeded) throw new MapSurfaceCapacityException(found.Diagnostic);
        foreach (MapCoveredRange range in found.KnownEmpty)
        {
            Surface(range.SurfaceId);
            _facts.AddEmpty(range);
        }
        foreach (MapPatchRead read in found.Patches)
        {
            if (read.Status != MapPatchStatus.Present || !_reserved.Contains(read.Key))
                throw new MapDocumentException(MapDocumentRefusal.AcquisitionContractMismatch, "unreserved discovery payload")
                { Patch = read.Key, PatchStatus = read.Status, FieldPath = read.Status != MapPatchStatus.Present ? "Patches.Status" : "Patches.Key" };
            Surface(read.Key.SurfaceId);
            _facts.AddRead(read);
        }
        foreach (MapPatchRead read in found.Unavailable)
        {
            MapAcquiredFacts.ValidateKey(read.Key);
            if (read.Status is not (MapPatchStatus.Missing or MapPatchStatus.Corrupt or MapPatchStatus.Unloaded) || read.Patch is not null)
                throw new MapDocumentException(read.Status is not (MapPatchStatus.Missing or MapPatchStatus.Corrupt or MapPatchStatus.Unloaded)
                    ? MapDocumentRefusal.SchemaEnumUnsupported : MapDocumentRefusal.AcquisitionContractMismatch,
                    "incoherent unavailable discovery fact")
                {
                    Patch = read.Key, PatchStatus = read.Status,
                    FieldPath = read.Status is not (MapPatchStatus.Missing or MapPatchStatus.Corrupt or MapPatchStatus.Unloaded) ? "Unavailable.Status" : "Unavailable.Patch",
                    EnumType = read.Status is not (MapPatchStatus.Missing or MapPatchStatus.Corrupt or MapPatchStatus.Unloaded) ? typeof(MapPatchStatus) : null,
                    ActualInteger = read.Status is not (MapPatchStatus.Missing or MapPatchStatus.Corrupt or MapPatchStatus.Unloaded) ? (long)read.Status : null,
                };
            Surface(read.Key.SurfaceId);
            if (_reserved.Contains(read.Key)) _facts.AddRead(read);
            else _facts.AddUnavailable("patch", read.Key, read.Status);
        }
        foreach (MapPatchKey key in _reserved)
        {
            if (!_facts.HasRead(key))
            {
                if (!found.KnownEmpty.Any(r => r.SurfaceId == key.SurfaceId && r.Slots.Contains(key.SlotX, key.SlotZ)))
                    throw new MapDocumentException(MapDocumentRefusal.AcquisitionContractMismatch, "reserved discovery key has no fact")
                    { Patch = key, FieldPath = "ReservedPatchKeys" };
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
                if (need.Depth > _scope.Limits.MaxRecordDepth) throw new MapSurfaceCapacityException();
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
            if (incidents is not null)
                throw new MapDocumentException(MapDocumentRefusal.AcquisitionContractMismatch, "unknown incidents carry records")
                { Patch = key, FieldPath = "IncidentRecords", ActualCount = incidents.Count };
            _unknownIncidents = true;
            _facts.AddUnavailable("patch", key, MapPatchStatus.Unloaded);
        }
        else
        {
            if (incidents is null)
                throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "known incidents have no record list")
                { Patch = key, FieldPath = "IncidentRecords" };
            foreach (MapRecordRef reference in incidents.ToArray()) Schedule(reference, depth + 1);
        }
        ReconcileReservations();
    }
    void Schedule(MapRecordRef reference, int depth)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.Id))
            throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "invalid acquired record reference")
            { Patch = reference?.Anchor, RecordId = reference?.Id, FieldPath = reference is null ? "reference" : nameof(reference.Id) };
        MapAcquiredFacts.ValidateKey(reference.Anchor);
        if (_scheduled.Add(reference)) _pending.Add(new(reference, depth));
    }
    void Read(MapPatchKey key, bool recordAnchor)
    {
        if (!_reserved.Contains(key) && _reserved.Count >= _scope.Limits.MaxCandidatePatches) throw new MapSurfaceCapacityException();
        if (recordAnchor && _recordReads >= _scope.Limits.MaxRecordReads) throw new MapSurfaceCapacityException();
        Surface(key.SurfaceId);
        _reserved.Add(key);
        int before = _session.PagesRead;
        if (recordAnchor) _recordReads++;
        MapPatchRead read = _session.ReadPatch(key);
        CheckPages(before, read.PagesRead);
        ReconcileReservations();
        if (read.Key != key)
            throw new MapDocumentException(MapDocumentRefusal.AcquisitionContractMismatch, "acquisition returned a different patch key")
            { Patch = read.Key, ExpectedPatch = key, FieldPath = "ReadPatch.Key" };
        _facts.AddRead(read);
    }
    MapSurfaceRef Surface(string id)
    {
        if (_facts.TrySurface(id, out MapSurfaceRef? captured)) return captured!;
        bool declared = _session.TryGetSurface(id, out MapSurfaceRef? found);
        if (!declared || found is null || found.Id != id)
            throw new MapDocumentException(!declared ? MapDocumentRefusal.ReferenceMissing
                : found is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.AuthoredValueMismatch,
                "required surface is undeclared: " + id)
            { SurfaceId = id, FieldPath = declared && found is not null ? "Surface.Id" : "Surface", ActualText = found?.Id };
        _facts.AddSurface(found);
        ReconcileReservations();
        if (found.PaintTargetSurfaceId is { } target) Surface(target);
        // AddSurface just captured this exact id in the same owned dictionary.
        return _facts.TrySurface(id, out captured) ? captured! : throw new InvalidOperationException("surface capture failed");
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
        if (snapshot.Length > _scope.Limits.MaxCandidatePatches || !snapshot.SequenceEqual(snapshot.Distinct().OrderBy(k => k)))
            throw new MapDocumentException(snapshot.Length > _scope.Limits.MaxCandidatePatches
                ? MapDocumentRefusal.WorkCapacityExceeded : MapDocumentRefusal.OrderInvalid, "incoherent acquisition reservations")
            {
                FieldPath = "ReservedPatchKeys", ActualCount = snapshot.Length,
                Budget = snapshot.Length > _scope.Limits.MaxCandidatePatches ? "MaxCandidatePatches" : null,
                Limit = snapshot.Length > _scope.Limits.MaxCandidatePatches ? _scope.Limits.MaxCandidatePatches : null,
            };
        return snapshot;
    }
    void ReconcileReservations()
    {
        // Each producer value is read once, so the refusal describes the comparison that failed.
        string snapshot = _session.SnapshotId;
        bool snapshotDiffers = snapshot != _snapshot;
        string? root = snapshotDiffers ? null : _session.RootSha256;
        bool rootDiffers = !snapshotDiffers && root != _root;
        if (snapshotDiffers || rootDiffers || !ReservationSnapshot().SequenceEqual(_reserved))
            throw new MapDocumentException(MapDocumentRefusal.AcquisitionSnapshotChanged, "acquisition changed its pinned facts or reservations")
            {
                FieldPath = snapshotDiffers ? "SnapshotId" : rootDiffers ? "RootSha256" : "ReservedPatchKeys",
                ExpectedSnapshot = snapshotDiffers ? _snapshot : null,
                ActualSnapshot = snapshotDiffers ? snapshot : null,
                ExpectedHash = rootDiffers ? _root : null,
                ActualHash = rootDiffers ? root : null,
            };
    }
    void CheckPages(int before, int delta)
    {
        int pages = _session.PagesRead;
        if (delta < 0 || pages < before || pages - before != delta)
            throw new MapDocumentException(delta < 0 ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.AcquisitionContractMismatch,
                "incoherent acquisition page observations")
            {
                FieldPath = "PagesRead",
                ActualInteger = delta < 0 ? delta : pages < before ? pages : null,
                PreviousInteger = delta >= 0 && pages < before ? before : null,
                MinimumInteger = delta < 0 ? 0 : pages < before ? before : null,
                ActualCount = delta >= 0 && pages >= before ? delta : null,
                ExpectedCount = delta >= 0 && pages >= before ? (long)pages - before : null,
            };
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
