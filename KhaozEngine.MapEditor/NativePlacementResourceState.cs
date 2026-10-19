using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEditor;

[Flags]
internal enum MapPlacementPoseFields { None = 0, X = 1, Y = 2, Z = 4, Yaw = 8, Scale = 16 }

/// <summary>Exact standalone owner state and its captured producers. Unrelated resources are not restored.</summary>
internal sealed class NativePlacementResourceState
{
    readonly MapDocument _snapshot;
    readonly string _id;
    readonly int _index;
    readonly HashSet<(MapPrefabChildKind Kind, string Id)> _keys;
    readonly HashSet<(string Instance, MapPrefabChildKey Child)> _transfers;
    NativePlacementResourceState(MapDocument document, string id)
    {
        _snapshot = CopyResources(document);
        _id = id;
        _index = document.Placements.FindIndex(p => p.Id == id);
        _keys = Keys(document, id);
        _transfers = (document.Prefabs?.Transfers ?? []).Where(t => _keys.Contains((t.Binding.Child.Kind, t.Binding.WorldId)))
            .Select(t => (t.InstanceId, t.Binding.Child)).ToHashSet();
    }
    // Foliage is an immutable set shared by reference, so capturing it copies no density.
    static MapDocument CopyResources(MapDocument document) => new()
    {
        Placements = document.Placements.Select(NativePlacementTransaction.Copy).ToList(),
        Surfaces = document.Surfaces.Clone(),
        Prefabs = document.Prefabs?.Clone(),
        BuildingIntent = document.BuildingIntent?.Clone(),
        NativeResourceTransforms = document.NativeResourceTransforms?.ToArray(),
        Foliage = document.Foliage,
    };
    internal static NativePlacementResourceState Capture(MapDocument document, string placementId) => new(document, placementId);
    internal MapNativeWriteSet WriteSet => Writes(_snapshot, _keys);

    /// <summary>The write set between two captures of one edit. It names the foliage root exactly when the logical
    /// foliage differs, as when a remap moves owner references, never for an unchanged root.</summary>
    internal static MapNativeWriteSet Between(NativePlacementResourceState before, NativePlacementResourceState after) =>
        Union(before.WriteSet, after.WriteSet) with
        {
            Foliage = MapFoliageProjection.Digest(before._snapshot.Foliage) != MapFoliageProjection.Digest(after._snapshot.Foliage),
        };

    internal static void Restore(MapDocument document, NativePlacementResourceState state)
    {
        var source = CopyResources(state._snapshot);
        var keys = new HashSet<(MapPrefabChildKind Kind, string Id)>(state._keys);
        foreach (var t in document.Prefabs?.Transfers ?? [])
            if (state._transfers.Contains((t.InstanceId, t.Binding.Child))) keys.Add((t.Binding.Child.Kind, t.Binding.WorldId));
        var currentOwner = document.Placements.FirstOrDefault(p => keys.Contains((MapPrefabChildKind.Placement, p.Id)));
        if (currentOwner is not null) keys.UnionWith(Keys(document, currentOwner.Id));
        document.Placements.RemoveAll(p => keys.Contains((MapPrefabChildKind.Placement, p.Id)));
        if (source.Placements.FirstOrDefault(p => p.Id == state._id) is { } placement)
            document.Placements.Insert(Math.Min(Math.Max(0, state._index), document.Placements.Count), placement);
        document.NativeResourceTransforms = document.NativeResourceTransforms is null && source.NativeResourceTransforms is null ? null :
            RestoreItems(document.NativeResourceTransforms, source.NativeResourceTransforms, p => keys.Contains((p.Kind, p.OwnerId)));
        var set = document.Prefabs;
        if (set is not null && source.Prefabs is { } captured)
        {
            bool Affected(MapPrefabBindingTransfer t) => keys.Contains((t.Binding.Child.Kind, t.Binding.WorldId)) || state._transfers.Contains((t.InstanceId, t.Binding.Child));
            var instances = set.Instances.Select(i => i.Unpacked && captured.Instances.FirstOrDefault(c => c.Id == i.Id) is { } old
                ? i with { Bindings = new(RestoreItems(i.Bindings.Entries, old.Bindings.Entries, b => keys.Contains((b.Child.Kind, b.WorldId)))) } : i).ToArray();
            document.Prefabs = new(set.PayloadVersion, set.ExpansionVersion, instances, RestoreItems(set.Transfers, captured.Transfers, Affected))
            { Attachments = RestoreItems(set.Attachments, captured.Attachments, a => keys.Contains((MapPrefabChildKind.Placement, a.PlacementId))) };
        }
        foreach (var key in document.Surfaces.Patches.Keys.Where(k => keys.Contains((MapPrefabChildKind.Surface, k.SurfaceId))).ToArray())
            document.Surfaces.Patches.Remove(key);
        foreach (var (key, patch) in source.Surfaces.Patches)
            if (keys.Contains((MapPrefabChildKind.Surface, key.SurfaceId))) document.Surfaces.Patches[key] = patch;
        document.Surfaces.Refs.RemoveAll(s => keys.Contains((MapPrefabChildKind.Surface, s.Id)));
        foreach (var surface in source.Surfaces.Refs.Where(s => keys.Contains((MapPrefabChildKind.Surface, s.Id))))
            NativeDocumentSnapshot.PlaceRef(document.Surfaces.Refs, surface, source.Surfaces.Refs.Select(s => s.Id).ToArray());
        foreach (var patch in document.Surfaces.Patches.Values)
        {
            patch.Records.RemoveAll(r => keys.Contains((MapPrefabChildKind.Record, r.Id)));
            if (source.Surfaces.Patches.TryGetValue(patch.Key, out var saved))
                patch.Records.AddRange(saved.Records.Where(r => keys.Contains((MapPrefabChildKind.Record, r.Id))));
        }
        var intent = document.BuildingIntent;
        if (intent is not null && source.BuildingIntent is { } savedIntent)
            document.BuildingIntent = new(intent.PayloadVersion, intent.RoofLinks, intent.RoofGroups,
                RestoreItems(intent.Lights, savedIntent.Lights, l => keys.Contains((MapPrefabChildKind.Light, l.Id))), intent.Markers)
            { Roofs = intent.Roofs, Storeys = intent.Storeys };
        document.Foliage = RestoreFoliage(document.Foliage, source.Foliage, keys);
    }

    // Exception masks and owner correspondences that the restored owners own return to their captured rows. Every other
    // row, layer and density byte is the current one, and an untouched layer keeps its object.
    static MapFoliageSet? RestoreFoliage(MapFoliageSet? current, MapFoliageSet? saved, HashSet<(MapPrefabChildKind Kind, string Id)> keys)
    {
        if (current is null || !References(current, keys) && !References(saved, keys)) return current;
        bool Owned(MapPrefabReference owner) => owner.Scope == MapPrefabReferenceScope.World && keys.Contains((owner.Kind, owner.Id));
        var layers = current.Layers.Select(l =>
        {
            MapFoliageLayer? old = saved?.Layers.FirstOrDefault(s => string.Equals(s.Id, l.Id, StringComparison.Ordinal));
            var masks = RestoreItems(l.ExceptionMasks, old?.ExceptionMasks, m => Owned(m.Owner));
            var owners = RestoreItems(l.OwnerSupports, old?.OwnerSupports, o => Owned(o.Owner));
            if (masks.SequenceEqual(l.ExceptionMasks) && owners.SequenceEqual(l.OwnerSupports)) return l;
            var metadata = new MapFoliageLayer(l.PayloadVersion, l.Id, l.Support, l.OriginX, l.OriginZ, l.CellSize, l.Width, l.Depth,
                l.RowDirection, l.Seed, l.Spacing, l.ScaleMin, l.ScaleMax, l.RootOffset, l.Models, l.AllowedVisibleMaterials, l.Materials,
                l.Policy, l.ExcludeIndoors, l.ExcludeSolidObjects, l.DoorClearance, l.EdgeFade, l.CompatibilityOrdinals, l.ClearanceRules,
                masks, owners, l.Chunks);
            return MapFoliageLayer.FromStorage(metadata, l.ChunkDigests, l.Chunks);
        }).ToArray();
        return layers.SequenceEqual(current.Layers) ? current : new(current.PayloadVersion, layers);
    }

    // True when a mask or owner correspondence names one of the keys as its World owner.
    static bool References(MapFoliageSet? set, HashSet<(MapPrefabChildKind Kind, string Id)> keys) =>
        set?.Layers.Any(l => l.ExceptionMasks.Any(m => m.Owner.Scope == MapPrefabReferenceScope.World && keys.Contains((m.Owner.Kind, m.Owner.Id))) ||
            l.OwnerSupports.Any(o => o.Owner.Scope == MapPrefabReferenceScope.World && keys.Contains((o.Owner.Kind, o.Owner.Id)))) == true;

    static IReadOnlyList<T> RestoreItems<T>(IEnumerable<T>? current, IEnumerable<T>? saved, Func<T, bool> affected)
    {
        // Retain the captured order among restored entries, and the original positions among untouched entries.
        var result = (current ?? []).ToList();
        var order = (saved ?? []).ToArray();
        result.RemoveAll(v => affected(v));
        for (int i = 0; i < order.Length; i++)
            if (affected(order[i])) result.Insert(Math.Min(i, result.Count), order[i]);
        return result;
    }

    internal static MapNativeWriteSet ApplyPose(MapDocument document, string placementId, MapPlacementPoseFields fields)
    {
        var p = NativePlacementTransaction.Find(document, placementId);
        var entry = document.NativeResourceTransforms?.SingleOrDefault(t => t.Kind == MapPrefabChildKind.Placement && t.OwnerId == placementId);
        if (entry is null) return NativeCompleteSnapshotCommand.EmptyWrites;
        if (fields == MapPlacementPoseFields.None)
        {
            MapPrefabAttachmentDescriptors.Refresh(document);
            return Writes(document, Keys(document, placementId));
        }
        var pose = entry.Transform;
        if (fields.HasFlag(MapPlacementPoseFields.X)) pose = pose with { X = p.X };
        if (fields.HasFlag(MapPlacementPoseFields.Y) && p.Y is { } y) pose = pose with { Y = y };
        if (fields.HasFlag(MapPlacementPoseFields.Z)) pose = pose with { Z = p.Z };
        if (fields.HasFlag(MapPlacementPoseFields.Yaw)) pose = pose with { YawRadians = p.Yaw };
        if (fields.HasFlag(MapPlacementPoseFields.Scale)) pose = pose with { Scale = p.Scale };
        MapEffectiveTransform.Valid(pose);
        SetPose(document, placementId, entry.Transform, pose);
        return Writes(document, Keys(document, placementId));
    }

    internal static MapNativeWriteSet Reseat(MapDocument document, MapAssetClosure assets)
    {
        var missing = document.Placements.Where(p => p.Y is null).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        if (missing.Count == 0 || document.ResolverIdentity?.ResolverVersion != 2) return NativeCompleteSnapshotCommand.EmptyWrites;
        var effective = MapPrefabResolver.Expand(document, assets);
        var writes = NativeCompleteSnapshotCommand.EmptyWrites;
        foreach (string id in missing)
        {
            var entry = document.NativeResourceTransforms?.SingleOrDefault(t => t.Kind == MapPrefabChildKind.Placement && t.OwnerId == id);
            if (entry is null) continue;
            var seated = effective.Transforms[MapEffectiveContext.Key(MapPrefabChildKind.Placement, id)];
            if (entry.Transform.Y == seated.Y) continue;
            SetPose(document, id, entry.Transform, entry.Transform with { Y = seated.Y });
            writes = Union(writes, Writes(document, Keys(document, id)));
        }
        return writes;
    }

    static void SetPose(MapDocument document, string id, MapEffectiveTransform before, MapEffectiveTransform after)
    {
        var poses = document.NativeResourceTransforms!.ToDictionary(p => (p.Kind, p.OwnerId));
        poses[(MapPrefabChildKind.Placement, id)] = poses[(MapPrefabChildKind.Placement, id)] with { Transform = after };
        var attached = (document.Prefabs?.Attachments ?? []).Where(a => a.PlacementId == id && !a.Retired).SelectMany(a => a.Children).ToArray();
        foreach (var child in attached)
        {
            var key = (child.LocalChild.Kind, child.WorldId);
            poses[key] = poses[key] with { Transform = MapEffectiveTransform.Compose(after, child.OwnerLocalPose) };
        }
        // Explicit owned lights may have their own frame. Keep that frame's relative offset to the owner.
        foreach (var light in document.BuildingIntent?.Lights ?? [])
            if (light.Owner is { Kind: MapPrefabChildKind.Placement } owner && owner.Id == id && !attached.Any(c => c.LocalChild.Kind == MapPrefabChildKind.Light && c.WorldId == light.Id) &&
                poses.TryGetValue((MapPrefabChildKind.Light, light.Id), out var frame))
                poses[(MapPrefabChildKind.Light, light.Id)] = frame with
                { Transform = MapEffectiveTransform.Compose(after, MapPrefabEditPreparation.Relative(before, frame.Transform)) };
        document.NativeResourceTransforms = poses.Values.ToArray();
        MapPrefabAttachmentDescriptors.Refresh(document);
    }

    internal static MapNativeWriteSet Remove(MapDocument document, string id)
    {
        var keys = Keys(document, id);
        var writes = Writes(document, keys);
        document.Placements.RemoveAll(p => p.Id == id);
        document.NativeResourceTransforms = document.NativeResourceTransforms?.Where(p => !keys.Contains((p.Kind, p.OwnerId))).ToArray();
        if (document.Prefabs is { } set)
            document.Prefabs = new(set.PayloadVersion, set.ExpansionVersion, set.Instances,
                set.Transfers.Select(t => keys.Contains((t.Binding.Child.Kind, t.Binding.WorldId)) ? t with { Retired = true } : t).ToArray())
            { Attachments = set.Attachments.Select(a => a.PlacementId == id ? a with { Retired = true } : a).ToArray() };
        document.Surfaces.Refs.RemoveAll(s => keys.Contains((MapPrefabChildKind.Surface, s.Id)));
        foreach (var key in document.Surfaces.Patches.Keys.Where(k => keys.Contains((MapPrefabChildKind.Surface, k.SurfaceId))).ToArray())
            document.Surfaces.Patches.Remove(key);
        foreach (var patch in document.Surfaces.Patches.Values) patch.Records.RemoveAll(r => keys.Contains((MapPrefabChildKind.Record, r.Id)));
        if (document.BuildingIntent is { } intent)
            document.BuildingIntent = new(intent.PayloadVersion, intent.RoofLinks, intent.RoofGroups,
                intent.Lights.Where(l => !keys.Contains((MapPrefabChildKind.Light, l.Id))).ToArray(), intent.Markers)
            { Roofs = intent.Roofs, Storeys = intent.Storeys };
        return writes;
    }

    static HashSet<(MapPrefabChildKind Kind, string Id)> Keys(MapDocument document, string id)
    {
        var keys = new HashSet<(MapPrefabChildKind, string)> { (MapPrefabChildKind.Placement, id) };
        foreach (var row in (document.Prefabs?.Attachments ?? []).Where(a => a.PlacementId == id))
            foreach (var c in row.Children) keys.Add((c.LocalChild.Kind, c.WorldId));
        foreach (var light in document.BuildingIntent?.Lights ?? [])
            if (light.Owner is { Kind: MapPrefabChildKind.Placement } owner && owner.Id == id) keys.Add((MapPrefabChildKind.Light, light.Id));
        return keys;
    }
    static MapNativeWriteSet Writes(MapDocument document, HashSet<(MapPrefabChildKind Kind, string Id)> keys)
    {
        var surfaces = keys.Where(k => k.Kind == MapPrefabChildKind.Surface).Select(k => k.Id).ToArray();
        var records = keys.Where(k => k.Kind == MapPrefabChildKind.Record).Select(k => k.Id).ToArray();
        var patches = document.Surfaces.Patches.Values.Where(p => surfaces.Contains(p.Key.SurfaceId) || p.Records.Any(r => records.Contains(r.Id))).Select(p => p.Key).ToArray();
        return new(patches, surfaces, records, [], [], [], true)
        {
            Prefabs = document.Prefabs is not null,
            BuildingIntent = keys.Any(k => k.Kind == MapPrefabChildKind.Light),
            NativeResourceTransforms = document.NativeResourceTransforms is not null,
            NumericReservations = true
        };
    }
    internal static MapNativeWriteSet Union(MapNativeWriteSet a, MapNativeWriteSet b) => new(
        a.Patches.Union(b.Patches).Order().ToArray(), a.SurfaceIds.Union(b.SurfaceIds).Order(StringComparer.Ordinal).ToArray(),
        a.RecordIds.Union(b.RecordIds).Order(StringComparer.Ordinal).ToArray(), a.SpaceIds.Union(b.SpaceIds).Order(StringComparer.Ordinal).ToArray(),
        a.OwnerChanges.Union(b.OwnerChanges).ToArray(), a.Materials.Union(b.Materials).ToArray(), a.Placements || b.Placements)
    {
        Prefabs = a.Prefabs || b.Prefabs,
        BuildingIntent = a.BuildingIntent || b.BuildingIntent,
        NativeResourceTransforms = a.NativeResourceTransforms || b.NativeResourceTransforms,
        NumericReservations = a.NumericReservations || b.NumericReservations,
        Water = a.Water || b.Water,
        NativeAssets = a.NativeAssets || b.NativeAssets,
        Foliage = a.Foliage || b.Foliage,
    };
}
