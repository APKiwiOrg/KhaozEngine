using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Composes generated resource edges with the caller's pinned native producer.</summary>
internal sealed partial class MapComposedSurfaceSource : IMapSurfaceAcquisitionSource, IMapEffectiveSurfaceContext
{
    readonly IMapSurfaceSource _native;
    internal bool SupportsSceneAdmission => Scene.MapSceneAdmission.Supports(_native);
    readonly MapEffectiveSurfaceSource _generated;
    readonly string _nativeSnapshot, _nativeRoot;
    readonly IReadOnlyDictionary<string, MapEffectiveTransform?> _nativePoses;
    internal IMapSurfaceSource Native => _native;
    internal MapEffectiveSurfaceSource Generated => _generated;
    internal Scene.MapPreparationReservation? PreparationOwnership { get; }
    internal long IndexBytes => checked(128L + MapSurfaceSourcePreparation.DictionaryBytes(_nativePoses));
    public MapEffectiveContext EffectiveContext { get; }
    public string SnapshotId { get; }
    public string RootSha256 => EffectiveContext.Digest;
    public IReadOnlyList<MapSurfaceRef> Surfaces => Array.AsReadOnly(EffectiveContext.Surfaces.Refs.Select(MapAcquiredFacts.CopySurface).ToArray());

    internal MapComposedSurfaceSource(MapEffectiveContext context, IMapSurfaceSource native)
    {
        using var preparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(128L + 64L * context.Surfaces.Refs.Count));
        MapSurfaceSourcePreparation.BeforeDictionary<string, MapEffectiveTransform?>(native.Surfaces.Count);
        EffectiveContext = context;
        _native = native;
        _nativeSnapshot = native.SnapshotId;
        _nativeRoot = native.RootSha256;
        _nativePoses = native.Surfaces.ToDictionary(s => s.Id,
            s => context.Transform(MapPrefabChildKind.Surface, s.Id)?.Transform, StringComparer.Ordinal);
        var owned = context.Surfaces.Refs.Where(s => !_nativePoses.ContainsKey(s.Id)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        _generated = new(context, owned);
        SnapshotId = "composed:" + MapCanonical.HashHex(w =>
        {
            w.WriteStartArray(); w.WriteStringValue(context.Digest); w.WriteStringValue(_nativeSnapshot);
            w.WriteStringValue(_nativeRoot); w.WriteEndArray();
        });
        PreparationOwnership = preparation?.Complete(checked(IndexBytes + _generated.IndexBytes));
    }
    public MapPatchRead ReadPatch(MapPatchKey key) => _nativePoses.ContainsKey(key.SurfaceId) ? _native.ReadPatch(key) : _generated.ReadPatch(key);
    public MapPatchFindResult FindPatches(MapSurfaceScope scope)
    {
        using var session = OpenAcquisition(scope);
        return session.FindPatches();
    }
    public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope) => new Acquisition(this, scope);

    sealed partial class Acquisition : IMapSurfacePageVisitSession, IMapSurfaceWorkSession
    {
        sealed record Part(IMapSurfaceAcquisitionSession Session, IReadOnlySet<string> Ids);
        readonly MapComposedSurfaceSource _source;
        readonly MapSurfaceScope _scope;
        readonly List<Part> _parts = new();
        readonly SortedSet<MapPatchKey> _reserved = new();
        int _pageVisits;
        bool _disposed;
        public bool CanObservePageVisits { get; private set; } = true;
        public event Action? PageVisiting;
        public string SnapshotId => _source.SnapshotId;
        public string RootSha256 => _source.RootSha256;
        public int PagesRead => _parts.Sum(p => p.Session.PagesRead);
        public IReadOnlyList<MapPatchKey> ReservedPatchKeys => Array.AsReadOnly(_reserved.ToArray());

        internal Acquisition(MapComposedSurfaceSource source, MapSurfaceScope scope)
        {
            _source = source; _scope = scope;
            _workLimit = 1L + 4L * MapSurfacePages.MaxEntries * scope.Limits.MaxPageReads;
            var groups = source._nativePoses.GroupBy(p => p.Value).ToArray();
            var budget = scope.SharedBudget ?? new MapPageBudget(scope.Limits.MaxPageReads);
            bool generated = source._generated.Surfaces.Count != 0;
            try
            {
                if (groups.Length != 0 && source._native is not IMapSurfaceAcquisitionSource)
                    throw new MapDocumentException("surface source does not support bounded scoped acquisition");
                foreach (var group in groups)
                {
                    var request = group.Key is { } pose ? LocalScope(scope, pose) : scope;
                    var ids = group.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
                    var discovery = scope.DiscoverySurfaces is { } selected
                        ? ids.Where(selected.Contains).ToHashSet(StringComparer.Ordinal) : ids;
                    var session = ((IMapSurfaceAcquisitionSource)source._native).OpenAcquisition(request with { SharedBudget = budget, DiscoverySurfaces = discovery });
                    if (session.SnapshotId != source._nativeSnapshot || session.RootSha256 != source._nativeRoot)
                    { session.Dispose(); throw new MapDocumentException("native producer differs from its pinned composition"); }
                    _parts.Add(new(session, ids));
                    // Several parts share one ceiling only while every native session reports its work before doing it.
                    _unobservable = Unobservable(session, pages: groups.Length > 1, work: groups.Length > 1 || generated);
                    if (_unobservable is not null) break;
                }
                int pageOpaque = 0, workOpaque = 0;
                foreach (var part in _parts) Observe(part.Session, ref pageOpaque, ref workOpaque);
                CanObservePageVisits = pageOpaque == 0;
                CanObserveWork = workOpaque == 0;
                // One legacy session already owns a whole-call allowance. Reserve it conservatively. Multiple
                // unobservable native sessions cannot be composed safely, so refuse before traversing any of them.
                if (pageOpaque != 0) _pageVisits = scope.Limits.MaxPageReads;
                if (workOpaque != 0) _metadata = _ranges = _workLimit;
                if (pageOpaque > 1) _unobservable ??= Unobservable(_parts[0].Session, pages: true, work: false);
                if (generated)
                {
                    var session = source._generated.OpenAcquisition(scope with { SharedBudget = budget });
                    _parts.Add(new(session, source._generated.Surfaces.Select(s => s.Id).ToHashSet(StringComparer.Ordinal)));
                    Observe(session, ref pageOpaque, ref workOpaque);
                }
            }
            catch { Dispose(); throw; }
        }
        Part? Owner(string id) => _parts.FirstOrDefault(p => p.Ids.Contains(id));
        public bool TryGetSurface(string id, out MapSurfaceRef? surface)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            surface = null;
            if (_unobservable is not null) throw new MapSurfaceCapacityException(_unobservable);
            return Owner(id)?.Session.TryGetSurface(id, out surface) == true;
        }
        public MapPatchFindResult FindPatches()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int before = PagesRead;
            var patches = new List<MapPatchRead>(); var empty = new List<MapCoveredRange>(); var unavailable = new List<MapPatchRead>();
            bool complete = true;
            try
            {
                if (_unobservable is not null) throw new MapSurfaceCapacityException(_unobservable);
                foreach (var part in _parts)
                {
                    var found = part.Session.FindPatches();
                    if (found.Status == MapFindStatus.CapacityExceeded) throw new MapSurfaceCapacityException(found.Diagnostic, found.Capacity);
                    complete &= found.Status == MapFindStatus.Complete;
                    foreach (var key in part.Session.ReservedPatchKeys.Where(k => part.Ids.Contains(k.SurfaceId))) Reserve(key);
                    patches.AddRange(found.Patches.Where(p => part.Ids.Contains(p.Key.SurfaceId)));
                    empty.AddRange(found.KnownEmpty.Where(r => part.Ids.Contains(r.SurfaceId)));
                    unavailable.AddRange(found.Unavailable.Where(r => part.Ids.Contains(r.Key.SurfaceId)));
                }
                return new(complete && unavailable.Count == 0 ? MapFindStatus.Complete : MapFindStatus.Incomplete,
                    _scope, SnapshotId, patches.AsReadOnly(), empty.AsReadOnly(), unavailable.AsReadOnly(), PagesRead - before);
            }
            catch (MapSurfaceCapacityException ex)
            {
                return new(MapFindStatus.CapacityExceeded, _scope, SnapshotId, [], [], [], PagesRead - before)
                { Diagnostic = ex.Capacity?.Message ?? ex.Diagnostic, Capacity = ex.Capacity };
            }
        }
        void Reserve(MapPatchKey key)
        {
            if (!_reserved.Contains(key) && _reserved.Count >= _scope.Limits.MaxCandidatePatches)
                throw new MapSurfaceCapacityException(nameof(MapQueryLimits.MaxCandidatePatches), (long)_reserved.Count + 1, _scope.Limits.MaxCandidatePatches);
            _reserved.Add(key);
        }
        public MapPatchRead ReadPatch(MapPatchKey key)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_unobservable is not null) throw new MapSurfaceCapacityException(_unobservable);
            Reserve(key);
            int before = PagesRead;
            var part = Owner(key.SurfaceId) ?? throw new MapDocumentException("required surface is undeclared: " + key.SurfaceId);
            var read = part.Session.ReadPatch(key);
            return read with { PagesRead = PagesRead - before };
        }
        public bool TryGetIncidentRecords(MapPatchKey key, out IReadOnlyList<MapRecordRef>? records)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_unobservable is not null) throw new MapSurfaceCapacityException(_unobservable);
            var owner = Owner(key.SurfaceId);
            records = null;
            return owner?.Session.TryGetIncidentRecords(key, out records) == true;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var part in _parts)
            {
                Release(part.Session);
                part.Session.Dispose();
            }
        }
    }

    static MapSurfaceScope LocalScope(MapSurfaceScope scope, MapEffectiveTransform pose)
    {
        var rectangle = scope.WorldRectangle();
        var (sin, cos) = Math.SinCos(pose.YawRadians);
        var points = new[] { (rectangle.MinX, rectangle.MinZ), (rectangle.MinX, rectangle.MaxZ),
            (rectangle.MaxX, rectangle.MinZ), (rectangle.MaxX, rectangle.MaxZ) }.Select(p =>
        {
            double x = p.Item1.ToDouble() - pose.X, z = p.Item2.ToDouble() - pose.Z;
            return ((cos * x - sin * z) / pose.Scale, (sin * x + cos * z) / pose.Scale);
        }).ToArray();
        // Frozen vertices can move by half a tick. Expand discovery only, never the final exact query.
        double halo = 1.0 / MapResourceTransform.Quantum / pose.Scale;
        float Lower(double value) => MathF.BitDecrement((float)(value - halo));
        float Upper(double value) => MathF.BitIncrement((float)(value + halo));
        return new(WorldFrame.Origin, new(Lower(points.Min(p => p.Item1)), Lower(points.Min(p => p.Item2))),
            new(Upper(points.Max(p => p.Item1)), Upper(points.Max(p => p.Item2))), null, null, scope.Roles, scope.SpaceIds, scope.Limits);
    }
}
