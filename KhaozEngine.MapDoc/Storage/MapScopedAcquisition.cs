using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>One session for discovery, breadth-first record acquisition and bound preflight.</summary>
internal sealed class MapScopedAcquisition
{
    internal sealed record Result(MapAcquireStatus Status, string? Detail, MapAcquiredFacts Facts,
        string SnapshotId, string RootSha256, int PagesRead, int RecordReads);

    readonly IMapSurfaceAcquisitionSession _session;
    readonly MapSurfaceScope _scope;
    readonly MapAcquisitionWork? _work;
    readonly MapAcquiredFacts _facts = new();
    readonly SortedSet<MapPatchKey> _reserved = new();
    readonly SortedSet<Need> _pending = new();
    readonly HashSet<MapRecordRef> _scheduled = new();
    readonly string _snapshot, _root;
    int _recordReads;
    bool _unknownIncidents;

    MapScopedAcquisition(IMapSurfaceAcquisitionSession session, MapSurfaceScope scope, MapAcquisitionWork? work)
    {
        _session = session;
        _scope = scope;
        _work = work;
        _snapshot = session.SnapshotId;
        _root = session.RootSha256;
        if (string.IsNullOrWhiteSpace(_snapshot)) throw new MapDocumentException("invalid acquisition snapshot");
        MapSurfaceStorageLayout.RequireDigest(_root);
    }
    internal static Result Run(IMapSurfaceAcquisitionSource source, MapSurfaceScope scope, MapAcquisitionWork? work)
    {
        using IMapSurfaceAcquisitionSession session = source.OpenAcquisition(scope);
        var acquisition = new MapScopedAcquisition(session, scope, work);
        if (source.SnapshotId != session.SnapshotId || source.RootSha256 != session.RootSha256)
            throw new MapDocumentException("acquisition session differs from its pinned source");
        return acquisition.Run();
    }
    Result Run()
    {
        string phase = "discovery";
        try
        {
            Discover();
            phase = "record expansion";
            ExpandRecords();
            phase = "bound slots";
            IReadOnlyList<MapPatchKey> bounds = MapBoundAcquisition.Collect(_scope, _facts.OwnedRecords.OfType<MapSpaceFootprint>(),
                _reserved, Surface, _work);
            foreach (MapPatchKey key in bounds) Read(key, recordAnchor: false);
            return Finish(_facts.Complete ? MapAcquireStatus.Complete : MapAcquireStatus.Incomplete,
                _facts.Complete ? null : _unknownIncidents ? "incomplete incident records" : "incomplete surface acquisition", _facts);
        }
        catch (MapSurfaceCapacityException) { return Finish(MapAcquireStatus.CapacityExceeded, phase + " capacity exceeded", new()); }
        catch (MapExactOverflowException) { return Finish(MapAcquireStatus.NotRepresentable, "exact value overflow", new()); }
    }
    Result Finish(MapAcquireStatus status, string? detail, MapAcquiredFacts facts) =>
        new(status, detail, facts, _snapshot, _root, _session.PagesRead, _recordReads);

    void Discover()
    {
        int before = _session.PagesRead;
        MapPatchFindResult found = _session.FindPatches();
        CheckPages(before, found.PagesRead);
        if (found.SnapshotId != _snapshot || found.Scope is null || found.Scope.Digest != _scope.Digest || !Enum.IsDefined(found.Status))
            throw new MapDocumentException("incoherent acquisition discovery snapshot or scope");
        foreach (MapPatchKey key in ReservationSnapshot()) _reserved.Add(key);
        if (found.Status == MapFindStatus.CapacityExceeded) throw new MapSurfaceCapacityException();
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
            _facts.AddRead(read);
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
        if (!_reserved.Contains(key) && _reserved.Count >= _scope.Limits.MaxCandidatePatches) throw new MapSurfaceCapacityException();
        if (recordAnchor && _recordReads >= _scope.Limits.MaxRecordReads) throw new MapSurfaceCapacityException();
        Surface(key.SurfaceId);
        _reserved.Add(key);
        int before = _session.PagesRead;
        if (recordAnchor) _recordReads++;
        MapPatchRead read = _session.ReadPatch(key);
        CheckPages(before, read.PagesRead);
        ReconcileReservations();
        if (read.Key != key) throw new MapDocumentException("acquisition returned a different patch key");
        _facts.AddRead(read);
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
        if (snapshot.Length > _scope.Limits.MaxCandidatePatches || !snapshot.SequenceEqual(snapshot.Distinct().OrderBy(k => k)))
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
