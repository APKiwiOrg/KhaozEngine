using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Captured attachments are explicit producers, independent of their advertised leaf bytes.</summary>
internal static class MapEffectiveAttachments
{
    internal static IReadOnlyList<MapPrefabAttachmentTransfer> ResolveLocal(MapPrefabReconciledAsset reconciled,
        IReadOnlyDictionary<MapPrefabChildKey, string> bindings, MapEffectiveTransform instance) => ResolveLocal(reconciled, bindings, instance, null);

    internal static IReadOnlyList<MapPrefabAttachmentTransfer> ResolveLocal(MapPrefabReconciledAsset reconciled,
        IReadOnlyDictionary<MapPrefabChildKey, string> bindings, MapEffectiveTransform instance, MapRecordRef? parent)
    {
        var asset = reconciled.EffectiveAsset;
        string Id(MapPrefabChildKind kind, string id) => bindings.TryGetValue(new(kind, id), out var world)
            ? world : throw new MapDocumentException($"No binding for captured {kind} '{id}'.");
        var children = asset.Children.ToDictionary(c => c.Key);
        var worldChildren = MapPrefabReferences.Remap(reconciled.LiveAsset, bindings, parent).Children.ToDictionary(c => c.Key);
        var result = new List<MapPrefabAttachmentTransfer>();
        foreach (var row in asset.MaterializedAttachments)
        {
            if (reconciled.Removed.Contains(new(MapPrefabChildKind.Placement, row.Placement.Id))) continue;
            var owner = MapEffectiveTransform.Compose(instance, row.PlacementFrame);
            var captured = new List<MapPrefabAttachedChild>();
            foreach (var child in row.Children)
            {
                var key = new MapPrefabChildKey(child.Child.Kind, child.Child.Id);
                if (reconciled.Removed.Contains(key)) continue;
                MapPatchKey? anchor = children[key] is MapPrefabRecordChild record
                    ? record.Anchor with { SurfaceId = Id(MapPrefabChildKind.Surface, record.Anchor.SurfaceId) } : null;
                var frame = MapEffectiveTransform.Compose(owner, child.OwnerLocalPose);
                string digest = MapPrefabJson.Digest("kemap/effective-world-frame/1", new
                { Descriptor = MapPrefabIdentity.ChildDigest(worldChildren[key] with { Key = new(key.Kind, Id(key.Kind, key.Key)) }), Frame = frame });
                captured.Add(new(key, Id(key.Kind, key.Key), anchor, child.OwnerLocalPose, digest));
            }
            result.Add(new(Id(MapPrefabChildKind.Placement, row.Placement.Id), row.ResourceId, row.ResourceSha256,
                row.Kind, captured.ToArray(), false));
        }
        return result.AsReadOnly();
    }

    internal static IReadOnlyList<MapPrefabChild> Reuse(MapDocument document, MapPrefabAttachmentTransfer attachment) =>
        Reuse(MapEffectiveResolutionInput.FromDocument(document), attachment);

    internal static IReadOnlyList<MapPrefabChild> Reuse(MapEffectiveResolutionInput document, MapPrefabAttachmentTransfer attachment)
    {
        if (attachment.Retired) return Array.Empty<MapPrefabChild>();
        var result = new List<MapPrefabChild>();
        foreach (var child in attachment.Children)
        {
            if (!document.NativeResourceTransforms!.Any(t => t.Kind == child.LocalChild.Kind && t.OwnerId == child.WorldId && t.SourceDigest == child.SourceDigest))
                throw new MapDocumentException($"Missing captured descriptor pin for '{child.WorldId}'.");
            var key = new MapPrefabChildKey(child.LocalChild.Kind, child.WorldId);
            switch (child.LocalChild.Kind)
            {
                case MapPrefabChildKind.Surface:
                    var surface = document.Surfaces.Refs.SingleOrDefault(s => s.Id == child.WorldId)
                        ?? throw new MapDocumentException("Missing captured support surface.");
                    result.Add(new MapPrefabSurfaceChild(key, surface, document.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == child.WorldId).Select(p =>
                    { var copy = p.Clone(); copy.Records.Clear(); return copy; }).ToArray()));
                    break;
                case MapPrefabChildKind.Record:
                    if (child.Anchor is not { } anchor || !document.Surfaces.TryGetRecord(new(child.WorldId, anchor), out var record))
                        throw new MapDocumentException("Missing captured record anchor.");
                    result.Add(new MapPrefabRecordChild(key, record!, anchor));
                    break;
                case MapPrefabChildKind.Light:
                    var light = document.BuildingIntent?.Lights.SingleOrDefault(l => l.Id == child.WorldId)
                        ?? throw new MapDocumentException("Missing captured light.");
                    result.Add(new MapPrefabLightChild(key, light));
                    break;
                default: throw new MapDocumentException("Invalid captured attachment child kind.");
            }
            var descriptor = result[^1];
            var frame = document.NativeResourceTransforms!.Single(t => t.Kind == child.LocalChild.Kind && t.OwnerId == child.WorldId).Transform;
            if (MapPrefabAttachmentDescriptors.Digest(descriptor, frame) != child.SourceDigest)
                throw new MapDocumentException($"Captured descriptor '{child.WorldId}' differs from its persisted digest.");
        }
        return result.AsReadOnly();
    }
}
