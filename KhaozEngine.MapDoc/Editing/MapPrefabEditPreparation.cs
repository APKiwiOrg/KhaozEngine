using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>The shared pure candidate gate and descriptor transport for prefab and interior preparations.</summary>
internal static class MapPrefabEditPreparation
{
    internal static readonly MapEffectiveTransform Identity = new(0, 0, 0, 0, 1);
    internal static readonly MapTransform AuthoredIdentity = new(Vector3.Zero, 0, 1);
    [ThreadStatic] static long t_copies;

    /// <summary>The whole-document candidate copies this thread made, counted at entry.</summary>
    internal static long ThreadCopies => t_copies;

    // A detached candidate that shares the immutable foliage set instead of encoding its density.
    internal static MapDocument Copy(MapDocument document)
    {
        t_copies++;
        return MapDocumentSnapshot.Copy(document, MapDocRegistry.CreateDefault());
    }
    internal static MapEffectiveResources Begin(MapDocument document, MapAssetClosure assets)
    {
        MapBoundDocumentValidation.Validate(document, assets);
        return MapDocumentEditFinalizer.Expand(document, assets);
    }

    internal static MapPrefabEditResult Finish(MapDocument after, MapAssetClosure assets, MapEffectiveResources before,
        IReadOnlyList<string> instances, IEnumerable<MapPrefabBinding> bindings, bool unpack = false, bool nativeAssets = false,
        bool interiors = false)
    {
        MapDocumentEditFinalizer.Preflight(after, assets);
        _ = MapPrefabPreflight.Count(after, assets, new());
        for (int i = 0; i < after.Surfaces.Refs.Count; i++)
        {
            var surface = after.Surfaces.Refs[i];
            after.Surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, after.Surfaces.Patches
                    .Where(p => p.Key.SurfaceId == surface.Id).Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value)))),
            };
        }
        MapPrefabAttachmentDescriptors.Refresh(after);
        after = Copy(after);
        MapBoundDocumentValidation.Validate(after, assets);
        var resources = MapDocumentEditFinalizer.Expand(after, assets);
        var errors = MapPrismCoverage.Validate(resources.CompleteView(), new());
        if (errors.Count != 0) throw new MapDocumentException(string.Join("\n", errors));
        MapBoundDocumentValidation.ValidateEffectiveWater(resources, assets);
        var keys = bindings.Select(b => (b.Child.Kind, b.WorldId)).Distinct().ToArray();
        string[] Of(MapPrefabChildKind kind) => Ids(keys.Where(k => k.Kind == kind).Select(k => k.WorldId));
        var surfaces = Of(MapPrefabChildKind.Surface).ToHashSet(StringComparer.Ordinal);
        var records = Of(MapPrefabChildKind.Record).ToHashSet(StringComparer.Ordinal);
        var patches = before.Surfaces.Patches.Values.Concat(resources.Surfaces.Patches.Values)
            .Where(p => surfaces.Contains(p.Key.SurfaceId) || p.Records.Any(r => records.Contains(r.Id))).Select(p => p.Key).Distinct().Order().ToArray();
        var spaces = Ids(before.Surfaces.AllRecords().Concat(resources.Surfaces.AllRecords()).OfType<MapSpaceDoc>()
            .Where(r => records.Contains(r.Id)).Select(r => r.Id));
        var flags = MapNativeInvalidation.Terrain | MapNativeInvalidation.Physics | MapNativeInvalidation.Nav |
            MapNativeInvalidation.Residency | MapNativeInvalidation.Placements | MapNativeInvalidation.Unbounded;
        var effects = new MapNativeEditEffects(null, null, patches, spaces, Of(MapPrefabChildKind.Record), [], flags)
        {
            InstanceIds = Ids(instances),
            ChildIds = Ids(keys.Select(k => k.WorldId)),
            // These inventories are the effective views, including bound implicit lights and suppressed roofs.
            RoofIds = Ids(before.BuildingIntent.Roofs.Concat(resources.BuildingIntent.Roofs).Select(r => r.Id)),
            LightIds = Ids(MapEffectiveAnnotations.Lights(before).Concat(MapEffectiveAnnotations.Lights(resources)).Select(l => l.Id)),
            MarkerIds = Ids(MapEffectiveAnnotations.Markers(before).Concat(MapEffectiveAnnotations.Markers(resources)).Select(m => m.Id)),
            WaterBodyIds = Ids((before.Water?.Bodies ?? []).Concat(resources.Water?.Bodies ?? []).Select(b => b.Id)),
        };
        if (effects.WaterBodyIds.Count != 0) effects = effects with { Invalidates = flags | MapNativeInvalidation.Medium };
        var writes = new MapNativeWriteSet(patches, Of(MapPrefabChildKind.Surface), Of(MapPrefabChildKind.Record), spaces, [], [], unpack)
        {
            Prefabs = true,
            BuildingIntent = unpack || interiors,
            NativeResourceTransforms = unpack || interiors,
            NumericReservations = true,
            NativeAssets = nativeAssets,
            Water = unpack && resources.Water is not null,
        };
        return new(after, writes, effects) { Expansions = new(before, resources) };
    }

    internal static string[] Ids(IEnumerable<string> values) => values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal static IReadOnlyList<MapPrefabChild> InstanceChildren(MapPrefabInstance instance, MapAssetClosure assets,
        out IReadOnlyDictionary<MapPrefabChildKey, MapPrefabChildKey> implicitOwners)
    {
        var reconciled = MapPrefabMaterializationOverrides.Reconcile(assets.GetPrefab(instance.ResourceId), instance.Overrides);
        var explicitChildren = reconciled.LiveAsset.Children;
        var owners = new Dictionary<MapPrefabChildKey, MapPrefabChildKey>();
        // F-R8. Enumerated children take their owner's pose, exactly as expansion does, before the combined asset is
        // validated and remapped. Explicit records anchored on a posed owner's implicit floor then keep their valid pose.
        var implicitChildren = MapEffectiveSupport.Enumerate(assets, explicitChildren, reconciled.EffectiveAsset.MaterializedAttachments, owners: owners)
            .Concat(MapPrefabAttachedLights.Enumerate(assets, explicitChildren, reconciled.EffectiveAsset.MaterializedAttachments, owners: owners))
            .Select(c => MapPrefabResolver.OwnerPose(c, explicitChildren, owners)).ToArray();
        var local = reconciled.LiveAsset with { Children = explicitChildren.Concat(implicitChildren).ToArray() };
        var bound = instance.Bindings.Entries.Where(b => !b.Removed).ToDictionary(b => b.Child, b => b.WorldId);
        implicitOwners = owners;
        return MapPrefabReferences.Remap(local, bound, instance.ParentSpace).Children
            .Select(c => c with { Key = new(c.Key.Kind, bound[c.Key]), LocalTransform = AuthoredIdentity }).ToArray();
    }

    internal static IReadOnlyDictionary<MapPrefabChildKey, MapPrefabChild> Snapshot(MapDocument document, MapAssetClosure assets,
        MapEffectiveResources resources)
    {
        var children = new Dictionary<MapPrefabChildKey, MapPrefabChild>();
        void Add(MapPrefabChild child) => children.Add(child.Key, WorldSlots(child));
        foreach (var instance in document.Prefabs?.Instances ?? [])
            if (!instance.Removed) foreach (var child in InstanceChildren(instance, assets, out _)) Add(child);
        foreach (var p in document.Placements)
            Add(new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, p.Id), MapPrefabReferenceWalker.Copy(p, p.Id, p.SupportBinding)));
        foreach (var surface in resources.Surfaces.Refs)
        {
            var key = new MapPrefabChildKey(MapPrefabChildKind.Surface, surface.Id);
            if (children.ContainsKey(key)) continue;
            var patches = resources.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == surface.Id).Select(p =>
            { var copy = p.Clone(); copy.Records.Clear(); return copy; }).ToArray();
            Add(new MapPrefabSurfaceChild(key, surface with { SemanticSha256 = "" }, patches));
        }
        foreach (var patch in resources.Surfaces.Patches.Values)
            foreach (var record in patch.Records)
                if (!children.ContainsKey(new(MapPrefabChildKind.Record, record.Id)))
                    Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, record.Id), record, patch.Key));
        foreach (var medium in document.Water?.Media ?? []) Add(new MapPrefabMediumChild(new(MapPrefabChildKind.WaterMedium, medium.Key), medium));
        foreach (var body in document.Water?.Bodies ?? []) Add(new MapPrefabWaterChild(new(MapPrefabChildKind.WaterBody, body.Id), body));
        if (document.BuildingIntent is { } intent)
        {
            foreach (var storey in intent.Storeys) Add(new MapPrefabStoreyChild(new(MapPrefabChildKind.Storey, storey.Id), storey.Name));
            foreach (var light in intent.Lights) Add(new MapPrefabLightChild(new(MapPrefabChildKind.Light, light.Id), light));
            foreach (var marker in intent.Markers) Add(new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, marker.Id), marker));
            foreach (var link in intent.RoofLinks) Add(new MapPrefabRoofChild(new(MapPrefabChildKind.RoofLink, link.Id), link));
            foreach (var group in intent.RoofGroups) Add(new MapPrefabRoofGroupChild(new(MapPrefabChildKind.RoofGroup, group.Id), group));
        }
        return children;
    }

    internal static MapPrefabChild WorldSlots(MapPrefabChild child)
    {
        var (copy, occurrences) = MapPrefabReferenceWalker.Child(child, MapPrefabReferenceMap.Identity);
        if (copy is MapPrefabSurfaceChild surface) copy = surface with { Surface = surface.Surface with { SemanticSha256 = "" } };
        return copy with { LocalTransform = AuthoredIdentity, References = MapPrefabReferences.Slots(occurrences, MapPrefabReferenceScope.World) };
    }

    internal static MapEffectiveTransform Relative(MapEffectiveTransform origin, MapEffectiveTransform world)
    {
        if (origin == world) return Identity;
        MapEffectiveTransform.Valid(origin); MapEffectiveTransform.Valid(world);
        var (s, c) = Math.SinCos(origin.YawRadians);
        double x = world.X - origin.X, z = world.Z - origin.Z;
        return MapEffectiveTransform.Valid(new((c * x - s * z) / origin.Scale, (world.Y - origin.Y) / origin.Scale,
            (s * x + c * z) / origin.Scale, world.YawRadians - origin.YawRadians, world.Scale / origin.Scale));
    }

    internal static MapTransform Presentation(MapEffectiveTransform pose) => new(new((float)pose.X, (float)pose.Y, (float)pose.Z),
        (float)pose.YawRadians, (float)pose.Scale);

    internal static void Unpack(MapDocument document, MapAssetClosure assets, MapPrefabInstance instance, MapEffectiveResources resources)
    {
        var children = InstanceChildren(instance, assets, out var implicitOwners).Select(WorldSlots).ToArray();
        var byId = children.ToDictionary(c => c.Key);
        var bindings = instance.Bindings.Entries.Where(b => !b.Removed && byId.ContainsKey(new(b.Child.Kind, b.WorldId))).ToArray();
        var poses = (document.NativeResourceTransforms ?? []).ToList();
        var intent = document.BuildingIntent ?? new(1, [], [], [], []);
        var links = intent.RoofLinks.ToList(); var groups = intent.RoofGroups.ToList();
        var lights = intent.Lights.ToList(); var markers = intent.Markers.ToList(); var storeys = intent.Storeys.ToList();
        foreach (var child in children.OrderBy(c => c.Key.Kind == MapPrefabChildKind.Record ? 1 : 0))
        {
            string id = child.Key.Key;
            switch (child)
            {
                case MapPrefabPlacementChild p:
                    var copy = MapPrefabReferenceWalker.Copy(p.Value, id, p.Value.SupportBinding);
                    var frame = resources.Transforms[MapEffectiveContext.Key(child.Key.Kind, id)];
                    copy.X = (float)frame.X; copy.Y = p.Value.Y is null ? null : (float)frame.Y; copy.Z = (float)frame.Z;
                    copy.Yaw = (float)frame.YawRadians; copy.Scale = (float)frame.Scale;
                    // F-R2. The standalone copy keeps a world-space request computed from the pre-seat frame, never
                    // from the seated floor height that the persisted pose now carries.
                    if (copy.SupportBinding is { } local && resources.SupportRequests.TryGetValue(id, out var world))
                        copy.SupportBinding = local with { ReferenceY = world.ReferenceY, SearchBelow = world.SearchBelow, SearchAbove = world.SearchAbove };
                    copy.NumericId = bindings.Single(b => b.WorldId == id && b.Child.Kind == MapPrefabChildKind.Placement).NumericId;
                    document.Placements.Add(copy);
                    break;
                case MapPrefabSurfaceChild s:
                    document.Surfaces.Refs.Add(s.Surface);
                    foreach (var patch in s.Patches) document.Surfaces.Patches.Add(patch.Key, patch.Clone());
                    break;
                case MapPrefabRecordChild r:
                    document.Surfaces.Patches[r.Anchor].Records.Add(r.Value is MapPrismDomain prism ? prism with { InstanceId = "" } : r.Value);
                    break;
                case MapPrefabStoreyChild s: storeys.Add(new(1, id, s.Name, null)); break;
                case MapPrefabMediumChild m: (document.Water ??= new()).Media.Add(m.Value); break;
                case MapPrefabWaterChild w: (document.Water ??= new()).Bodies.Add(w.Value); break;
                case MapPrefabLightChild l: lights.Add(l.Value); break;
                case MapPrefabMarkerChild m: markers.Add(m.Value); break;
                case MapPrefabRoofChild r: links.Add(r.Value); break;
                case MapPrefabRoofGroupChild g: groups.Add(g.Value); break;
            }
            if (resources.Transforms.TryGetValue(MapEffectiveContext.Key(child.Key.Kind, id), out var pose))
                poses.Add(new(child.Key.Kind, id, pose, MapPrefabAttachmentDescriptors.Digest(child, pose)));
        }
        var reconciled = MapPrefabMaterializationOverrides.Reconcile(assets.GetPrefab(instance.ResourceId), instance.Overrides);
        var remapped = MapPrefabReferences.Remap(reconciled.LiveAsset,
            bindings.ToDictionary(b => b.Child, b => b.WorldId), instance.ParentSpace);
        document.BuildingIntent = new(1, links, groups, lights, markers)
        { Storeys = storeys, Roofs = intent.Roofs.Concat(remapped.Roofs).Distinct().ToArray() };
        document.NativeResourceTransforms = poses;
        var set = document.Prefabs!;
        var rows = resources.Attachments.Where(a => bindings.Any(b => b.Child.Kind == MapPrefabChildKind.Placement && b.WorldId == a.PlacementId)).ToList();
        var boundIds = bindings.ToDictionary(b => b.Child, b => b.WorldId);
        foreach (var owner in children.OfType<MapPrefabPlacementChild>())
        {
            var leaf = assets.GetAsset(owner.Value.AssetId!);
            var ownerBinding = bindings.Single(b => b.Child.Kind == MapPrefabChildKind.Placement && b.WorldId == owner.Key.Key);
            foreach (var kind in new[] { MapPrefabAttachmentKind.Support, MapPrefabAttachmentKind.Light })
                foreach (var resource in kind == MapPrefabAttachmentKind.Support ? leaf.SupportResourceIds : leaf.LightResourceIds)
                {
                    if (rows.Any(r => r.PlacementId == owner.Key.Key && r.Kind == kind && r.ResourceId == resource)) continue;
                    var captured = new List<MapPrefabAttachedChild>();
                    foreach (var pair in implicitOwners.Where(p => p.Value == ownerBinding.Child && p.Key.Key.StartsWith(ownerBinding.Child.Key + "/" + resource + "/", StringComparison.Ordinal)))
                    {
                        string world = boundIds[pair.Key];
                        var child = byId[new(pair.Key.Kind, world)];
                        var pose = resources.Transforms[MapEffectiveContext.Key(pair.Key.Kind, world)];
                        captured.Add(new(pair.Key, world, child is MapPrefabRecordChild record ? record.Anchor : null, Identity,
                            MapPrefabAttachmentDescriptors.Digest(child, pose)));
                    }
                    rows.Add(new(owner.Key.Key, resource, assets.GetResource(resource).Reference.Sha256, kind, captured, false));
                }
        }
        rows = rows.Select(row => row with
        {
            Children = row.Children.Select(c => c with
            { SourceDigest = poses.Single(p => p.Kind == c.LocalChild.Kind && p.OwnerId == c.WorldId).SourceDigest }).ToArray(),
        }).ToList();
        var transferred = bindings.Select(b => b.Child).ToHashSet();
        var tombstone = instance with
        { Removed = true, Unpacked = true, Bindings = new(instance.Bindings.Entries.Select(b => b with { Removed = !transferred.Contains(b.Child) }).ToArray()) };
        document.Prefabs = new(set.PayloadVersion, set.ExpansionVersion,
            set.Instances.Select(i => i.Id == instance.Id ? tombstone : i).ToArray(),
            set.Transfers.Concat(bindings.Select(b => new MapPrefabBindingTransfer(instance.Id, b))).ToArray())
        { Attachments = set.Attachments.Concat(rows).ToArray() };
    }
}
