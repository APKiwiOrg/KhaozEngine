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

/// <summary>The one marker enumeration shared by expansion, name validation and window anchors. It reads root
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
            if (!transfer.Retired && transfer.Binding.Child.Kind == MapPrefabChildKind.Marker) transferred.Add(transfer.Binding.WorldId, transfer.InstanceId);
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
}
