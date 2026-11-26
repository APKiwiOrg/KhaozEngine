using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>One shape extent of a placement reseated by terrain, kept separate to avoid claiming tiles between extents.</summary>
internal readonly record struct MapTerrainPlacementBounds(string PlacementId, MapBox3 Bounds)
{
    internal static IReadOnlyList<MapTerrainPlacementBounds> Of(MapBuiltWorld world, MapNativeEditEffects effects)
    {
        if (!world.IsNative || effects.ReseatedPlacements.Count == 0) return [];

        var placements = world.Document.Placements.ToDictionary(p => p.PlacementId, StringComparer.Ordinal);
        var assets = new Dictionary<string, MapAssetShapes>(StringComparer.Ordinal);
        var bounds = new List<MapTerrainPlacementBounds>();
        try
        {
            foreach (var change in effects.ReseatedPlacements)
            {
                if (!placements.TryGetValue(change.PlacementId, out var placement)) continue;
                var built = world.Placement(change.PlacementId);
                Add(change.Before);
                Add(change.After);

                void Add(MapEffectiveTransform pose)
                {
                    // A change in Y can change registration padding on X and Z when alignment changes.
                    var (collider, envelope) = pose == built.EffectiveTransform
                        ? (built.ColliderBounds, built.Envelope.Bounds)
                        : MapPlacementShapes.BoundsAt(placement, Asset(placement.AssetId), pose);
                    if (collider is { } solid) bounds.Add(new(change.PlacementId, solid));
                    if (collider != envelope) bounds.Add(new(change.PlacementId, envelope));
                }
            }
            return bounds;
        }
        finally
        {
            foreach (var asset in assets.Values) asset.Dispose();
        }

        MapAssetShapes Asset(string id)
        {
            if (!assets.TryGetValue(id, out var asset))
                assets.Add(id, asset = MapAssetShapes.Read(world.Document.AssetClosure, id));
            return asset;
        }
    }
}
