using System;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Admits resolver table construction separately from installed and captured patch storage.</summary>
internal static class MapEffectiveConstructionPreparation
{
    internal static void BeforeEnumerate(MapEffectiveResolutionInput input, MapAssetClosure assets)
    {
        long rows = input.Placements.Count + (input.NativeResourceTransforms?.Count ?? 0) +
            (input.BuildingIntent is { } intent ? intent.RoofLinks.Count + intent.RoofGroups.Count + intent.Roofs.Count +
                intent.Storeys.Count + intent.Lights.Count + intent.Markers.Count : 0);
        foreach (var placement in input.Placements) rows = checked(rows + Support(placement));
        foreach (var instance in input.Prefabs?.Instances ?? [])
        {
            if (instance.Removed) continue;
            var children = assets.BorrowPrefab(instance.ResourceId).Children;
            rows = checked(rows + Math.Max(children.Count, instance.Bindings.Entries.Count));
        }
        rows = checked(rows + (input.Prefabs?.Transfers.Count ?? 0) +
            (input.Prefabs?.Attachments.Sum(a => 1L + a.Children.Count) ?? 0));
        // Remapping owns several dictionaries, reference rows and replacement arrays per expanded child.
        // Patch and nested payload copies have their separate before-copy admission.
        MapSceneAdmission.Current?.ReserveBytes(checked(4096L + 4096L * rows));
        long Support(MapPlacement placement)
        {
            var asset = assets.GetAsset(placement.AssetId!);
            long count = asset.LightResourceIds.Count;
            foreach (string id in asset.SupportResourceIds)
                if (assets.HasSupport(id))
                {
                    var support = assets.BorrowSupport(id);
                    count = checked(count + support.Surfaces.Refs.Count + support.Surfaces.Patches.Values.Sum(p => (long)p.Records.Count));
                }
            return count;
        }
    }
    internal static long Tables(MapEffectiveEnumeration captured) => checked(512L + 512L * captured.Children.Count +
        96L * captured.Transforms.EnsureCapacity(0) + 40L * (captured.Owners.EnsureCapacity(0) + captured.StoreyOwners.EnsureCapacity(0)) +
        64L * captured.WorldLightFrames.EnsureCapacity(0) + 32L * captured.StandaloneIds.EnsureCapacity(0) +
        8L * (captured.Children.Capacity + captured.Roofs.Capacity + captured.Attachments.Capacity + captured.DigestParts.Capacity) +
        captured.Attachments.Sum(a => 128L + 136L * a.Children.Count));
}
