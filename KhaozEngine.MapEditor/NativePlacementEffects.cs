using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapEditor;

/// <summary>Reported bounds and an optional conservative installed extent of a placement set.</summary>
public readonly record struct NativePlacementExtent(MapBox3? Bounds, bool Unknown)
{
    public MapBox3? SpatialBounds { get; init; }
}

/// <summary>A host supplies shape bounds without giving MapEditor a physics dependency.</summary>
public interface INativePlacementBounds
{
    /// <summary>Sizes the named placements. Unknown geometry returns Unknown, so it cannot block deletion or repair.
    /// SpatialBounds includes any installed geometry beyond the tight report. Programming errors still throw.</summary>
    NativePlacementExtent Bounds(MapDocument document, MapAssetClosure assets, MapDocRegistry registry,
        IReadOnlyList<string> placementIds);
}

/// <summary>Sizes placements already resolved from the complete expansion the caller holds, as
/// <see cref="INativePlacementBounds.Bounds"/> sizes the same placements after expanding the same document.</summary>
internal interface INativeResolvedPlacementBounds
{
    NativePlacementExtent Bounds(MapAssetClosure assets, IReadOnlyList<MapResolvedPlacement> placements);
}

internal static class NativePlacementEffects
{
    const MapNativeInvalidation Spatial = MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency;
    const MapNativeInvalidation FullReset = MapNativeInvalidation.Placements | Spatial | MapNativeInvalidation.Unbounded;
    internal static MapNativeEditEffects Describe(MapDocument before, MapDocument after, MapAssetClosure? assets,
        MapDocRegistry registry, INativePlacementBounds? bounds, MapAssetClosure? beforeAssets = null,
        Lazy<MapEffectiveResources>? expandedBefore = null, Lazy<MapEffectiveResources>? expandedAfter = null)
    {
        var old = ById(before); var current = ById(after);
        var changed = old.Keys.Union(current.Keys).Where(id => !old.TryGetValue(id, out var a) || !current.TryGetValue(id, out var b) || !SameGeometry(a, b))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pose in before.NativeResourceTransforms ?? [])
            if (pose.Kind == MapPrefabChildKind.Placement && after.NativeResourceTransforms?.FirstOrDefault(p => p.Kind == pose.Kind && p.OwnerId == pose.OwnerId)?.Transform != pose.Transform)
                changed.Add(pose.OwnerId);
        beforeAssets ??= assets;
        MapEffectiveResources? aResources = null, bResources = null;
        bool resolve = before.ResolverIdentity?.ResolverVersion == 2 && assets is not null && beforeAssets is not null &&
            (before.Prefabs is not null || after.Prefabs is not null || before.NativeResourceTransforms is not null || after.NativeResourceTransforms is not null ||
             before.Surfaces.Patches.Keys.Union(after.Surfaces.Patches.Keys).Any(k => !before.Surfaces.Patches.TryGetValue(k, out var a) ||
                 !after.Surfaces.Patches.TryGetValue(k, out var b) || MapSurfaceSemantics.PatchDigest(a) != MapSurfaceSemantics.PatchDigest(b)));
        if (resolve)
        {
            try
            {
                aResources = expandedBefore?.Value ?? MapPrefabResolver.Expand(before, beforeAssets!);
                bResources = expandedAfter?.Value ?? MapPrefabResolver.Expand(after, assets!);
                var a = aResources.Placements.ToDictionary(p => p.PlacementId);
                var b = bResources.Placements.ToDictionary(p => p.PlacementId);
                foreach (string id in a.Keys.Union(b.Keys))
                    if (!a.TryGetValue(id, out var p) || !b.TryGetValue(id, out var q) || p.NumericId != q.NumericId || p.AssetId != q.AssetId ||
                        aResources.Transforms[MapEffectiveContext.Key(MapPrefabChildKind.Placement, id)] != bResources.Transforms[MapEffectiveContext.Key(MapPrefabChildKind.Placement, id)])
                        changed.Add(id);
            }
            catch (MapDocumentException)
            {
                // Unknown old geometry cannot make a valid repair fail. The provider sizes known members below.
                if (changed.Count == 0 && before.Placements.Any(p => p.Y is null)) return Effects(null, null, FullReset);
            }
        }
        if (changed.Count == 0) return Effects(null, null, MapNativeInvalidation.Placements);
        if (assets is null || beforeAssets is null || bounds is null) return Effects(null, null, FullReset);
        var oldExtent = Extent(before, aResources, changed, beforeAssets, registry, bounds);
        var newExtent = Extent(after, bResources, changed, assets, registry, bounds);
        if (oldExtent.Unknown || newExtent.Unknown) return Effects(null, null, FullReset);
        if (oldExtent.Bounds is null && newExtent.Bounds is null) return Effects(null, null, MapNativeInvalidation.Placements);
        return Effects(oldExtent.Bounds, newExtent.Bounds, MapNativeInvalidation.Placements | Spatial) with
        {
            OldSpatialBounds = oldExtent.SpatialBounds,
            NewSpatialBounds = newExtent.SpatialBounds,
            ChildIds = before.Prefabs is not null || after.Prefabs is not null || before.NativeResourceTransforms is not null || after.NativeResourceTransforms is not null
                ? changed.Order(StringComparer.Ordinal).ToArray() : []
        };
    }

    static NativePlacementExtent Extent(MapDocument document, MapEffectiveResources? resources, HashSet<string> changed,
        MapAssetClosure assets, MapDocRegistry registry, INativePlacementBounds bounds)
    {
        if (resources is null)
        {
            var ids = document.Placements.Where(p => changed.Contains(p.Id)).Select(p => p.Id).ToArray();
            return ids.Length == 0 ? default : bounds.Bounds(document, assets, registry, ids);
        }
        var selected = resources.Placements.Where(p => changed.Contains(p.PlacementId)).ToArray();
        if (selected.Length == 0) return default;
        bool authored = selected.All(p => document.Placements.Any(a => a.Id == p.PlacementId));
        // The provider's released contract takes authored placements. Project already resolved leaves into a
        // detached standalone view, with their precise frames and no support resampling or identity allocation.
        var projected = NativeDocumentSnapshot.Clone(document, registry);
        projected.Prefabs = null; projected.BuildingIntent = null; projected.Water = null; projected.Surfaces = new();
        projected.Placements = selected.Select(p => new MapPlacement
        {
            Id = p.PlacementId,
            Kind = p.Kind,
            AssetId = p.AssetId,
            NumericId = p.NumericId,
            X = p.Transform.Position.X,
            Y = p.Transform.Position.Y,
            Z = p.Transform.Position.Z,
            Yaw = p.Transform.YawRadians,
            Scale = p.Transform.Scale,
            Tags = p.Tags.ToList()
        }).ToList();
        projected.NativeResourceTransforms = selected.Select(p => new MapStandaloneResourceTransform(MapPrefabChildKind.Placement, p.PlacementId,
            resources.Transforms[MapEffectiveContext.Key(MapPrefabChildKind.Placement, p.PlacementId)], new string('0', 64))).ToArray();
        // Resolver 2 sizes authored placements of a document with prefabs or resource poses from its complete expansion,
        // which is resources. Reuse it rather than expanding the document again.
        var extent = authored && bounds is INativeResolvedPlacementBounds resolved && document.ResolverIdentity?.ResolverVersion == 2 &&
            (document.Prefabs is not null || document.NativeResourceTransforms is not null)
            ? resolved.Bounds(assets, selected)
            : bounds.Bounds(authored ? document : projected, assets, registry, selected.Select(p => p.PlacementId).ToArray());
        if ((document.Prefabs is not null || document.NativeResourceTransforms is not null) && (extent.SpatialBounds ?? extent.Bounds) is { } spatial)
        {
            double radius = Math.Max(spatial.MaxX - spatial.MinX, Math.Max(spatial.MaxY - spatial.MinY, spatial.MaxZ - spatial.MinZ));
            double padding = selected.Max(p =>
            {
                var pose = resources.Transforms[MapEffectiveContext.Key(MapPrefabChildKind.Placement, p.PlacementId)];
                var t = p.Transform;
                return Math.Max(Math.Abs(pose.X - t.Position.X), Math.Max(Math.Abs(pose.Y - t.Position.Y), Math.Abs(pose.Z - t.Position.Z))) +
                    radius * (Math.Abs(pose.YawRadians - t.YawRadians) + Math.Abs(pose.Scale / t.Scale - 1) + 0.000001) + 1d / 1048576;
            });
            extent = extent with
            {
                SpatialBounds = new(spatial.MinX - padding, spatial.MinY - padding, spatial.MinZ - padding,
                spatial.MaxX + padding, spatial.MaxY + padding, spatial.MaxZ + padding)
            };
        }
        return extent;
    }

    internal static MapNativeEditEffects Combine(MapNativeEditEffects named, MapNativeEditEffects placements,
        MapDocument before, MapDocument after, MapAssetClosure? beforeAssets, MapAssetClosure? afterAssets,
        MapNativeWriteSet writes, MapNativePublicationMode mode,
        Lazy<MapEffectiveResources>? expandedBefore = null, Lazy<MapEffectiveResources>? expandedAfter = null)
    {
        var result = named with
        {
            OldBounds = Union(named.OldBounds, placements.OldBounds),
            NewBounds = Union(named.NewBounds, placements.NewBounds),
            OldSpatialBounds = Union(named.OldSpatialBounds, placements.OldSpatialBounds),
            NewSpatialBounds = Union(named.NewSpatialBounds, placements.NewSpatialBounds),
            Invalidates = named.Invalidates | (placements.OldBounds is not null || placements.NewBounds is not null || placements.Invalidates.HasFlag(MapNativeInvalidation.Unbounded)
                ? placements.Invalidates : placements.Invalidates & ~MapNativeInvalidation.Placements),
            ChildIds = named.ChildIds.Union(placements.ChildIds).Order(StringComparer.Ordinal).ToArray(),
        };
        if (!(writes.Prefabs || writes.BuildingIntent || writes.NativeResourceTransforms || named.LightIds.Count != 0 || named.MarkerIds.Count != 0)) return result;
        // Resolution may refuse old geometry during a repair. Preserve the reset categories of the named resources.
        var resourceReset = (writes.Patches.Count != 0 || writes.SurfaceIds.Count != 0 || writes.SpaceIds.Count != 0)
            ? Spatial : MapNativeInvalidation.None;
        if (named.LightIds.Count != 0 || named.MarkerIds.Count != 0) resourceReset |= MapNativeInvalidation.Residency;
        if (beforeAssets is null || afterAssets is null)
            return result with { Invalidates = result.Invalidates | resourceReset | MapNativeInvalidation.Unbounded };
        try
        {
            var a = expandedBefore?.Value ?? MapPrefabResolver.Expand(before, beforeAssets);
            var b = expandedAfter?.Value ?? MapPrefabResolver.Expand(after, afterAssets);
            var oldExtent = ResourceBounds(a, b, writes, named);
            var newExtent = ResourceBounds(b, a, writes, named);
            result = result with
            {
                OldBounds = Union(result.OldBounds, oldExtent.Bounds),
                NewBounds = Union(result.NewBounds, newExtent.Bounds),
                OldSpatialBounds = Union(result.OldSpatialBounds, oldExtent.Bounds),
                NewSpatialBounds = Union(result.NewSpatialBounds, newExtent.Bounds),
                Invalidates = result.Invalidates | oldExtent.Invalidates | newExtent.Invalidates,
            };
            if (mode == MapNativePublicationMode.NamedWriteSet && !placements.Invalidates.HasFlag(MapNativeInvalidation.Unbounded))
                result = result with { Invalidates = result.Invalidates & ~MapNativeInvalidation.Unbounded };
        }
        catch (MapDocumentException)
        { result = result with { Invalidates = result.Invalidates | resourceReset | MapNativeInvalidation.Unbounded }; }
        return result;
    }
    internal static MapNativeEditEffects NameResources(MapNativeEditEffects named, MapDocument before, MapDocument after, MapNativeWriteSet writes)
    {
        var a = (before.NativeResourceTransforms ?? []).ToDictionary(p => (p.Kind, p.OwnerId));
        var b = (after.NativeResourceTransforms ?? []).ToDictionary(p => (p.Kind, p.OwnerId));
        var changed = a.Keys.Union(b.Keys).Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k)).ToArray();
        string[] Ids(MapPrefabChildKind kind, IEnumerable<string> existing) => existing.Union(changed.Where(k => k.Kind == kind).Select(k => k.OwnerId)).Order(StringComparer.Ordinal).ToArray();
        var oldPlacements = ById(before); var newPlacements = ById(after);
        var moved = oldPlacements.Keys.Union(newPlacements.Keys).Where(id => !oldPlacements.TryGetValue(id, out var x) ||
            !newPlacements.TryGetValue(id, out var y) || !SameGeometry(x, y)).Concat(changed.Where(k => k.Kind == MapPrefabChildKind.Placement).Select(k => k.OwnerId)).ToHashSet();
        var ownerLights = (before.BuildingIntent?.Lights ?? []).Concat(after.BuildingIntent?.Lights ?? [])
            .Where(l => l.Owner is { Kind: MapPrefabChildKind.Placement } owner && moved.Contains(owner.Id)).Select(l => l.Id);
        return named with
        {
            Patches = named.Patches.Union(writes.Patches).Order().ToArray(),
            ChildIds = named.ChildIds.Union(changed.Select(k => k.OwnerId)).Order(StringComparer.Ordinal).ToArray(),
            LightIds = Ids(MapPrefabChildKind.Light, named.LightIds.Concat(ownerLights)),
            MarkerIds = Ids(MapPrefabChildKind.Marker, named.MarkerIds),
            InstanceIds = named.InstanceIds.Union((before.Prefabs?.Transfers ?? []).Concat(after.Prefabs?.Transfers ?? [])
                .Where(t => changed.Contains((t.Binding.Child.Kind, t.Binding.WorldId)) || moved.Contains(t.Binding.WorldId)).Select(t => t.InstanceId)).Order(StringComparer.Ordinal).ToArray(),
            DigestChanges = named.DigestChanges.Concat(changed.Select(k => new MapDigestChange(MapEffectiveContext.Key(k.Kind, k.OwnerId),
                a.GetValueOrDefault(k)?.SourceDigest, b.GetValueOrDefault(k)?.SourceDigest))).ToArray(),
        };
    }
    readonly record struct ResourceExtent(MapBox3? Bounds, MapNativeInvalidation Invalidates);

    static ResourceExtent ResourceBounds(MapEffectiveResources resources, MapEffectiveResources other, MapNativeWriteSet writes, MapNativeEditEffects named)
    {
        MapBox3? box = null;
        var invalidates = MapNativeInvalidation.None;
        var view = resources.CompleteView();
        var surfaces = resources.Surfaces;
        var otherSurfaces = other.Surfaces;
        foreach (var patch in surfaces.Patches.Values.Where(p => writes.Patches.Contains(p.Key) || writes.SurfaceIds.Contains(p.Key.SurfaceId)))
        {
            string key = MapEffectiveContext.Key(MapPrefabChildKind.Surface, patch.Key.SurfaceId);
            if (otherSurfaces.Patches.TryGetValue(patch.Key, out var old) && MapSurfaceSemantics.PatchDigest(old) == MapSurfaceSemantics.PatchDigest(patch) &&
                resources.Transforms.GetValueOrDefault(key) == other.Transforms.GetValueOrDefault(key)) continue;
            if (PatchRole(patch.Key.SurfaceId) == MapSurfaceRole.PaintOverride)
            {
                invalidates |= MapNativeInvalidation.Material | MapNativeInvalidation.Residency;
                // Paint has no physical faces. Its clipped polygons already sit on the canonical target,
                // so effects use those captured points rather than compiling the authored paint height.
                foreach (var region in resources.PaintRegions.Values.SelectMany(set => set.Regions).Where(r => r.PaintId == patch.Key.SurfaceId))
                    foreach (var point in region.Polygon)
                        box = Union(box, new(point.X.ToDouble(), point.Y.ToDouble(), point.Z.ToDouble(), point.X.ToDouble(), point.Y.ToDouble(), point.Z.ToDouble()));
                continue;
            }
            invalidates |= Spatial;
            foreach (var point in MapEffectiveGeometry.Patch(view, patch.Key).ExactVertices)
                box = Union(box, new(point.X.ToDouble(), point.Y.ToDouble(), point.Z.ToDouble(), point.X.ToDouble(), point.Y.ToDouble(), point.Z.ToDouble()));
        }
        var otherPrisms = otherSurfaces.AllRecords().OfType<MapPrismDomain>().ToDictionary(p => p.Id);
        foreach (var prism in surfaces.AllRecords().OfType<MapPrismDomain>().Where(p => writes.RecordIds.Contains(p.Id) || named.SpaceIds.Contains(p.Space.Id)))
        {
            string key = MapEffectiveContext.Key(MapPrefabChildKind.Record, prism.Id);
            bool changed = !otherPrisms.TryGetValue(prism.Id, out var old) ||
                MapPrefabJson.Digest("mapeditor/prism-effects/1", prism) != MapPrefabJson.Digest("mapeditor/prism-effects/1", old) ||
                resources.Transforms.GetValueOrDefault(key) != other.Transforms.GetValueOrDefault(key);
            if (changed) invalidates |= Spatial;
            box = Union(box, MapPrismGeometry.Bounds(view, prism));
        }
        // Effective annotation points use MapEffectiveAnnotations.LightFrame, including its explicit-frame priority.
        var otherLights = MapEffectiveAnnotations.Lights(other).ToDictionary(l => l.Id);
        foreach (var light in MapEffectiveAnnotations.Lights(resources).Where(l => named.LightIds.Contains(l.Id)))
            if (!otherLights.TryGetValue(light.Id, out var old) || light.Position != old.Position || light.Radius != old.Radius ||
                light.Colour != old.Colour || light.Intensity != old.Intensity || light.CastShadows != old.CastShadows)
            {
                invalidates |= MapNativeInvalidation.Residency;
                box = Union(box, MapEffectiveAnnotations.Bounds(light));
            }
        var otherMarkers = MapEffectiveAnnotations.Markers(other).ToDictionary(m => m.Id);
        foreach (var marker in MapEffectiveAnnotations.Markers(resources).Where(m => named.MarkerIds.Contains(m.Id)))
            if (!otherMarkers.TryGetValue(marker.Id, out var old) || marker.Position != old.Position || marker.YawRadians != old.YawRadians || marker.Enabled != old.Enabled)
            {
                invalidates |= MapNativeInvalidation.Residency;
                box = Union(box, MapEffectiveAnnotations.Bounds(marker));
            }
        return new(box, invalidates);

        MapSurfaceRole PatchRole(string id) => surfaces.Refs.Single(s => s.Id == id).Role;
    }
    internal static MapBox3? Union(MapBox3? a, MapBox3? b) => a is { } x ? b is { } y ? x.Union(y) : x : b;
    static Dictionary<string, MapPlacement> ById(MapDocument document) => document.Placements.ToDictionary(p => p.Id, StringComparer.Ordinal);
    static bool SameGeometry(MapPlacement a, MapPlacement b) => a.AssetId == b.AssetId && a.NumericId == b.NumericId &&
        Same(a.X, b.X) && Same(a.Z, b.Z) && Same(a.Yaw, b.Yaw) && Same(a.Scale, b.Scale) &&
        (a.Y is { } ay ? b.Y is { } by && Same(ay, by) : b.Y is null) && Equals(a.SupportBinding, b.SupportBinding);
    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static MapNativeEditEffects Effects(MapBox3? oldBounds, MapBox3? newBounds, MapNativeInvalidation invalidates) =>
        new(oldBounds, newBounds, [], [], [], [], invalidates);
}
