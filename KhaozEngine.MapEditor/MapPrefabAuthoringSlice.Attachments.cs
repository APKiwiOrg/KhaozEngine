using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapEditor;

internal static partial class MapPrefabAuthoringSlice
{
    static void CaptureImplicitDependencies(MapDocument document, MapAssetClosure assets, MapEffectiveResources resources,
        IReadOnlySet<MapPrefabChildKey> required, IReadOnlyList<MapPrefabChild> definitions,
        IReadOnlyDictionary<MapPrefabChildKey, MapPrefabChildKey> owners)
    {
        var poses = (document.NativeResourceTransforms ?? []).ToList();
        var set = document.Prefabs ?? new(1, 1, [], []);
        var transfers = set.Transfers.ToList();
        var attachments = set.Attachments.ToList();
        var intent = document.BuildingIntent ?? new(1, [], [], [], []);
        var lights = intent.Lights.ToList();
        foreach (var placement in document.Placements.Where(p => required.Contains(new(MapPrefabChildKind.Placement, p.Id))))
        {
            var asset = assets.GetAsset(placement.AssetId!);
            foreach (var kind in new[] { MapPrefabAttachmentKind.Support, MapPrefabAttachmentKind.Light })
                foreach (var resourceId in kind == MapPrefabAttachmentKind.Support ? asset.SupportResourceIds : asset.LightResourceIds)
                {
                    string prefix = placement.Id + "/" + resourceId + "/";
                    var children = definitions.Where(c => owners[c.Key] == new MapPrefabChildKey(MapPrefabChildKind.Placement, placement.Id) &&
                        c.Key.Key.StartsWith(prefix, StringComparison.Ordinal) && (kind == MapPrefabAttachmentKind.Light
                            ? c is MapPrefabLightChild : c is MapPrefabSurfaceChild or MapPrefabRecordChild)).ToArray();
                    if (children.Length == 0) continue;
                    var captured = new List<MapPrefabAttachedChild>();
                    foreach (var child in children.OrderBy(c => c is MapPrefabRecordChild ? 1 : 0))
                    {
                        switch (child)
                        {
                            case MapPrefabSurfaceChild surface:
                                document.Surfaces.Refs.Add(surface.Surface);
                                foreach (var patch in surface.Patches) document.Surfaces.Patches.Add(patch.Key, patch.Clone());
                                break;
                            case MapPrefabRecordChild record: document.Surfaces.Patches[record.Anchor].Records.Add(record.Value); break;
                            case MapPrefabLightChild light: lights.Add(light.Value); break;
                        }
                        var frame = resources.Transforms[MapEffectiveContext.Key(child.Key.Kind, child.Key.Key)];
                        string digest = MapPrefabAttachmentDescriptors.Digest(child, frame);
                        poses.Add(new(child.Key.Kind, child.Key.Key, frame, digest));
                        var relative = MapPrefabEditPreparation.Relative(resources.Transforms[MapEffectiveContext.Key(MapPrefabChildKind.Placement, placement.Id)], frame);
                        captured.Add(new(child.Key, child.Key.Key, child is MapPrefabRecordChild r ? r.Anchor : null, relative, digest));
                        transfers.Add(new(placement.Id, new(child.Key, child.Key.Key, null, false)));
                    }
                    attachments.Add(new(placement.Id, resourceId, assets.GetResource(resourceId).Reference.Sha256, kind, captured, false));
                }
        }
        document.NativeResourceTransforms = poses;
        document.Prefabs = new(set.PayloadVersion, set.ExpansionVersion, set.Instances, transfers) { Attachments = attachments };
        document.BuildingIntent = new(intent.PayloadVersion, intent.RoofLinks, intent.RoofGroups, lights, intent.Markers)
        { Roofs = intent.Roofs, Storeys = intent.Storeys };
    }
}
