using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.TileWorld;
using static KhaozEngine.Tests.MapDocOracle.ShippedFoliageManifest;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Attested catalog correspondence, independent of either head's policy and of placement membership.</summary>
[UnsupportedOSPlatform("windows")]
internal static class ShippedSceneSelections
{
    internal static IReadOnlyDictionary<string, string[]> Bind(ShippedSceneInputs inputs, MapAssetClosure assets,
        MapEffectiveResources effective)
    {
        var objects = inputs.World.AllObjects().ToDictionary(o => o.Id);
        var placements = effective.Placements.ToDictionary(p => p.PlacementId, StringComparer.Ordinal);
        var mappings = inputs.Manifest.Placements.Select(p =>
                (Source: objects[p.SourceObjectId].ArchetypeId, Native: placements[p.NativePlacementId].AssetId))
            .Concat(inputs.FoliageBindings.SelectMany(b => b.Models.Select(p => (Source: p.Key, Native: p.Value))))
            .GroupBy(p => p.Source, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Native).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var nativeIds = assets.Assets.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in inputs.Manifest.AssetSelections)
        {
            Require(inputs.Catalogs.Archetype(row.SourceArchetypeId) is not null, "unknown scene selection source archetype");
            Require(row.NativeAssetIds.All(nativeIds.Contains), "unknown scene selection native asset");
            Require(!mappings.TryGetValue(row.SourceArchetypeId, out var observed) ||
                observed.All(id => row.NativeAssetIds.Contains(id, StringComparer.Ordinal)), "scene selection correspondence differs from observed assets");
            mappings[row.SourceArchetypeId] = row.NativeAssetIds.ToArray();
        }
        var declared = inputs.Manifest.AssetSelections.Select(r => r.SourceArchetypeId).ToHashSet(StringComparer.Ordinal);
        foreach (string id in DeclaredSourceIds(inputs.SourcePolicy))
            Require(declared.Contains(id), "missing scene selection correspondence");
        return mappings;
    }

    // Policies name the roster to validate. They never supply a correspondence value.
    internal static IEnumerable<string> DeclaredSourceIds(ShippedScenePolicy policy) => policy.ModelExtents.Keys
        .Concat(policy.AnimatedAssets).Concat(policy.Placements.Layers.SelectMany(l => l.AssetIds))
        .Concat(policy.Foliage.MotionProfiles.Keys).Concat(policy.Foliage.ModelCastsShadows.Keys).Distinct(StringComparer.Ordinal);
}
