using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Editing;

internal readonly record struct MapReseatedPlacement(string PlacementId, MapEffectiveTransform Before, MapEffectiveTransform After);

/// <summary>The resolved placement changes caused by a terrain edit, independent of physics shape formats.</summary>
internal static class MapTerrainPlacementChanges
{
    internal static MapPrefabEditResult Include(MapDocument before, MapPrefabEditResult prepared, MapAssetClosure assets)
    {
        var effects = prepared.Effects;
        if (before.ResolverIdentity?.ResolverVersion != 2 ||
            (effects.Patches.Count == 0 && prepared.WriteSet.SurfaceIds.Count == 0) ||
            (effects.Invalidates & MapTerrainEdits.Geometry) == 0 ||
            (before.Prefabs?.Instances.Count is not > 0 && before.Placements.All(p => p.Y is not null)))
            return prepared;

        MapEffectiveResources previous, current;
        try
        {
            var stamp = prepared.Stamp;
            previous = stamp?.Before.ResourcesFor(before) ?? MapPrefabResolver.Expand(before, assets);
            current = stamp?.After.ResourcesFor(prepared.After) ?? MapPrefabResolver.Expand(prepared.After, assets);
        }
        catch (MapDocumentException)
        {
            // The terrain transaction can repair an unresolved document. Its missing extent requires a reset.
            return prepared with { Effects = effects with { Invalidates = effects.Invalidates | MapNativeInvalidation.Unbounded } };
        }

        var moved = new List<MapReseatedPlacement>();
        foreach (var support in current.PlacementSupports.OrderBy(p => p.PlacementId, StringComparer.Ordinal))
        {
            string key = MapEffectiveContext.Key(MapPrefabChildKind.Placement, support.PlacementId);
            var oldPose = previous.Transforms[key];
            var newPose = current.Transforms[key];
            if (oldPose != newPose) moved.Add(new(support.PlacementId, oldPose, newPose));
        }
        return moved.Count == 0 ? prepared : prepared with { Effects = effects with { ReseatedPlacements = moved.AsReadOnly() } };
    }
}
