using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Implicit Light children of prefab leaves. Each consuming placement gets one child per advertised light
/// resource, keyed <c>placement/resource/light</c>, with <c>$owner</c> bound to that placement.</summary>
internal static class MapPrefabAttachedLights
{
    internal static IReadOnlyList<MapPrefabChild> Enumerate(MapAssetClosure assets, IReadOnlyList<MapPrefabChild> children,
        IReadOnlyList<MapPrefabLocalMaterialization>? materialized = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(children);
        var suppressed = MapEffectiveSupport.Suppressed(materialized, MapPrefabAttachmentKind.Light);
        var result = new List<MapPrefabChild>();
        foreach (MapPrefabPlacementChild placement in MapEffectiveSupport.Placements(children))
            foreach (string resourceId in assets.GetAsset(placement.Value.AssetId!).LightResourceIds)
            {
                if (suppressed.Contains((placement.Key.Key, resourceId))) continue;
                MapLocalLightDoc light = MapLightFile.Read(assets.GetResource(resourceId).Bytes, resourceId);
                string key = MapEffectiveSupport.Key(placement.Key.Key, resourceId, light.Id);
                var owner = light.Owner is null ? null : new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, placement.Key.Key);
                result.Add(new MapPrefabLightChild(new(MapPrefabChildKind.Light, key), light with { Id = key, Owner = owner }));
            }
        return result;
    }
}
