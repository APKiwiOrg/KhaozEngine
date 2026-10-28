using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Bounds = KhaozEngine.MapDoc.Foliage.MapFoliageGenerationContext.Bounds;
using SpatialIndex = KhaozEngine.MapDoc.Foliage.MapFoliageGenerationContext.SpatialIndex;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>The exact captured inputs shared by complete and windowed foliage. No complete-resource claim crosses this seam.</summary>
internal interface IMapFoliageInputs
{
    MapQueryLimits? QueryLimits => null;
    MapFoliageSet? Foliage { get; }
    MapAssetClosure Assets { get; }
    IReadOnlyList<MapSurfaceRef> DeclaredSurfaces { get; }
    IReadOnlyDictionary<string, MapEffectiveTransform> Transforms { get; }
    MapFoliageOwners ClearanceOwners { get; }
    MapScopedSurfaces Acquire(MapSurfaceScope scope);
    IReadOnlyDictionary<string, MapPaintRegionSet> PaintFor(MapSurfaceScope scope);
    IReadOnlyDictionary<MapFoliageChunkKey, string> DensityDigests(MapFoliageLayer layer);
    IReadOnlyList<MapSurfaceRef> SupportFloorsFor(MapFoliageLayer layer, MapFoliageChunkKey chunk);
    IReadOnlyList<MapResolvedPlacement> PlacementsIn(MapFoliageRect bounds, out long candidates);
    MapEffectiveTransform Pose(MapPrefabReference owner);
    void RequireChunk(MapFoliageLayer layer, MapFoliageChunkKey chunk);
}

/// <summary>Spatial facts and dependency selection over a neutral effective product. Both public contexts use this kernel.</summary>
internal sealed class MapFoliageInputKernel : IMapFoliageInputs
{
    readonly SpatialIndex _owners;
    readonly MapResolvedPlacement[] _ownerPlacements;
    readonly Bounds[] _ownerBounds;
    readonly SpatialIndex _paint;
    readonly SpatialIndex _floorIndex;
    readonly MapSurfaceRef[] _floorEntries;
    readonly IReadOnlyDictionary<string, IReadOnlyDictionary<MapFoliageChunkKey, string>> _densityDigests;
    readonly PaintEntry[] _paintEntries;

    readonly IMapSurfaceSource? _retainedSource;
    readonly MapFoliageWindowMetadata? _metadata;
    readonly MapQueryLimits? _queryLimits;
    internal MapFoliageInputKernel(MapFoliageSet? foliage, MapAssetClosure assets, MapEffectiveResolutionProduct resources,
        IMapSurfaceSource? retainedSource = null, MapFoliageWindowMetadata? metadata = null, MapQueryLimits? queryLimits = null)
    {
        Foliage = foliage;
        Assets = assets;
        Resources = resources;
        _retainedSource = retainedSource;
        _metadata = metadata;
        _queryLimits = queryLimits;
        _densityDigests = (foliage?.Layers ?? []).ToDictionary(l => l.Id,
            l => (IReadOnlyDictionary<MapFoliageChunkKey, string>)new ReadOnlyDictionary<MapFoliageChunkKey, string>(
                l.ChunkDigests.ToDictionary(d => d.Key, d => d.Sha256)), StringComparer.Ordinal);
        (_ownerPlacements, _ownerBounds, _owners) = Owners(foliage, resources);
        (_paintEntries, _paint) = Paint(resources);
        (_floorEntries, _floorIndex) = SupportInfluences(resources);
        ClearanceOwners = new(foliage, resources, Acquire, retainedSource is not null, metadata is null ? null : metadata.MaskCandidates, queryLimits);
        SnapshotId = Snapshot(foliage, assets, resources, _paintEntries);
    }
    internal long InventoryBytes => checked(new Scene.MapSceneInventory().Product(Resources) + 512L +
        256L * _ownerPlacements.Length + 256L * _paintEntries.Length + 192L * _floorEntries.Length +
        80L * _densityDigests.Values.Sum(d => d.Count) + (Foliage?.Layers.Sum(l =>
            Scene.MapSceneResourceBounds.FoliageMetadataBytes(l) + l.Chunks.Count * (long)MapFoliageChunk.ByteLength) ?? 0));
    internal MapEffectiveResolutionProduct Resources { get; }
    internal string SnapshotId { get; }
    public MapFoliageSet? Foliage { get; }
    public MapAssetClosure Assets { get; }
    public MapQueryLimits? QueryLimits => _queryLimits;
    public MapFoliageOwners ClearanceOwners { get; }
    public IReadOnlyList<MapSurfaceRef> DeclaredSurfaces => Resources.SurfaceSource.Surfaces;
    public IReadOnlyDictionary<string, MapEffectiveTransform> Transforms => Resources.Transforms;
    public MapEffectiveTransform Pose(MapPrefabReference owner) => MapFoliageOwners.Pose(Resources, owner);
    public void RequireChunk(MapFoliageLayer layer, MapFoliageChunkKey chunk) { }
    public MapScopedSurfaces Acquire(MapSurfaceScope scope)
    {
        if (_queryLimits is { } cap)
        {
            var l = scope.Limits;
            var limits = new MapQueryLimits(Math.Min(l.MaxCandidatePatches, cap.MaxCandidatePatches), Math.Min(l.MaxInspectedFaces, cap.MaxInspectedFaces),
                Math.Min(l.MaxSupportIntersections, cap.MaxSupportIntersections), Math.Min(l.MaxPageReads, cap.MaxPageReads),
                Math.Min(l.MaxRecordReads, cap.MaxRecordReads), Math.Min(l.MaxRecordDepth, cap.MaxRecordDepth))
            { Refinement = l.Refinement is { } reads && cap.Refinement is { } maximum ? reads.Intersect(maximum) : l.Refinement ?? cap.Refinement };
            // Each intersected field keeps both originating profiles for evaluated demand, without changing the minimum.
            Scene.MapDemandOrigins.Intersect(limits, l, cap);
            scope = scope with { Limits = limits };
        }
        return _retainedSource is null ? Resources.Acquire(scope) : MapScopedSurfaces.Acquire(_retainedSource, scope, new[] { Resources.Digest },
            MapScopedSurfaces.EffectiveQueryPolicyVersion, MapScopedSurfaces.EffectiveBuildPolicyVersion);
    }

    public IReadOnlyDictionary<MapFoliageChunkKey, string> DensityDigests(MapFoliageLayer layer) => _densityDigests[layer.Id];

    // A captured per-patch world-bounds index. Only reached floors size the normal and clearance halo, so a
    // distant coarse prefab never sizes a local request. Iterate the reached set to closure: a newly reached lattice
    // may enlarge the clearance read chain. Acquisition, grids and clearance still use the single ReadReach recipe.
    public IReadOnlyList<MapSurfaceRef> SupportFloorsFor(MapFoliageLayer layer, MapFoliageChunkKey chunk)
    {
        return MapFoliageSupport.FloorsFor(layer, chunk, Resources.Transforms,
            bounds => _floorIndex.Query(bounds, out _).Select(i => _floorEntries[i]));
    }

    static (MapSurfaceRef[], SpatialIndex) SupportInfluences(MapEffectiveResolutionProduct resources)
    {
        var entries = new List<MapSurfaceRef>();
        var bounds = new List<Bounds>();
        if (resources.SurfaceSource is not IMapEffectiveSurfaceContext source)
            throw new MapDocumentException("foliage context requires captured effective surface bounds");
        MapEffectiveContext effective = source.EffectiveContext;
        var floors = effective.Surfaces.Refs.Where(s => s.Role == MapSurfaceRole.SupportFloor).ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (MapSurfacePatch patch in effective.Surfaces.Patches.Values)
        {
            if (!floors.TryGetValue(patch.Key.SurfaceId, out MapSurfaceRef? surface)) continue;
            long x = checked(patch.Key.SlotX * 64 + patch.CellMinX), z = checked(patch.Key.SlotZ * 64 + patch.CellMinZ);
            MapExactRect rect = MapEffectiveGeometry.Bounds(new[] { MapLatticeAddress.Corner(x, z), MapLatticeAddress.Corner(x + patch.Width, z),
                MapLatticeAddress.Corner(x + patch.Width, z + patch.Depth), MapLatticeAddress.Corner(x, z + patch.Depth) }
                .Select(a => MapEffectiveGeometry.Vertex(effective, new(surface.Id, a))));
            Bounds b = Bounds.Of(rect);
            entries.Add(surface);
            bounds.Add(b);
        }
        return (entries.ToArray(), new SpatialIndex(bounds));
    }

    /// <summary>The captured paint partition restricted to the scope: per target, the regions whose exact XZ bounds
    /// touch the scope's world rectangle, in published order. Targets with no such region are absent. Each subset has
    /// its own digest over only its regions.</summary>
    public IReadOnlyDictionary<string, MapPaintRegionSet> PaintFor(MapSurfaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        MapExactRect rect = scope.WorldRectangle();
        List<int> hits = _paint.Query(Bounds.Of(rect), out _);
        var result = new Dictionary<string, MapPaintRegionSet>(StringComparer.Ordinal);
        foreach (var target in hits.Where(i => _paintEntries[i].Rect.Touches(rect)).Select(i => _paintEntries[i])
            .GroupBy(e => e.Target, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            PaintEntry[] entries = target.OrderBy(e => e.Order).ToArray();
            string digest = MapCanonical.HashHex(w =>
            {
                w.WriteStartArray(); w.WriteStringValue("kemap/foliage-scoped-paint/1"); w.WriteStringValue(target.Key);
                foreach (PaintEntry entry in entries) w.WriteStringValue(entry.Digest);
                w.WriteEndArray();
            });
            result.Add(target.Key, new MapPaintRegionSet(entries.Select(e => e.Region).ToArray(), digest));
        }
        return new ReadOnlyDictionary<string, MapPaintRegionSet>(result);
    }

    /// <summary>The effective placements that own foliage clearing whose proxy bounds meet <paramref name="bounds"/>, in
    /// effective order. A proxy is the union of the placement's applicable clearance rule and exception mask footprints
    /// through its effective transform, each Door footprint widened to include its origin and the layer's door
    /// clearance. Interaction envelopes never enter a proxy. Callers pad <paramref name="bounds"/> by the clearance read reach.</summary>
    public IReadOnlyList<MapResolvedPlacement> PlacementsIn(MapFoliageRect bounds) => PlacementsIn(bounds, out _);

    /// <summary><see cref="PlacementsIn(MapFoliageRect)"/>, also reporting how many indexed owners it inspected.</summary>
    /// <summary>Every indexed clearance owner with its proxy bounds, in effective order.</summary>
    internal IEnumerable<(MapResolvedPlacement Placement, Bounds Proxy)> OwnerProxies() =>
        _ownerPlacements.Select((p, i) => (p, _ownerBounds[i]));

    public IReadOnlyList<MapResolvedPlacement> PlacementsIn(MapFoliageRect bounds, out long candidates)
    {
        if (!float.IsFinite(bounds.MinX) || !float.IsFinite(bounds.MinZ) || !float.IsFinite(bounds.MaxX) || !float.IsFinite(bounds.MaxZ) ||
            bounds.MinX > bounds.MaxX || bounds.MinZ > bounds.MaxZ)
            throw new ArgumentException("foliage bounds must be finite and ordered.", nameof(bounds));
        var query = new Bounds(bounds.MinX, bounds.MinZ, bounds.MaxX, bounds.MaxZ);
        List<int> hits = _owners.Query(query, out candidates);
        if (_metadata is not null) candidates = _metadata.PlacementCandidates(query);
        hits.Sort();
        return Array.AsReadOnly(hits.Select(i => _ownerPlacements[i]).ToArray());
    }

    // Owner proxies, indexed spatially. Rules are looked up by asset and masks by owner, never scanned per placement.
    static (MapResolvedPlacement[], Bounds[], SpatialIndex) Owners(MapFoliageSet? set, MapEffectiveResolutionProduct resources)
    {
        var rules = new Dictionary<string, List<(MapFoliageLayer Layer, MapFoliageClearanceRule Rule)>>(StringComparer.Ordinal);
        var masks = new Dictionary<string, List<(MapFoliageLayer Layer, MapFoliageMask Mask)>>(StringComparer.Ordinal);
        foreach (MapFoliageLayer layer in set?.Layers ?? [])
        {
            foreach (MapFoliageClearanceRule rule in layer.ClearanceRules)
            {
                if (!rules.TryGetValue(rule.AssetId, out var list)) rules.Add(rule.AssetId, list = new());
                list.Add((layer, rule));
            }
            foreach (MapFoliageMask mask in layer.ExceptionMasks.Where(m => m.Owner is { Scope: MapPrefabReferenceScope.World, Kind: MapPrefabChildKind.Placement }))
            {
                if (!masks.TryGetValue(mask.Owner.Id, out var list)) masks.Add(mask.Owner.Id, list = new());
                list.Add((layer, mask));
            }
        }
        var placements = new List<MapResolvedPlacement>();
        var bounds = new List<Bounds>();
        if (rules.Count == 0 && masks.Count == 0) return (placements.ToArray(), bounds.ToArray(), new SpatialIndex(bounds));
        foreach (MapResolvedPlacement placement in resources.Placements)
        {
            Bounds? proxy = null;
            MapEffectiveTransform pose = resources.Transforms.TryGetValue(MapEffectiveContext.Key(MapPrefabChildKind.Placement, placement.PlacementId),
                out MapEffectiveTransform effective) ? effective : MapEffectiveTransform.From(placement.Transform);
            if (rules.TryGetValue(placement.AssetId, out var applicable))
                foreach (var (layer, rule) in applicable)
                    if (rule.PlacementTag is not { } tag || placement.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                        proxy = Bounds.Union(proxy, MapFoliageGenerationContext.Proxy(pose, rule.Footprint, rule.Kind == MapFoliageClearanceKind.Door ? layer.DoorClearance : null));
            if (masks.TryGetValue(placement.PlacementId, out var owned))
                foreach (var (layer, mask) in owned)
                    proxy = Bounds.Union(proxy, MapFoliageGenerationContext.Proxy(pose, mask.Footprint, mask.Kind == MapFoliageClearanceKind.Door ? layer.DoorClearance : null));
            if (proxy is not { } value) continue;
            placements.Add(placement);
            bounds.Add(value);
        }
        return (placements.ToArray(), bounds.ToArray(), new SpatialIndex(bounds));
    }

    sealed record PaintEntry(string Target, int Order, MapPaintRegion Region, MapExactRect Rect, string Digest);

    // Every published paint region with its exact XZ bounds and its own content digest, indexed spatially.
    static (PaintEntry[], SpatialIndex) Paint(MapEffectiveResolutionProduct resources)
    {
        var entries = new List<PaintEntry>();
        foreach (var (target, set) in resources.PaintRegions.OrderBy(p => p.Key, StringComparer.Ordinal))
            for (int i = 0; i < set.Regions.Count; i++)
            {
                MapPaintRegion region = set.Regions[i];
                if (region.Polygon.Count == 0) continue;
                MapExactValue minX = region.Polygon[0].X, maxX = minX, minZ = region.Polygon[0].Z, maxZ = minZ;
                foreach (MapExactPoint p in region.Polygon)
                {
                    if (p.X.CompareTo(minX) < 0) minX = p.X;
                    if (p.X.CompareTo(maxX) > 0) maxX = p.X;
                    if (p.Z.CompareTo(minZ) < 0) minZ = p.Z;
                    if (p.Z.CompareTo(maxZ) > 0) maxZ = p.Z;
                }
                entries.Add(new(target, i, region, new(minX, minZ, maxX, maxZ), RegionDigest(region)));
            }
        return (entries.ToArray(), new SpatialIndex(entries.Select(e => Bounds.Of(e.Rect)).ToList()));
    }

    static string RegionDigest(MapPaintRegion region) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray(); w.WriteStringValue("kemap/foliage-paint-region/1");
        MapFaceKey face = region.Face;
        w.WriteStringValue(face.OwnerId); w.WriteStringValue(face.Patch?.SurfaceId);
        w.WriteStringValue(face.Patch?.SlotX.ToString(CultureInfo.InvariantCulture));
        w.WriteStringValue(face.Patch?.SlotZ.ToString(CultureInfo.InvariantCulture));
        w.WriteNumberValue(face.Primitive); w.WriteNumberValue(face.ParentTriangle); w.WriteNumberValue(face.Child); w.WriteNumberValue((byte)face.Side);
        w.WriteStringValue(region.PaintId); w.WriteNumberValue(region.Priority);
        w.WriteNumberValue(region.Material.Underlay); w.WriteNumberValue(region.Material.Overlay);
        w.WriteBooleanValue(region.Material.OverlayCovers); w.WriteBooleanValue(region.Material.Feather);
        foreach (MapExactPoint p in region.Polygon)
            foreach (MapExactValue value in new[] { p.X, p.Y, p.Z })
            { w.WriteNumberValue(value.Numerator); w.WriteNumberValue(value.Denominator); }
        w.WriteEndArray();
    });

    static string Snapshot(MapFoliageSet? foliage, MapAssetClosure assets, MapEffectiveResolutionProduct resources, PaintEntry[] paint) =>
        MapCanonical.HashHex(w =>
        {
            w.WriteStartArray(); w.WriteStringValue("kemap/foliage-context/1");
            w.WriteStringValue(resources.Digest); w.WriteStringValue(resources.AnnotationDigest); w.WriteStringValue(assets.Hash);
            w.WriteStringValue(MapFoliageProjection.Digest(foliage));
            foreach (MapResolvedPlacement p in resources.Placements)
            {
                w.WriteStartArray();
                w.WriteStringValue(p.PlacementId); w.WriteStringValue(p.Kind); w.WriteStringValue(p.AssetId);
                if (p.NumericId is { } id) w.WriteNumberValue(id); else w.WriteNullValue();
                foreach (float value in new[] { p.Transform.Position.X, p.Transform.Position.Y, p.Transform.Position.Z, p.Transform.YawRadians, p.Transform.Scale })
                    w.WriteNumberValue(BitConverter.SingleToInt32Bits(value));
                foreach (string tag in p.Tags) w.WriteStringValue(tag);
                w.WriteEndArray();
            }
            foreach (PaintEntry entry in paint) { w.WriteStringValue(entry.Target); w.WriteStringValue(entry.Digest); }
            w.WriteEndArray();
        });

}
