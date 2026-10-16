using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Markers;

/// <summary>One authored marker with its world ID and owner IDs, before height resolution. A prefab child and a
/// framed standalone marker carry the frame its point and binding are local to. An unframed standalone marker is
/// already in world space.</summary>
internal sealed record MapMarkerCandidate(MapLocalMarkerDoc Value, MapEffectiveTransform? Frame, string? InstanceId);

/// <summary>The one marker enumeration shared by expansion, ID and name validation and window anchors. It reads root
/// globals and verified prefab payloads only, through the released reconciliation, remap and exact frames. It
/// never reads geometry and never allocates an identity.</summary>
internal static class MapMarkerExpansion
{
    internal static IReadOnlyList<MapMarkerCandidate> Capture(MapDocument document, MapAssetClosure assets)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        var frames = new Dictionary<string, MapEffectiveTransform>(StringComparer.Ordinal);
        foreach (var pose in document.NativeResourceTransforms ?? [])
            if (pose.Kind == MapPrefabChildKind.Marker) frames.Add(pose.OwnerId, pose.Transform);
        var transferred = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var transfer in document.Prefabs?.Transfers ?? [])
            // A doubly reserved world ID is the identity registry's refusal, so the first transfer stands in here.
            if (!transfer.Retired && transfer.Binding.Child.Kind == MapPrefabChildKind.Marker) transferred.TryAdd(transfer.Binding.WorldId, transfer.InstanceId);
        var result = new List<MapMarkerCandidate>();
        foreach (var marker in document.BuildingIntent?.Markers ?? [])
            result.Add(new(marker, frames.TryGetValue(marker.Id, out var frame) ? frame : null, transferred.GetValueOrDefault(marker.Id)));
        var children = new List<MapMarkerCandidate>();
        foreach (var instance in document.Prefabs?.Instances ?? [])
        {
            if (instance.Removed) continue;
            var asset = assets.BorrowPrefab(instance.ResourceId);
            // Only a declared Marker child or an added one can yield a marker, so other instances skip reconciliation.
            if (!asset.Children.Any(c => c is MapPrefabMarkerChild) && !instance.Overrides.Any(o => o is MapPrefabAddOverride { Value: MapPrefabMarkerChild }))
                continue;
            var reconciled = MapPrefabMaterializationOverrides.Reconcile(asset, instance.Overrides);
            var bindings = instance.Bindings.Entries.Where(b => !b.Removed).ToDictionary(b => b.Child, b => b.WorldId);
            var pose = MapEffectiveTransform.From(instance.Transform);
            foreach (var child in reconciled.EffectiveAsset.Children)
            {
                if (child is not MapPrefabMarkerChild || reconciled.Removed.Contains(child.Key)) continue;
                var world = (MapPrefabMarkerChild)MapPrefabReferences.RemapChild(child, bindings, instance.ParentSpace);
                children.Add(new(world.Value, MapEffectiveTransform.Compose(pose, child.LocalTransform), instance.Id));
            }
        }
        // Root globals keep their authored order and prefab children follow in ordinal world ID order, as released.
        result.AddRange(children.OrderBy(c => c.Value.Id, StringComparer.Ordinal));
        return result.AsReadOnly();
    }

    /// <summary>R6-P3 and T4-R2. Refuses duplicate effective marker IDs and names across world markers and every
    /// prefab-derived marker after current overrides. Disabled markers take part and removed children do not. This
    /// reads metadata only, so it refuses before any support, geometry or callback work.</summary>
    internal static void RequireUnique(MapDocument document, MapAssetClosure assets) => RequireUnique(Capture(document, assets));

    /// <summary>Names compare ordinally and case-sensitively, with no normalization or suffixing. Every collision is
    /// reported, IDs first, each in ordinal order.</summary>
    internal static void RequireUnique(IReadOnlyList<MapMarkerCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var errors = new List<string>();
        Duplicates(candidates, c => c.Value.Id, "ID", errors);
        Duplicates(candidates, c => c.Value.Name, "name", errors);
        if (errors.Count != 0) throw new MapDocumentException(string.Join("\n", errors));
    }

    static void Duplicates(IReadOnlyList<MapMarkerCandidate> candidates, Func<MapMarkerCandidate, string> key, string what, List<string> errors)
    {
        foreach (var group in candidates.GroupBy(key, StringComparer.Ordinal).Where(g => g.Skip(1).Any()).OrderBy(g => g.Key, StringComparer.Ordinal))
            errors.Add($"Markers {string.Join(", ", group.Select(c => "'" + c.Value.Id + "'").Order(StringComparer.Ordinal))} have duplicate marker {what} '{group.Key}'.");
    }
}
