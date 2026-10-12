using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Composes generated resource edges with the caller's pinned native producer.</summary>
internal sealed class MapComposedSurfaceSource : IMapSurfaceAcquisitionSource, IMapEffectiveSurfaceContext
{
    readonly IMapSurfaceSource _native;
    readonly MapEffectiveSurfaceSource _generated;
    readonly string _nativeSnapshot, _nativeRoot;
    readonly IReadOnlyDictionary<string, MapEffectiveTransform?> _nativePoses;
    public MapEffectiveContext EffectiveContext { get; }
    public string SnapshotId { get; }
    public string RootSha256 => EffectiveContext.Digest;
    public IReadOnlyList<MapSurfaceRef> Surfaces => Array.AsReadOnly(EffectiveContext.Surfaces.Refs.Select(MapAcquiredFacts.CopySurface).ToArray());

    internal MapComposedSurfaceSource(MapEffectiveContext context, IMapSurfaceSource native)
    {
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
    }
    public MapPatchRead ReadPatch(MapPatchKey key) => _nativePoses.ContainsKey(key.SurfaceId) ? _native.ReadPatch(key) : _generated.ReadPatch(key);
    public MapPatchFindResult FindPatches(MapSurfaceScope scope)
    {
        using var session = OpenAcquisition(scope);
        return session.FindPatches();
    }
    public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope) => new Acquisition(this, scope);

    sealed class Acquisition : IMapSurfaceAcquisitionSession
    {
        sealed record Part(IMapSurfaceAcquisitionSession Session, IReadOnlySet<string> Ids);
        readonly MapComposedSurfaceSource _source;
        readonly MapSurfaceScope _scope;
        readonly List<Part> _parts = new();
        readonly SortedSet<MapPatchKey> _reserved = new();
        bool _disposed;
        public string SnapshotId => _source.SnapshotId;
        public string RootSha256 => _source.RootSha256;
        public int PagesRead => _parts.Sum(p => p.Session.PagesRead);
        public IReadOnlyList<MapPatchKey> ReservedPatchKeys => Array.AsReadOnly(_reserved.ToArray());

        internal Acquisition(MapComposedSurfaceSource source, MapSurfaceScope scope)
        {
            _source = source; _scope = scope;
            var groups = source._nativePoses.GroupBy(p => p.Value).ToArray();
            var budget = scope.SharedBudget ?? new MapPageBudget(scope.Limits.MaxPageReads);
            try
            {
                if (groups.Length != 0 && source._native is not IMapSurfaceAcquisitionSource)
                    throw new MapDocumentException("surface source does not support bounded scoped acquisition");
                foreach (var group in groups)
                {
                    var request = group.Key is { } pose ? LocalScope(scope, pose) : scope;
                    var ids = group.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
                    var session = ((IMapSurfaceAcquisitionSource)source._native).OpenAcquisition(request with { SharedBudget = budget, DiscoverySurfaces = ids });
                    if (session.SnapshotId != source._nativeSnapshot || session.RootSha256 != source._nativeRoot)
                    { session.Dispose(); throw new MapDocumentException("native producer differs from its pinned composition"); }
                    _parts.Add(new(session, ids));
                }
                if (source._generated.Surfaces.Count != 0)
                    _parts.Add(new(source._generated.OpenAcquisition(scope with { SharedBudget = budget }), source._generated.Surfaces.Select(s => s.Id).ToHashSet(StringComparer.Ordinal)));
            }
            catch { Dispose(); throw; }
        }
        Part? Owner(string id) => _parts.FirstOrDefault(p => p.Ids.Contains(id));
        public bool TryGetSurface(string id, out MapSurfaceRef? surface)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            surface = null;
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
                foreach (var part in _parts)
                {
                    var found = part.Session.FindPatches();
                    if (found.Status == MapFindStatus.CapacityExceeded) throw new MapSurfaceCapacityException();
                    complete &= found.Status == MapFindStatus.Complete;
                    foreach (var key in part.Session.ReservedPatchKeys.Where(k => part.Ids.Contains(k.SurfaceId))) Reserve(key);
                    patches.AddRange(found.Patches.Where(p => part.Ids.Contains(p.Key.SurfaceId)));
                    empty.AddRange(found.KnownEmpty.Where(r => part.Ids.Contains(r.SurfaceId)));
                    unavailable.AddRange(found.Unavailable.Where(r => part.Ids.Contains(r.Key.SurfaceId)));
                }
                return new(complete && unavailable.Count == 0 ? MapFindStatus.Complete : MapFindStatus.Incomplete,
                    _scope, SnapshotId, patches.AsReadOnly(), empty.AsReadOnly(), unavailable.AsReadOnly(), PagesRead - before);
            }
            catch (MapSurfaceCapacityException) { return new(MapFindStatus.CapacityExceeded, _scope, SnapshotId, [], [], [], PagesRead - before); }
        }
        void Reserve(MapPatchKey key)
        {
            if (!_reserved.Contains(key) && _reserved.Count >= _scope.Limits.MaxCandidatePatches) throw new MapSurfaceCapacityException();
            _reserved.Add(key);
        }
        public MapPatchRead ReadPatch(MapPatchKey key)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Reserve(key);
            int before = PagesRead;
            var part = Owner(key.SurfaceId) ?? throw new MapDocumentException("required surface is undeclared: " + key.SurfaceId);
            var read = part.Session.ReadPatch(key);
            return read with { PagesRead = PagesRead - before };
        }
        public bool TryGetIncidentRecords(MapPatchKey key, out IReadOnlyList<MapRecordRef>? records)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var owner = Owner(key.SurfaceId);
            records = null;
            return owner?.Session.TryGetIncidentRecords(key, out records) == true;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var part in _parts) part.Session.Dispose();
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
