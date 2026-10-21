using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>Stored poses of standalone placements and the named roots that move with them, shared by the document-aware
/// terrain transaction and the editor's placement transactions.</summary>
internal static class MapPlacementSeats
{
    internal static MapNativeWriteSet Empty { get; } = new([], [], [], [], [], [], false);

    /// <summary>Re-seats every Y-null resolver-2 placement whose stored pose no longer matches its seated height, with its
    /// attached children and owned lights, and names the roots it rewrote.</summary>
    internal static MapNativeWriteSet Reseat(MapDocument document, MapAssetClosure assets)
    {
        var missing = document.Placements.Where(p => p.Y is null).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        if (missing.Count == 0 || document.ResolverIdentity?.ResolverVersion != 2) return Empty;
        var effective = MapDocumentEditFinalizer.Expand(document, assets);
        var writes = Empty;
        foreach (string id in missing)
        {
            var entry = document.NativeResourceTransforms?.SingleOrDefault(t => t.Kind == MapPrefabChildKind.Placement && t.OwnerId == id);
            if (entry is null) continue;
            var seated = effective.Transforms[MapEffectiveContext.Key(MapPrefabChildKind.Placement, id)];
            if (entry.Transform.Y == seated.Y) continue;
            SetPose(document, id, entry.Transform, entry.Transform with { Y = seated.Y });
            writes = MapNativeWriteSet.Union(writes, Writes(document, Keys(document, id)));
        }
        return writes;
    }

    internal static void SetPose(MapDocument document, string id, MapEffectiveTransform before, MapEffectiveTransform after)
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

    internal static HashSet<(MapPrefabChildKind Kind, string Id)> Keys(MapDocument document, string id)
    {
        var keys = new HashSet<(MapPrefabChildKind, string)> { (MapPrefabChildKind.Placement, id) };
        foreach (var row in (document.Prefabs?.Attachments ?? []).Where(a => a.PlacementId == id))
            foreach (var c in row.Children) keys.Add((c.LocalChild.Kind, c.WorldId));
        foreach (var light in document.BuildingIntent?.Lights ?? [])
            if (light.Owner is { Kind: MapPrefabChildKind.Placement } owner && owner.Id == id) keys.Add((MapPrefabChildKind.Light, light.Id));
        return keys;
    }

    internal static MapNativeWriteSet Writes(MapDocument document, HashSet<(MapPrefabChildKind Kind, string Id)> keys)
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
}
