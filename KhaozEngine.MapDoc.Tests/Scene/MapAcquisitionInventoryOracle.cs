using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Scene;

// Enumerates the captured graph and backing capacities without production inventory or receipt totals.
internal sealed class MapAcquisitionInventoryOracle(IEnumerable<MapPrefabAttachmentTransfer>? borrowed = null)
{
    readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<MapPrefabAttachmentTransfer> _borrowed = new(borrowed ?? [], ReferenceEqualityComparer.Instance);
    internal long Scene(MapScopedScene scene) => 512 + 4L * scene.Scope.Surfaces.Roles.Count +
        8L * (scene.Scope.Surfaces.SpaceIds?.Count ?? 0) + Product(scene.Product) + Surfaces(Field<MapSurfaceSet>(scene, "_surfaces")) +
        View(scene.SurfaceView) + 64L * (scene.Witness.Required.Count + scene.Witness.Present.Count +
            scene.Witness.KnownEmpty.Count + scene.Witness.Unavailable.Count);
    internal long Product(MapEffectiveResolutionProduct product) => Context(product.CapturedContext) + Source(product.SurfaceSource) +
        Water(product.CapturedWater) + Intent(product.CapturedBuildingIntent) + 256 +
        128L * product.Placements.Count + 96L * product.PlacementSupports.Count +
        128L * (product.EffectiveLights.Count + product.EffectiveMarkers.Count + product.MarkerSupports.Count) +
        (Unsafe.SizeOf<MapExactPoint>() + 32L) * Dictionary(product.LightPoints).EnsureCapacity(0) +
        40L * Dictionary(product.WaterCorners).EnsureCapacity(0) + product.WaterCorners.Sum(c => 8L + 48L * c.Value.Count) +
        8L * product.Attachments.Count + product.Attachments.Where(a => !_borrowed.Contains(a)).Sum(a => 128L + 136L * a.Children.Count) +
        40L * Dictionary(product.SupportRequests).EnsureCapacity(0) + 64L * product.SupportRequests.Count +
        8L * product.Placements.Sum(p => p.Tags.Count) + 8L * product.EffectiveMarkers.Sum(m => m.Tags.Count) +
        40L * Dictionary(product.PaintRegions).EnsureCapacity(0) +
        product.PaintRegions.Values.Sum(p => 128L + p.Regions.Sum(r => 128L + 48L * r.Polygon.Count));
    internal long Context(MapEffectiveContext context)
    {
        if (!_seen.Add(context)) return 0;
        return Surfaces(context.Surfaces) + 128 + 96L * context.Transforms.Count + 8L * context.Storeys.Count +
            context.WaterSources.Values.Sum(Body) +
            40L * Dictionary(Field<IReadOnlyDictionary<string, MapSurfaceRef>>(context, "_surfaces")).EnsureCapacity(0) +
            (Unsafe.SizeOf<MapEffectiveTransform>() + 32L) * Dictionary(context.Transforms).EnsureCapacity(0) +
            40L * Dictionary(context.WaterSources).EnsureCapacity(0);
    }
    long Source(IMapSurfaceSource source)
    {
        if (!_seen.Add(source)) return 0;
        if (source is MapSceneSurfaceSource subset)
            return 128 + 32L * ((HashSet<string>)Field<IReadOnlySet<string>>(subset, "_ids")).EnsureCapacity(0) + 8L * subset.Surfaces.Count;
        if (source is MapComposedSurfaceSource composed)
            return 128 + (Unsafe.SizeOf<MapEffectiveTransform?>() + 32L) *
                Dictionary(Field<IReadOnlyDictionary<string, MapEffectiveTransform?>>(composed, "_nativePoses")).EnsureCapacity(0) +
                Source(Field<MapEffectiveSurfaceSource>(composed, "_generated")) + Source(Field<IMapSurfaceSource>(composed, "_native"));
        if (source is MapEffectiveSurfaceSource effective)
        {
            var bounds = Field<IReadOnlyDictionary<MapPatchKey, MapExactRect>>(effective, "_bounds");
            var rows = Field<IReadOnlyList<(MapRecordRef, MapExactRect)>>(effective, "_records");
            int capacity = Field<System.Collections.IList>(rows, "list").Count;
            foreach (var field in rows.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.GetValue(rows) is List<(MapRecordRef, MapExactRect)> backing) capacity = backing.Capacity;
            return 128 + effective.Surfaces.Sum(s => 136L + 8L * (s.IndoorSpan?.DomainTags.Count ?? 0)) +
                (Unsafe.SizeOf<MapPatchKey>() + Unsafe.SizeOf<MapExactRect>() + 24L) * Dictionary(bounds).EnsureCapacity(0) +
                Unsafe.SizeOf<(MapRecordRef, MapExactRect)>() * (long)capacity + 32L * rows.Count;
        }
        return 0;
    }
    long Surfaces(MapSurfaceSet surfaces)
    {
        if (!_seen.Add(surfaces)) return 0;
        return 128 + surfaces.Refs.Sum(s => 128L + 8L * (s.IndoorSpan?.DomainTags.Count ?? 0)) + 8L * surfaces.Refs.Capacity + 64L * surfaces.Patches.Count +
            surfaces.Patches.Values.Sum(p => _seen.Add(p) ? Patch(p) : 0);
    }
    internal long View(MapScopedSurfaces view)
    {
        if (!_seen.Add(view)) return 0;
        var facts = Field<MapAcquiredFacts>(view, "_facts");
        var reads = Field<Dictionary<MapPatchKey, MapPatchRead>>(facts, "_reads");
        var surfaces = Field<SortedDictionary<string, MapSurfaceRef>>(facts, "_surfaces");
        long storage = (Unsafe.SizeOf<MapPatchKey>() + 32L) * reads.EnsureCapacity(0) +
            40L * Field<Dictionary<string, MapRecordRef>>(facts, "_recordIds").EnsureCapacity(0) +
            32L * (Field<HashSet<MapCoveredRange>>(facts, "_empty").EnsureCapacity(0) + Field<HashSet<MapUnavailable>>(facts, "_unavailable").EnsureCapacity(0)) +
            64L * reads.Count + surfaces.Values.Sum(s => 192L + 8L * (s.IndoorSpan?.DomainTags.Count ?? 0));
        return 256 + view.Surfaces.Sum(s => 128L + 8L * (s.IndoorSpan?.DomainTags.Count ?? 0)) + 128L * view.Witness.Present.Count +
            64L * (view.Witness.Records.Count + view.Witness.KnownEmpty.Count + view.Witness.Unavailable.Count) +
            8L * view.Identity.AssetSha256.Count + 4L * view.Scope.Roles.Count + 8L * (view.Scope.SpaceIds?.Count ?? 0) +
            storage + reads.Values.Sum(r => r.Patch is { } p && _seen.Add(p) ? Patch(p) : 0) +
            (view.EffectiveContext is { } context ? Context(context) : 0);
    }
    static long Patch(MapSurfacePatch patch) => 4L * patch.Heights.Length + 8L * patch.Cells.Length + 8L * patch.Presence.Length +
        Unsafe.SizeOf<MapCornerDependency>() * (long)patch.CornerDependencies.Capacity +
        Unsafe.SizeOf<MapEdgeSubdivision>() * (long)patch.EdgeSubdivisions.Capacity + 8L * patch.Records.Capacity + patch.Records.Sum(r => r switch
        {
            MapBoundaryChain chain => 8L * chain.Vertices.Count,
            MapWallStrip => 0,
            MapSpaceFootprint footprint => 4L * footprint.SlotCells.Count,
            MapHorizontalOpening opening => 4L * opening.SlotCells.Count,
            MapCavePortal portal => Unsafe.SizeOf<MapLatticeVertex>() * (long)portal.Interval.Count,
            MapVerticalLink link => 8L * (link.Openings.Count + link.Portals.Count + link.GeometryOwners.Count),
            MapSpaceDoc space => 8L * (space.DomainTags.Count + space.Walls.Count + space.Portals.Count + space.Links.Count),
            MapSurfaceSeam seam => Unsafe.SizeOf<(MapLatticeVertex, MapLatticeVertex)>() * (long)seam.Pairs.Count,
            MapPrismDomain prism => 32L * prism.Footprint.Rects.Count + 8L * prism.Footprint.Rings.Count +
                prism.Footprint.Rings.Sum(r => Unsafe.SizeOf<MapLatticeAddress>() * (long)r.Vertices.Count),
            _ => throw new InvalidOperationException("Extend the oracle for the fixture's record kind.")
        });
    static long Intent(MapBuildingIntent intent) => 128 + 8L * (intent.RoofLinks.Count + intent.RoofGroups.Count + intent.Roofs.Count +
        intent.Storeys.Count + intent.Lights.Count + intent.Markers.Count) +
        intent.RoofLinks.Sum(l => 96L + 40L * (l.Rooms.Count + l.Groups.Count)) + intent.RoofGroups.Sum(g => 32L + 40L * g.Rooms.Count) +
        32L * (intent.Roofs.Count + intent.Storeys.Count) + intent.Lights.Sum(l => 128L + (l.Owner is null ? 0 : 32) + (l.ShadowExclusion is null ? 0 : 64)) +
        intent.Markers.Sum(m => 128L + 8L * m.Tags.Count + (m.SupportBinding is null ? 0 : 64));
    static long Water(MapWaterSet? water) => water is null ? 0 : 128 + 8L * water.Media.Count + water.Bodies.Sum(Body);
    static long Body(MapWaterBodyDoc body) => 128 + 32L * body.Domain.Rects.Count + 32L * body.Covers.Count +
        body.Domain.Rings.Sum(r => 32L + 24L * r.Vertices.Count);
    static Dictionary<TKey, TValue> Dictionary<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> dictionary) where TKey : notnull =>
        dictionary as Dictionary<TKey, TValue> ?? dictionary.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(f => f.GetValue(dictionary)).OfType<Dictionary<TKey, TValue>>().Single();
    static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
}
