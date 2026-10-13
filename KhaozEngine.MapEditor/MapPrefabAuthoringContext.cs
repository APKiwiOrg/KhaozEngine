using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEditor;

/// <summary>Preserves the captured map around a detached asset, retaining its source's reservations.</summary>
internal static class MapPrefabAuthoringContext
{
    internal static string InstanceId(MapDocument context)
    {
        var instances = (context.Prefabs?.Instances ?? []).Select(i => i.Id)
            .Concat((context.Prefabs?.Transfers ?? []).Select(t => t.InstanceId)).ToHashSet(StringComparer.Ordinal);
        var reserved = (context.Prefabs?.Instances ?? []).SelectMany(i => i.Bindings.Entries)
            .Concat((context.Prefabs?.Transfers ?? []).Select(t => t.Binding))
            .Select(b => new MapPrefabChildKey(b.Child.Kind, b.WorldId))
            .Concat((context.Prefabs?.Attachments ?? []).SelectMany(a => a.Children).Select(c => new MapPrefabChildKey(c.LocalChild.Kind, c.WorldId)))
            .Concat(context.Surfaces.Refs.Select(s => new MapPrefabChildKey(MapPrefabChildKind.Surface, s.Id)))
            .Concat(context.Surfaces.AllRecords().Select(r => new MapPrefabChildKey(MapPrefabChildKind.Record, r.Id)))
            .Concat(context.Placements.Select(p => new MapPrefabChildKey(MapPrefabChildKind.Placement, p.Id)))
            .Concat(MapPrefabIdentityRegistry.GlobalStandalone(context).Select(p => new MapPrefabChildKey(p.Kind, p.Id))).ToHashSet();
        for (long suffix = 0; ; suffix++)
        {
            string id = "prefab-local-session" + (suffix == 0 ? "" : "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string prefix = "prefab/" + MapPrefabBindingPlanner.Escape(id) + "/";
            if (!instances.Contains(id) && !reserved.Any(k => k.Key == id || k.Key.StartsWith(prefix, StringComparison.Ordinal))) return id;
        }
    }

    internal static MapDocument Create(MapDocument context, MapAssetClosure assets, IReadOnlySet<MapPrefabChildKey> removed)
    {
        // Check effective foreign records before removing a source surface or hiding a live source.
        var resources = MapPrefabResolver.Expand(context, assets);
        foreach (var child in MapPrefabEditPreparation.Snapshot(context, assets, resources).Values.OfType<MapPrefabRecordChild>())
        {
            if (removed.Contains(child.Key)) continue;
            foreach (var reference in child.References)
                if (removed.Contains(new(reference.Target.Kind, reference.Target.Id)))
                    throw new MapDocumentException($"Retained record '{child.Key.Key}' references removed {reference.Target.Kind} '{reference.Target.Id}' at '{reference.Path}'.");
        }
        var document = NativeDocumentSnapshot.Clone(context, MapDocRegistry.CreateDefault());
        document.Tiles = null;
        bool Removed(MapPrefabChildKind kind, string id) => removed.Contains(new(kind, id));
        document.Placements.RemoveAll(p => Removed(MapPrefabChildKind.Placement, p.Id));
        document.Surfaces.Refs.RemoveAll(s => Removed(MapPrefabChildKind.Surface, s.Id));
        foreach (var key in document.Surfaces.Patches.Keys.Where(k => Removed(MapPrefabChildKind.Surface, k.SurfaceId)).ToArray())
            document.Surfaces.Patches.Remove(key);
        foreach (var patch in document.Surfaces.Patches.Values) patch.Records.RemoveAll(r => Removed(MapPrefabChildKind.Record, r.Id));
        if (document.Water is { } water)
        {
            water.Media.RemoveAll(m => Removed(MapPrefabChildKind.WaterMedium, m.Key));
            water.Bodies.RemoveAll(b => Removed(MapPrefabChildKind.WaterBody, b.Id));
        }
        if (document.BuildingIntent is { } intent)
            document.BuildingIntent = new(intent.PayloadVersion,
                intent.RoofLinks.Where(r => !Removed(MapPrefabChildKind.RoofLink, r.Id)).ToArray(),
                intent.RoofGroups.Where(g => !Removed(MapPrefabChildKind.RoofGroup, g.Id)).Select(g => g with
                {
                    // The local replacement has its own room IDs. Keep foreign group membership while the
                    // chosen source's rooms are absent, without changing the captured map or exported group reference.
                    Rooms = g.Rooms.Where(r => !Removed(r.Kind, r.Id)).ToArray()
                }).ToArray(),
                intent.Lights.Where(l => !Removed(MapPrefabChildKind.Light, l.Id)).ToArray(),
                intent.Markers.Where(m => !Removed(MapPrefabChildKind.Marker, m.Id)).ToArray())
            {
                Storeys = intent.Storeys.Where(s => !Removed(MapPrefabChildKind.Storey, s.Id)).ToArray(),
                Roofs = intent.Roofs.Where(r => !Removed(r.Kind, r.Id)).ToArray()
            };
        if (document.NativeResourceTransforms is { } poses)
            document.NativeResourceTransforms = poses.Where(p => !Removed(p.Kind, p.OwnerId)).ToArray();
        if (document.Prefabs is { } set)
        {
            var sources = set.Instances.Where(i => i.Bindings.Entries.Any(b => Removed(b.Child.Kind, b.WorldId)))
                .Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            document.Prefabs = new(set.PayloadVersion, set.ExpansionVersion, set.Instances.Select(i => sources.Contains(i.Id)
                ? i with { Removed = true, Bindings = new(i.Bindings.Entries.Select(b => b with { Removed = true }).ToArray()) } : i).ToArray(),
                set.Transfers.Where(t => !sources.Contains(t.InstanceId) && !Removed(t.Binding.Child.Kind, t.Binding.WorldId)).ToArray())
            {
                Attachments = set.Attachments.Where(a => !Removed(MapPrefabChildKind.Placement, a.PlacementId) &&
                    !a.Children.Any(c => Removed(c.LocalChild.Kind, c.WorldId))).ToArray()
            };
        }
        RefreshSurfaceDigests(document);
        MapPrefabAttachmentDescriptors.Refresh(document);
        return document;
    }

    static void RefreshSurfaceDigests(MapDocument document)
    {
        for (int i = 0; i < document.Surfaces.Refs.Count; i++)
        {
            var surface = document.Surfaces.Refs[i];
            document.Surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, document.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id)
                    .Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
            };
        }
    }
}
