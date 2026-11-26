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
        var stamp = prepared.Stamp;
        var effects = Include(before, prepared.WriteSet, prepared.Effects,
            () => stamp?.Before.ResourcesFor(before) ?? MapPrefabResolver.Expand(before, assets),
            () => stamp?.After.ResourcesFor(prepared.After) ?? MapPrefabResolver.Expand(prepared.After, assets));
        return ReferenceEquals(effects, prepared.Effects) ? prepared : prepared with { Effects = effects };
    }

    /// <summary>Uses the caller's own before and after expansions, reading them only for an edit that can reseat placements.</summary>
    internal static MapNativeEditEffects Include(MapDocument before, MapNativeWriteSet writes, MapNativeEditEffects effects,
        Func<MapEffectiveResources> expandBefore, Func<MapEffectiveResources> expandAfter)
    {
        if (before.ResolverIdentity?.ResolverVersion != 2 ||
            (effects.Patches.Count == 0 && writes.SurfaceIds.Count == 0) ||
            (effects.Invalidates & MapTerrainEdits.Geometry) == 0 ||
            (before.Prefabs?.Instances.Count is not > 0 && before.Placements.All(p => p.Y is not null)))
            return effects;

        MapEffectiveResources previous, current;
        try
        {
            previous = expandBefore();
            current = expandAfter();
        }
        catch (MapDocumentException)
        {
            // The terrain transaction can repair an unresolved document. Its missing extent requires a reset.
            return effects with { Invalidates = effects.Invalidates | MapNativeInvalidation.Unbounded };
        }

        var moved = new List<MapReseatedPlacement>();
        foreach (var support in current.PlacementSupports.OrderBy(p => p.PlacementId, StringComparer.Ordinal))
        {
            string key = MapEffectiveContext.Key(MapPrefabChildKind.Placement, support.PlacementId);
            var oldPose = previous.Transforms[key];
            var newPose = current.Transforms[key];
            if (oldPose != newPose) moved.Add(new(support.PlacementId, oldPose, newPose));
        }
        return moved.Count == 0 ? effects : effects with { ReseatedPlacements = moved.AsReadOnly() };
    }
}
