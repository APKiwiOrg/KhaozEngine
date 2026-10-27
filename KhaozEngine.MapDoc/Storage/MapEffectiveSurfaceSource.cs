using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Storage;

internal interface IMapEffectiveSurfaceContext { MapEffectiveContext EffectiveContext { get; } }

/// <summary>Pinned local descriptors indexed by their transformed world bounds. Local keys stay local.</summary>
internal sealed class MapEffectiveSurfaceSource : IMapSurfaceAcquisitionSource, IMapEffectiveSurfaceContext
{
    readonly IReadOnlyDictionary<MapPatchKey, MapExactRect> _bounds;
    readonly IReadOnlyList<(MapRecordRef Record, MapExactRect Bounds)> _records;
    readonly int _recordCapacity;
    internal Scene.MapPreparationReservation? PreparationOwnership { get; }
    internal long IndexBytes => checked(128L + Surfaces.Sum(s => 136L + 8L * (s.IndoorSpan?.DomainTags.Count ?? 0)) +
        MapSurfaceSourcePreparation.DictionaryBytes(_bounds) +
        System.Runtime.CompilerServices.Unsafe.SizeOf<(MapRecordRef, MapExactRect)>() * (long)_recordCapacity + 32L * _records.Count);
    public MapEffectiveContext EffectiveContext { get; }
    public string SnapshotId => "effective:" + EffectiveContext.Digest;
    public string RootSha256 => EffectiveContext.Digest;
    public IReadOnlyList<MapSurfaceRef> Surfaces { get; }
    internal MapEffectiveSurfaceSource(MapEffectiveContext context, IReadOnlySet<string>? ownedSurfaces = null)
    {
        using var preparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(1024L + context.Surfaces.Refs.Sum(s => 136L + 8L * (s.IndoorSpan?.DomainTags.Count ?? 0)) +
            512L * context.Surfaces.Patches.Values.Sum(p => (long)p.Records.Count)));
        MapSurfaceSourcePreparation.BeforeDictionary<MapPatchKey, MapExactRect>(context.Surfaces.Patches.Count);
        EffectiveContext = context;
        Surfaces = Array.AsReadOnly(context.Surfaces.Refs.Where(s => ownedSurfaces is null || ownedSurfaces.Contains(s.Id)).Select(MapAcquiredFacts.CopySurface).ToArray());
        _bounds = context.Surfaces.Patches.Values.Where(p => ownedSurfaces is null || ownedSurfaces.Contains(p.Key.SurfaceId)).ToDictionary(p => p.Key, Bounds);
        var records = new List<(MapRecordRef, MapExactRect)>();
        foreach (var patch in context.Surfaces.Patches.Values.Where(p => _bounds.ContainsKey(p.Key)))
            foreach (var record in patch.Records)
            {
                using var recordPreparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
                Scene.MapSceneAdmission.Current?.ReserveBytes(checked(512L + 192L * MapSurfaceSourcePreparation.Vertices(record)));
                IEnumerable<MapExactXz> vertices = record switch
                {
                    MapPrismDomain prism => PrismPoints(prism),
                    MapSpaceFootprint footprint => footprint.SlotCells.SelectMany(c => MapEffectiveGeometry.CellPolygon(context, footprint.Lattice, c)),
                    MapHorizontalOpening opening => opening.SlotCells.SelectMany(c => MapEffectiveGeometry.CellPolygon(context, opening.Patch, c)),
                    MapBoundaryChain chain => chain.Vertices.Select(v => MapEffectiveGeometry.Vertex(context, v.Vertex)),
                    MapCavePortal portal => portal.Interval.Select(v => MapEffectiveGeometry.Vertex(context, v)),
                    MapSurfaceSeam seam => new[] { seam.First.From, seam.First.To, seam.Second.From, seam.Second.To }.Select(v => MapEffectiveGeometry.Vertex(context, v)),
                    _ => Array.Empty<MapExactXz>(),
                };
                var points = vertices.ToArray();
                if (points.Length != 0) records.Add((new(record.Id, patch.Key), MapEffectiveGeometry.Bounds(points)));
            }
        _records = records.AsReadOnly();
        _recordCapacity = records.Capacity;
        PreparationOwnership = preparation?.Complete(IndexBytes);
    }
    IEnumerable<MapExactXz> PrismPoints(MapPrismDomain prism)
    {
        var addresses = prism.Footprint.Rects.SelectMany(r => new[] { MapLatticeAddress.Corner(r.MinX, r.MinZ),
            MapLatticeAddress.Corner(r.MaxX, r.MinZ), MapLatticeAddress.Corner(r.MaxX, r.MaxZ), MapLatticeAddress.Corner(r.MinX, r.MaxZ) })
            .Concat(prism.Footprint.Rings.SelectMany(r => r.Vertices));
        foreach (var address in addresses)
        {
            var point = prism.Footprint.Frame.WorldXz(address);
            var world = EffectiveContext.Point(MapPrefabChildKind.Record, prism.Id, new(point.X, default, point.Z));
            yield return new(world.X, world.Z);
        }
    }
    MapExactRect Bounds(MapSurfacePatch patch)
    {
        long x = checked(patch.Key.SlotX * 64 + patch.CellMinX), z = checked(patch.Key.SlotZ * 64 + patch.CellMinZ);
        foreach (int cell in Enumerable.Range(0, patch.Width * patch.Depth))
        {
            int localX = cell % patch.Width, localZ = cell / patch.Width;
            if (!patch.IsPresent(localX, localZ)) continue;
            var polygon = MapEffectiveGeometry.CellPolygon(EffectiveContext, patch.Key, (localZ + patch.CellMinZ) * 64 + localX + patch.CellMinX);
            if (MapEffectiveGeometry.Area(polygon).Sign == 0)
                throw new MapDocumentException("Collapsed effective cell is not representable.", new MapExactOverflowException());
        }
        return MapEffectiveGeometry.Bounds(new[] { MapLatticeAddress.Corner(x, z), MapLatticeAddress.Corner(x + patch.Width, z),
            MapLatticeAddress.Corner(x + patch.Width, z + patch.Depth), MapLatticeAddress.Corner(x, z + patch.Depth) }
            .Select(a => MapEffectiveGeometry.Vertex(EffectiveContext, new(patch.Key.SurfaceId, a))));
    }
    public MapPatchRead ReadPatch(MapPatchKey key) => _bounds.ContainsKey(key) && EffectiveContext.Surfaces.Patches.TryGetValue(key, out var patch)
        ? new(key, MapPatchStatus.Present, patch.Clone(), MapSurfaceSemantics.PatchDigest(patch), null, 0)
        : new(key, MapPatchStatus.KnownEmpty, null, null, null, 0);
    public MapPatchFindResult FindPatches(MapSurfaceScope scope)
    {
        using var session = OpenAcquisition(scope);
        return session.FindPatches();
    }
    public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope) => new Acquisition(this, scope);

    sealed class Acquisition : IMapSurfacePageVisitSession, IMapSurfaceWorkSession
    {
        readonly MapEffectiveSurfaceSource _source;
        readonly MapSurfaceScope _scope;
        readonly MapPageBudget _budget;
        readonly SortedSet<MapPatchKey> _reserved = new();
        readonly HashSet<MapPatchKey> _served = new();
        long _recordsCharged;
        bool _disposed;
        public bool CanObservePageVisits => true;
        public bool CanObserveWork => true;
        public event Action? MetadataInspecting { add => _budget.MetadataInspecting += value; remove => _budget.MetadataInspecting -= value; }
        public event Action? RangeAdding { add => _budget.RangeAdding += value; remove => _budget.RangeAdding -= value; }
        public event Action? PageVisiting { add => _budget.PageVisiting += value; remove => _budget.PageVisiting -= value; }
        public string SnapshotId => _source.SnapshotId;
        public string RootSha256 => _source.RootSha256;
        public int PagesRead => 0;
        public IReadOnlyList<MapPatchKey> ReservedPatchKeys => Array.AsReadOnly(_reserved.ToArray());
        internal Acquisition(MapEffectiveSurfaceSource source, MapSurfaceScope scope)
        {
            scope.Validate(); _source = source; _scope = scope; _budget = scope.SharedBudget?.Share() ?? new(scope.Limits.MaxPageReads);
        }
        public bool TryGetSurface(string surfaceId, out MapSurfaceRef? surface)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _budget.BeforeMetadata();
            surface = _source.Surfaces.FirstOrDefault(s => s.Id == surfaceId);
            return surface is not null;
        }
        public MapPatchFindResult FindPatches()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var selected = new List<MapPatchKey>();
            try
            {
                var rectangle = _scope.WorldRectangle();
                foreach (var entry in _source._bounds)
                {
                    _budget.BeforeMetadata();
                    var surface = _source.Surfaces.Single(s => s.Id == entry.Key.SurfaceId);
                    if ((_scope.DiscoverySurfaces is { } ids && !ids.Contains(surface.Id)) || !_scope.Roles.Contains(surface.Role) || !rectangle.Touches(entry.Value)) continue;
                    Reserve(entry.Key);
                    selected.Add(entry.Key);
                }
                return new(MapFindStatus.Complete, _scope, SnapshotId, Array.AsReadOnly(selected.Select(Serve).ToArray()), [], [], 0);
            }
            catch (MapSurfaceCapacityException) { return new(MapFindStatus.CapacityExceeded, _scope, SnapshotId, [], [], [], 0); }
        }
        void Reserve(MapPatchKey key)
        {
            if (!_reserved.Contains(key) && DemandDiagnostics.Check(this, "MapEffectiveSurfaceSource.MaxCandidatePatches.1", "MaxCandidatePatches", _reserved.Count, 1, _scope.Limits.MaxCandidatePatches, _reserved.Count >= _scope.Limits.MaxCandidatePatches, CapacityDemandLifetime.Inventory, allowanceSource: _scope.Limits)) throw new MapSurfaceCapacityException();
            _scope.SceneAdmission?.BeforeResource(Scene.MapSceneAcquisition.PatchKey(key));
            _reserved.Add(key);
        }
        public MapPatchRead ReadPatch(MapPatchKey key)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _budget.BeforeMetadata(); Reserve(key);
            return Serve(key);
        }
        MapPatchRead Serve(MapPatchKey key)
        {
            using var admitted = _scope.SceneAdmission?.Enter();
            if (_served.Add(key) && _source.EffectiveContext.Surfaces.Patches.TryGetValue(key, out var patch))
            {
                long next = checked(_recordsCharged + patch.Records.Count);
                if (DemandDiagnostics.Check(this, "MapEffectiveSurfaceSource.MaxRecordReads.1", "MaxRecordReads", 0, next, _scope.Limits.MaxRecordReads, next > _scope.Limits.MaxRecordReads, CapacityDemandLifetime.Cumulative, allowanceSource: _scope.Limits)) throw new MapSurfaceCapacityException();
                _recordsCharged = next;
            }
            Scene.MapSceneAdmission.Current?.BeforePayload(0);
            return _source.ReadPatch(key);
        }
        public bool TryGetIncidentRecords(MapPatchKey acquiredKey, out IReadOnlyList<MapRecordRef>? records)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var incident = new List<MapRecordRef>();
            if (_source._bounds.TryGetValue(acquiredKey, out var bounds))
                foreach (var record in _source._records)
                {
                    _budget.BeforeMetadata();
                    if (record.Record.Anchor != acquiredKey && record.Bounds.Touches(bounds)) incident.Add(record.Record);
                }
            records = Array.AsReadOnly(incident.Distinct().OrderBy(r => r.Anchor).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray());
            return true;
        }
        public void Dispose() => _disposed = true;
    }
}
