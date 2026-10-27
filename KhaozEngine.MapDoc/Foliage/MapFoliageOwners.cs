using KhaozEngine.Diagnostics;
using KhaozEngine.Primitives;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using Bounds = KhaozEngine.MapDoc.Foliage.MapFoliageGenerationContext.Bounds;
using SpatialIndex = KhaozEngine.MapDoc.Foliage.MapFoliageGenerationContext.SpatialIndex;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Captured typed owners for clearance. Non-placement exception masks have their own spatial index because
/// PlacementsIn deliberately indexes placements only. Seating answers are canonical surface provenance, not geometry.</summary>
internal sealed class MapFoliageOwners
{
    internal sealed record MaskOwner(string LayerId, MapFoliageMask Mask, MapEffectiveTransform Pose);
    readonly Dictionary<string, (MaskOwner[] Masks, Bounds[] Proxies, SpatialIndex Index)> _masks = new(StringComparer.Ordinal);
    readonly Dictionary<MapPrefabReference, MapSupportResult> _seats;
    readonly Func<string, MapFoliageRect, long>? _maskCandidates;
    readonly Func<MapSurfaceScope, MapScopedSurfaces> _acquire;
    readonly MapQueryLimits? _queryLimits;
    readonly Dictionary<string, Dictionary<MapPrefabReference, MapSupportBinding>> _bindings;
    readonly Dictionary<string, Dictionary<string, int>> _ordinals;

    internal MapFoliageOwners(MapFoliageSet? set, MapEffectiveResolutionProduct resources,
        Func<MapSurfaceScope, MapScopedSurfaces> acquire, bool scoped = false, Func<string, MapFoliageRect, long>? maskCandidates = null, MapQueryLimits? queryLimits = null)
    {
        _queryLimits = queryLimits;
        var present = scoped ? PresentOwners(resources) : null;
        _acquire = acquire;
        _maskCandidates = maskCandidates;
        _bindings = (set?.Layers ?? []).ToDictionary(l => l.Id, l => l.OwnerSupports.ToDictionary(o => o.Owner, o => o.Support), StringComparer.Ordinal);
        _ordinals = (set?.Layers ?? []).ToDictionary(l => l.Id, l => l.CompatibilityOrdinals.ToDictionary(r => r.SurfaceId, r => r.Ordinal, StringComparer.Ordinal), StringComparer.Ordinal);
        _seats = resources.PlacementSupports.ToDictionary(s => World(MapPrefabChildKind.Placement, s.PlacementId), s => s.Support);
        foreach (var marker in resources.MarkerSupports) _seats.Add(World(MapPrefabChildKind.Marker, marker.MarkerId), marker.Support);
        foreach (var layer in set?.Layers ?? [])
        {
            var masks = new List<MaskOwner>();
            var bounds = new List<Bounds>();
            foreach (var mask in layer.ExceptionMasks)
            {
                if (present is not null && !present.Contains(mask.Owner)) continue;
                MapEffectiveTransform pose = Pose(resources, mask.Owner);
                masks.Add(new(layer.Id, mask, pose));
                bounds.Add(MapFoliageGenerationContext.Proxy(pose, mask.Footprint,
                    mask.Kind == MapFoliageClearanceKind.Door ? layer.DoorClearance : null));
            }
            _masks.Add(layer.Id, (masks.ToArray(), bounds.ToArray(), new(bounds)));
        }
    }

    static HashSet<MapPrefabReference> PresentOwners(MapEffectiveResolutionProduct resources)
    {
        var owners = resources.Placements.Select(p => World(MapPrefabChildKind.Placement, p.PlacementId))
            .Concat(resources.SurfaceSource.Surfaces.Select(s => World(MapPrefabChildKind.Surface, s.Id)))
            .Concat(resources.EffectiveMarkers.Select(m => World(MapPrefabChildKind.Marker, m.Id))).ToHashSet();
        if (resources.SurfaceSource is IMapEffectiveSurfaceContext context)
            foreach (var record in context.EffectiveContext.Surfaces.AllRecords()) owners.Add(World(MapPrefabChildKind.Record, record.Id));
        return owners;
    }

    internal IReadOnlyList<MaskOwner> Masks(string layerId, MapFoliageRect rect, out long candidates)
    {
        var (masks, _, index) = _masks[layerId];
        var hits = index.Query(new(rect.MinX, rect.MinZ, rect.MaxX, rect.MaxZ), out candidates);
        if (_maskCandidates is not null) candidates = _maskCandidates(layerId, rect);
        return hits.Select(i => masks[i]).OrderBy(m => m.Mask.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Every captured exception mask with its owner pose and proxy bounds, by layer in authored order.</summary>
    internal IEnumerable<(string LayerId, MaskOwner Mask, Bounds Proxy)> MaskProxies() =>
        _masks.SelectMany(l => l.Value.Masks.Select((m, i) => (l.Key, m, l.Value.Proxies[i])));

    /// <summary>The released seated answer of an already bound placement or Support marker, null for an owner whose
    /// seat comes from its exceptional correspondence.</summary>
    internal string? SeatIdentity(MapPrefabReference owner) => _seats.TryGetValue(owner, out var seated)
        ? MapFoliageSupportIdentity.From(seated) + "|" + seated.SpaceId + "|" + seated.Face + "|" + seated.Status : null;

    internal int? Ordinal(string layerId, string surfaceId) => _ordinals[layerId].TryGetValue(surfaceId, out int ordinal) ? ordinal : null;

    /// <summary>How far from an exceptional owner's origin its seat acquisition reaches, in metres on each world axis.</summary>
    internal const float SeatReach = 2;

    internal static MapPrefabReference World(MapPrefabChildKind kind, string id) => new(MapPrefabReferenceScope.World, kind, id);

    internal static MapEffectiveTransform Pose(MapEffectiveResources resources, MapPrefabReference owner) => Pose(resources.Product, owner);

    internal static MapEffectiveTransform Pose(MapEffectiveResolutionProduct resources, MapPrefabReference owner)
    {
        MapEffectiveTransform pose = resources.Transforms.GetValueOrDefault(MapEffectiveContext.Key(owner.Kind, owner.Id), new(0, 0, 0, 0, 1));
        if (owner.Kind == MapPrefabChildKind.Marker)
        {
            var marker = resources.EffectiveMarkers.Single(m => m.Id == owner.Id);
            return pose with { X = marker.Position.X.ToDouble(), Y = marker.Position.Y.ToDouble(), Z = marker.Position.Z.ToDouble(), YawRadians = marker.YawRadians };
        }
        return pose;
    }

    /// <summary>The released exceptional-owner read rectangle, shared with window dependency acquisition.</summary>
    internal static MapSurfaceScope SeatScope(MapExactPoint point, MapQueryLimits? queryLimits = null)
    {
        var frame = WorldFrame.Nearest(point.X.ToSingle(), point.Z.ToSingle());
        float x = point.X.ToSingle() - frame.Anchor.X, z = point.Z.ToSingle() - frame.Anchor.Z;
        return new(frame, new(x - SeatReach, z - SeatReach), new(x + SeatReach, z + SeatReach), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, queryLimits ?? new MapQueryLimits());
    }

    internal string Seated(MapFoliageLayer layer, MapPrefabReference owner, MapEffectiveTransform pose,
        MapFoliageLimits limits, ref MapFoliageWork work, List<string> dependencies)
    {
        // Already bound placements and Support markers need no exceptional correspondence and never resample their seat.
        if (_seats.TryGetValue(owner, out var seated))
        {
            Describe(seated);
            return MapFoliageSupportIdentity.From(seated) ?? throw new MapFoliageRefusal(MapFoliageSupport.Map(seated.Status), work, "owner seat refused");
        }
        MapSupportBinding? binding = _bindings[layer.Id].GetValueOrDefault(owner);
        // A room-bound placement already has the released seated answer above. An explicit-height owner requires
        // its correspondence. RoofLink.Rooms describes clearing/visibility targets and cannot supply a seat.
        if (binding is null) throw new MapFoliageRefusal(MapFoliageStatus.MissingSupport, work, $"owner {owner.Kind} '{owner.Id}' lacks seated support");
        // One bounded support preparation per exceptional owner, never per foliage candidate. The shared selection
        // kernel retains its own released point budgets and all surface/room semantics.
        work = work with { PrepareScopes = checked(work.PrepareScopes + 1) };
        if (DemandDiagnostics.Check(limits, "MapFoliageOwners.MaxPrepareScopes.1", "MaxPrepareScopes", 0, work.PrepareScopes, limits.MaxPrepareScopes, work.PrepareScopes > limits.MaxPrepareScopes, CapacityDemandLifetime.Cumulative)) throw new MapFoliageRefusal(MapFoliageStatus.CapacityExceeded, work, "owner support scopes");
        var point = new MapResourceTransform(pose).Point(new(default, default, default));
        var scope = SeatScope(point, _queryLimits);
        MapScopedSurfaces view = _acquire(scope);
        var prepared = MapSupportPreparation.PrepareFoliage(view);
        var xz = new MapExactXz(point.X, point.Z);
        MapBoundFaceContext shared;
        long contextFaces = 0;
        try
        {
            shared = MapFoliageCandidateSupport.Context(view, prepared, xz, xz, limits.MaxFaces - work.Faces, out _, chargeFaces: count =>
            {
                limits.ChargePreparation?.Invoke(checked(1280 * count));
                contextFaces = checked(contextFaces + count);
            }, faceOrigin: new("MaxFaces", work.Faces, MapFoliageDemandOrigins.Profile(limits).MaxFaces, limits));
        }
        finally { work = work with { Faces = checked(work.Faces + contextFaces) }; }
        work = work with { SurfaceQueries = checked(work.SurfaceQueries + 1) };
        if (MapSupportSelection.BindingRequest(view, binding, xz) is not { } request)
            throw new MapFoliageRefusal(MapFoliageStatus.Invalid, work, "invalid owner support binding");
        MapSupportResult result = MapSupportSelection.Compute(prepared, request.Request, null, request.NamedSurfaceOnly, request.World, shared).Result;
        result = MapFoliageSupportIdentity.ForClearance(view, binding, xz, result);
        Add("owner-binding|" + owner.Kind + "|" + owner.Id + "|" + MapCanonical.HashHex(w => System.Text.Json.JsonSerializer.Serialize(w, binding)));
        foreach (var patch in view.Witness.Present) Add("owner-patch|" + patch.Key + "|" + patch.Value);
        Describe(result);
        return MapFoliageSupportIdentity.From(result) ?? throw new MapFoliageRefusal(MapFoliageSupport.Map(result.Status), work, "owner seated support refused: " + result.Detail);

        void Add(string value)
        {
            limits.ChargePreparation?.Invoke(checked(128 + 2L * System.Text.Encoding.UTF8.GetByteCount(value)));
            dependencies.Add(value);
        }

        void Describe(MapSupportResult selected) => Add("owner-seat|" + owner.Kind + "|" + owner.Id + "|" +
            MapFoliageSupportIdentity.From(selected) + "|" + selected.SpaceId + "|" + selected.Face);
    }
}
