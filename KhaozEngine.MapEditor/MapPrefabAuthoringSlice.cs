using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEditor;

/// <summary>Captures only the authored dependencies needed to validate a detached asset.</summary>
internal static partial class MapPrefabAuthoringSlice
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

    internal static MapDocument Create(MapDocument context, MapAssetClosure assets, MapPrefabAsset asset, MapRecordRef? parent,
        IReadOnlySet<MapPrefabChildKey> excluded)
    {
        var resources = MapPrefabResolver.Expand(context, assets);
        var snapshot = MapPrefabEditPreparation.Snapshot(context, assets, resources).ToDictionary(p => p.Key, p => p.Value);
        var implicitOwners = new Dictionary<MapPrefabChildKey, MapPrefabChildKey>();
        var standalone = context.Placements.Select(p =>
        {
            var value = MapPrefabJson.Copy(p); value.NumericId = null;
            return MapPrefabEditPreparation.WorldSlots(new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, p.Id), value));
        }).ToArray();
        var suppressed = (context.Prefabs?.Attachments ?? []).Where(a => !a.Retired).Select(a => new MapPrefabLocalMaterialization(
            new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, a.PlacementId), a.ResourceId, a.ResourceSha256, a.Kind,
            MapPrefabEditPreparation.Identity, [])).ToArray();
        var implicitChildren = MapEffectiveSupport.Enumerate(assets, standalone, suppressed, allowOpaque: true, owners: implicitOwners)
            .Concat(MapPrefabAttachedLights.Enumerate(assets, standalone, suppressed, allowOpaque: true, owners: implicitOwners)).ToArray();
        var expanded = standalone.Concat(implicitChildren).ToArray();
        var implicitDefinitions = MapPrefabReferences.Remap(new(1, "prefab-authoring-dependencies", expanded),
            expanded.ToDictionary(c => c.Key, c => c.Key.Key), null).Children.Where(c => implicitOwners.ContainsKey(c.Key)).ToArray();
        foreach (var child in implicitDefinitions) snapshot[child.Key] = MapPrefabEditPreparation.WorldSlots(child);
        var required = new HashSet<MapPrefabChildKey>();
        var pending = new Queue<MapPrefabChildKey>();
        void Add(MapPrefabChildKind kind, string id)
        {
            var key = new MapPrefabChildKey(kind, id);
            if (excluded.Contains(key)) throw new MapDocumentException("A detached prefab dependency belongs to its selected source children.");
            if (required.Add(key)) pending.Enqueue(key);
        }
        void References(MapPrefabChild child, bool worldOnly)
        {
            foreach (var slot in child.References.Where(s => !worldOnly || s.Target.Scope == MapPrefabReferenceScope.World)) Add(slot.Target.Kind, slot.Target.Id);
            IEnumerable<MapPrefabReference> direct = child switch
            {
                MapPrefabLightChild { Value.Owner: { } owner } => new[] { owner },
                MapPrefabRoofChild roof => new[] { roof.Value.Roof }.Concat(roof.Value.Rooms).Concat(roof.Value.Groups),
                MapPrefabRoofGroupChild group => group.Value.Rooms,
                _ => [],
            };
            foreach (var reference in direct.Where(r => !worldOnly || r.Scope == MapPrefabReferenceScope.World)) Add(reference.Kind, reference.Id);
        }
        foreach (var child in asset.Children) References(child, true);
        foreach (var roof in asset.Roofs.Where(r => r.Scope == MapPrefabReferenceScope.World)) Add(roof.Kind, roof.Id);
        if (parent is not null) { Add(MapPrefabChildKind.Record, parent.Id); Add(MapPrefabChildKind.Surface, parent.Anchor.SurfaceId); }
        while (pending.TryDequeue(out var key))
        {
            if (!snapshot.TryGetValue(key, out var child)) throw new MapDocumentException("A detached prefab World dependency is absent: " + key.Key);
            if (implicitOwners.TryGetValue(key, out var owner)) Add(owner.Kind, owner.Key);
            foreach (var produced in implicitOwners.Where(p => p.Value == key)) Add(produced.Key.Kind, produced.Key.Key);
            References(child, false);
            if (child is MapPrefabRecordChild { Value: MapSpaceDoc space })
                foreach (var domain in snapshot.Values.OfType<MapPrefabRecordChild>().Where(r => r.Value is MapSpaceFootprint f && f.Space.Id == space.Id ||
                             r.Value is MapPrismDomain p && p.Space.Id == space.Id)) Add(domain.Key.Kind, domain.Key.Key);
            foreach (var row in resources.Attachments.Where(r => r.PlacementId == key.Key && key.Kind == MapPrefabChildKind.Placement ||
                         r.Children.Any(c => c.LocalChild.Kind == key.Kind && c.WorldId == key.Key)))
            {
                Add(MapPrefabChildKind.Placement, row.PlacementId);
                foreach (var childBinding in row.Children) Add(childBinding.LocalChild.Kind, childBinding.WorldId);
            }
        }
        var document = NativeDocumentSnapshot.Clone(context, MapDocRegistry.CreateDefault());
        document.Tiles = null;
        foreach (var instance in (context.Prefabs?.Instances ?? []).Where(i => !i.Removed && i.Bindings.Entries.Any(b => required.Contains(new(b.Child.Kind, b.WorldId)))))
            MapPrefabEditPreparation.Unpack(document, assets, instance, resources);
        CaptureImplicitDependencies(document, assets, resources, required, implicitDefinitions, implicitOwners);
        bool Has(MapPrefabChildKind kind, string id) => required.Contains(new(kind, id));
        document.Placements.RemoveAll(p => !Has(MapPrefabChildKind.Placement, p.Id));
        document.Surfaces.Refs.RemoveAll(s => !Has(MapPrefabChildKind.Surface, s.Id));
        foreach (var key in document.Surfaces.Patches.Keys.Where(k => !Has(MapPrefabChildKind.Surface, k.SurfaceId)).ToArray()) document.Surfaces.Patches.Remove(key);
        foreach (var patch in document.Surfaces.Patches.Values) patch.Records.RemoveAll(r => !Has(MapPrefabChildKind.Record, r.Id));
        if (document.Water is { } water)
        { water.Media.RemoveAll(m => !Has(MapPrefabChildKind.WaterMedium, m.Key)); water.Bodies.RemoveAll(b => !Has(MapPrefabChildKind.WaterBody, b.Id)); }
        var intent = document.BuildingIntent ?? new(1, [], [], [], []);
        document.BuildingIntent = new(1, intent.RoofLinks.Where(r => Has(MapPrefabChildKind.RoofLink, r.Id)).ToArray(),
            intent.RoofGroups.Where(g => Has(MapPrefabChildKind.RoofGroup, g.Id)).ToArray(), intent.Lights.Where(l => Has(MapPrefabChildKind.Light, l.Id)).ToArray(),
            intent.Markers.Where(m => Has(MapPrefabChildKind.Marker, m.Id)).ToArray())
        { Storeys = intent.Storeys.Where(s => Has(MapPrefabChildKind.Storey, s.Id)).ToArray(), Roofs = intent.Roofs.Where(r => Has(r.Kind, r.Id)).ToArray() };
        document.NativeResourceTransforms = (document.NativeResourceTransforms ?? []).Where(p => Has(p.Kind, p.OwnerId)).ToArray();
        document.Prefabs = new(1, 1, [], (document.Prefabs?.Transfers ?? [])
            .Where(t => !t.Retired && !t.Binding.Removed && Has(t.Binding.Child.Kind, t.Binding.WorldId)).ToArray())
        { Attachments = (document.Prefabs?.Attachments ?? []).Where(a => !a.Retired && Has(MapPrefabChildKind.Placement, a.PlacementId)).ToArray() };
        document.ScatterLayers.Clear(); document.CompanionLayers.Clear(); document.ScatterOverrides.Clear(); document.Exclusions.Clear();
        document.Spawns.Clear(); document.PlayerSpawns.Clear(); document.Regions.Clear();
        document.TerrainOverrides = null;
        PrefabSurfaceDigests(document);
        MapPrefabAttachmentDescriptors.Refresh(document);
        return document;
    }

    static void PrefabSurfaceDigests(MapDocument document)
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
