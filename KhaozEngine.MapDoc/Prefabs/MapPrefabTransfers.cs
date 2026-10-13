using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Prefabs;

public enum MapPrefabAttachmentKind { Support, Light }
public sealed record MapPrefabAttachedChild(MapPrefabChildKey LocalChild, string WorldId, MapPatchKey? Anchor,
    MapEffectiveTransform OwnerLocalPose, string SourceDigest);
public sealed record MapPrefabAttachmentTransfer(string PlacementId, string ResourceId, string ResourceSha256,
    MapPrefabAttachmentKind Kind, IReadOnlyList<MapPrefabAttachedChild> Children,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Retired);

/// <summary>Canonical captured standalone descriptor pins. Derived surface hashes are not authored descriptor fields.</summary>
internal static class MapPrefabAttachmentDescriptors
{
    internal static MapPrefabChild? Read(MapDocument document, MapPrefabChildKind kind, string id)
    {
        var key = new MapPrefabChildKey(kind, id);
        switch (kind)
        {
            case MapPrefabChildKind.Placement:
                var placement = document.Placements.SingleOrDefault(p => p.Id == id);
                return placement is null ? null : new MapPrefabPlacementChild(key, placement);
            case MapPrefabChildKind.Surface:
                var surface = document.Surfaces.Refs.SingleOrDefault(s => s.Id == id);
                return surface is null ? null : new MapPrefabSurfaceChild(key, surface,
                    document.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == id).Select(p =>
                    { var copy = p.Clone(); copy.Records.Clear(); return copy; }).ToArray());
            case MapPrefabChildKind.Record:
                foreach (var patch in document.Surfaces.Patches.Values)
                    if (patch.Records.SingleOrDefault(r => r.Id == id) is { } record) return new MapPrefabRecordChild(key, record, patch.Key);
                return null;
            case MapPrefabChildKind.Storey:
                var storey = document.BuildingIntent?.Storeys.SingleOrDefault(s => s.Id == id);
                return storey is null ? null : new MapPrefabStoreyChild(key, storey.Name);
            case MapPrefabChildKind.WaterMedium:
                var medium = document.Water?.Media.SingleOrDefault(m => m.Key == id);
                return medium is null ? null : new MapPrefabMediumChild(key, medium);
            case MapPrefabChildKind.WaterBody:
                var water = document.Water?.Bodies.SingleOrDefault(b => b.Id == id);
                return water is null ? null : new MapPrefabWaterChild(key, water);
            case MapPrefabChildKind.Light:
                var light = document.BuildingIntent?.Lights.SingleOrDefault(l => l.Id == id);
                return light is null ? null : new MapPrefabLightChild(key, light);
            case MapPrefabChildKind.Marker:
                var marker = document.BuildingIntent?.Markers.SingleOrDefault(m => m.Id == id);
                return marker is null ? null : new MapPrefabMarkerChild(key, marker);
            case MapPrefabChildKind.RoofLink:
                var link = document.BuildingIntent?.RoofLinks.SingleOrDefault(l => l.Id == id);
                return link is null ? null : new MapPrefabRoofChild(key, link);
            case MapPrefabChildKind.RoofGroup:
                var group = document.BuildingIntent?.RoofGroups.SingleOrDefault(g => g.Id == id);
                return group is null ? null : new MapPrefabRoofGroupChild(key, group);
            default: throw new MapDocumentException("Unsupported standalone descriptor kind.");
        }
    }

    internal static void Refresh(MapDocument document)
    {
        if (document.NativeResourceTransforms is null) return;
        var poses = document.NativeResourceTransforms.Select(p => Read(document, p.Kind, p.OwnerId) is { } child
            ? p with { SourceDigest = Digest(child, p.Transform) } : p).ToArray();
        document.NativeResourceTransforms = poses;
        if (document.Prefabs is not { } set) return;
        document.Prefabs = new(set.PayloadVersion, set.ExpansionVersion, set.Instances, set.Transfers)
        {
            Attachments = set.Attachments.Select(row => row.Retired ? row : row with
            {
                Children = row.Children.Select(c => c with
                { SourceDigest = poses.Single(p => p.Kind == c.LocalChild.Kind && p.OwnerId == c.WorldId).SourceDigest }).ToArray(),
            }).ToArray(),
        };
    }

    internal static string Digest(MapPrefabChild child, MapEffectiveTransform frame)
    {
        var (copy, occurrences) = MapPrefabReferenceWalker.Child(child, MapPrefabReferenceMap.Identity);
        if (copy is MapPrefabSurfaceChild surface) copy = surface with { Surface = surface.Surface with { SemanticSha256 = "" } };
        copy = copy with
        {
            LocalTransform = new(System.Numerics.Vector3.Zero, 0, 1),
            References = MapPrefabReferences.Slots(occurrences, MapPrefabReferenceScope.World),
        };
        return MapPrefabJson.Digest("kemap/effective-world-frame/1", new
        { Descriptor = Identity.MapPrefabIdentity.ChildDigest(copy), Frame = frame });
    }
}
