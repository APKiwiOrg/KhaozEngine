using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Modeled owned payload and collection inventory. Shared immutable strings and source indexes are borrowed.</summary>
internal sealed class MapSceneInventory
{
    readonly HashSet<object> _seen = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    internal long Surfaces(MapSurfaceSet surfaces)
    {
        if (!_seen.Add(surfaces)) return 0;
        long bytes = checked(128L + surfaces.Refs.Sum(s => 128L + 8L * (s.IndoorSpan?.DomainTags.Count ?? 0)) +
            8L * surfaces.Refs.Capacity + 64L * surfaces.Patches.Count);
        foreach (var patch in surfaces.Patches.Values)
            if (_seen.Add(patch)) bytes = checked(bytes + MapSceneAdmission.PatchBytes(patch));
        return bytes;
    }
    internal long View(MapScopedSurfaces view, bool includeContext = true)
    {
        if (!_seen.Add(view)) return 0;
        long bytes = checked(view.MetadataBytes + view.FactsContainerBytes);
        foreach (var key in view.Witness.Present)
            if (view.TryAcquiredPatch(key.Key, out var patch, out _) && patch is not null && _seen.Add(patch))
                bytes = checked(bytes + MapSceneAdmission.PatchBytes(patch));
        if (includeContext && view.EffectiveContext is { } context) bytes = checked(bytes + Context(context));
        return bytes;
    }
    internal long Context(MapEffectiveContext context)
    {
        if (!_seen.Add(context)) return 0;
        return checked(Surfaces(context.Surfaces) + 128L + context.IndexBytes + 96L * context.Transforms.Count +
            8L * context.Storeys.Count + context.WaterSources.Values.Sum(Body));
    }
    internal long Product(MapEffectiveResolutionProduct product)
    {
        if (!_seen.Add(product)) return 0;
        return checked(Context(product.CapturedContext) + Source(product.SurfaceSource) + Water(product.CapturedWater) +
            MapBuildingIntentPreparation.Bytes(product.CapturedBuildingIntent) + MapEffectiveProductPreparation.Tables(product) + 256L +
            128L * product.Placements.Count + 96L * product.PlacementSupports.Count +
            product.PaintRegions.Values.Sum(p => 128L + p.Regions.Sum(r => 128L + 48L * r.Polygon.Count)) +
            128L * (product.EffectiveLights.Count + product.EffectiveMarkers.Count + product.MarkerSupports.Count));
    }
    internal long Water(MapWaterSet? water)
    {
        if (water is null || !_seen.Add(water)) return 0;
        return checked(128L + 8L * water.Media.Count + water.Bodies.Sum(Body));
    }
    internal long Source(IMapSurfaceSource source)
    {
        if (!_seen.Add(source)) return 0;
        return source switch
        {
            MapEffectiveSurfaceSource effective => effective.IndexBytes,
            MapComposedSurfaceSource composed => checked(composed.IndexBytes + Source(composed.Generated) + Source(composed.Native)),
            MapSceneSurfaceSource subset => subset.IndexBytes,
            _ => 0,
        };
    }
    static long Body(MapWaterBodyDoc body) => checked(128L + 32L * body.Domain.Rects.Count +
        32L * body.Covers.Count + body.Domain.Rings.Sum(r => 32L + 24L * r.Vertices.Count));

}
