using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Identity;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Implicit support children of prefab leaves. A consuming placement's support resource yields one Surface
/// child per resource surface, keyed <c>placement/resource/surface</c>, and one Record child per embedded record,
/// keyed <c>placement/resource/record</c> and anchored on its remapped surface key. Patches never keep embedded
/// records. Local resource references become those keys, World references stay exact.</summary>
internal static class MapEffectiveSupport
{
    internal static IReadOnlyList<MapPrefabChild> Enumerate(MapAssetClosure assets, IReadOnlyList<MapPrefabChild> children,
        IReadOnlyList<MapPrefabLocalMaterialization>? materialized = null, bool allowOpaque = false)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(children);
        var suppressed = Suppressed(materialized, MapPrefabAttachmentKind.Support);
        var result = new List<MapPrefabChild>();
        foreach (MapPrefabPlacementChild placement in Placements(children))
            foreach (string resourceId in assets.GetAsset(placement.Value.AssetId!).SupportResourceIds)
            {
                if (suppressed.Contains((placement.Key.Key, resourceId))) continue;
                if (allowOpaque && !assets.HasSupport(resourceId)) continue;
                MapSupportResource resource = assets.BorrowSupport(resourceId);
                result.AddRange(Children(placement.Key.Key, resourceId, resource));
            }
        return result;
    }

    internal static MapSurfaceSet Install(MapAssetClosure assets, IReadOnlyList<MapPrefabChild> children,
        IReadOnlyDictionary<string, MapEffectiveTransform> transforms)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(transforms);
        var result = new MapSurfaceSet();
        foreach (var surface in children.OfType<MapPrefabSurfaceChild>())
        {
            if (result.Refs.Any(s => s.Id == surface.Surface.Id)) throw new MapDocumentException("Duplicate effective support surface.");
            result.Refs.Add(surface.Surface);
            foreach (var patch in surface.Patches) result.Patches.Add(patch.Key, patch.Clone());
        }
        foreach (var record in children.OfType<MapPrefabRecordChild>())
        {
            if (!result.Patches.TryGetValue(record.Anchor, out var patch)) throw new MapDocumentException("Missing effective record anchor.");
            if (result.AllRecords().Any(r => r.Id == record.Value.Id)) throw new MapDocumentException("Duplicate effective record.");
            patch.Records.Add(record.Value);
        }
        for (int i = 0; i < result.Refs.Count; i++)
        {
            var surface = result.Refs[i];
            result.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, result.Patches
                .Where(p => p.Key.SurfaceId == surface.Id).Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
            };
        }
        return result;
    }

    internal static string Key(string placementKey, string resourceId, string localId) => placementKey + "/" + resourceId + "/" + localId;

    internal static IEnumerable<MapPrefabPlacementChild> Placements(IReadOnlyList<MapPrefabChild> children) =>
        children.OfType<MapPrefabPlacementChild>().OrderBy(c => c.Key.Key, StringComparer.Ordinal);

    internal static HashSet<(string Placement, string Resource)> Suppressed(IReadOnlyList<MapPrefabLocalMaterialization>? rows, MapPrefabAttachmentKind kind) =>
        (rows ?? []).Where(r => r.Kind == kind).Select(r => (r.Placement.Id, r.ResourceId)).ToHashSet();

    // Each child is walked from its resource parts. Its pointers translate to the resource pointers, so every
    // occurrence takes the scope of its own resource slot. The record anchor is the resource patch key slot.
    internal static IEnumerable<MapPrefabChild> Children(string placementKey, string resourceId, MapSupportResource resource)
    {
        MapSurfaceRef[] surfaces = resource.Surfaces.Refs.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        MapSurfacePatch[] patches = resource.Surfaces.Patches.Values.OrderBy(p => p.Key).ToArray();
        for (int j = 0; j < surfaces.Length; j++)
        {
            int[] owned = Enumerable.Range(0, patches.Length).Where(i => patches[i].Key.SurfaceId == surfaces[j].Id).ToArray();
            var local = new MapPrefabSurfaceChild(new(MapPrefabChildKind.Surface, surfaces[j].Id), surfaces[j], owned.Select(i =>
            {
                MapSurfacePatch copy = patches[i].Clone();
                copy.Records.Clear();
                return copy;
            }).ToArray());
            string surface = "/surfaces/" + j;
            yield return Split(local, placementKey, resourceId, resource.References, path => path.StartsWith("/surface/", StringComparison.Ordinal)
                ? surface + path["/surface".Length..]
                : Patch(path, owned));
        }
        for (int i = 0; i < patches.Length; i++)
        {
            MapTopologyRecord[] records = patches[i].Records.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
            for (int k = 0; k < records.Length; k++)
            {
                var local = new MapPrefabRecordChild(new(MapPrefabChildKind.Record, records[k].Id), records[k], patches[i].Key);
                string patch = "/patches/" + i, record = patch + "/records/" + k;
                yield return Split(local, placementKey, resourceId, resource.References, path => path == "/anchor/surfaceId"
                    ? patch + "/key/surfaceId"
                    : record + path["/value".Length..]);
            }
        }
    }

    // A child patch pointer /patches/{n}/rest names the resource patch owned[n].
    static string Patch(string path, int[] owned)
    {
        int start = "/patches/".Length, end = path.IndexOf('/', start);
        int n = int.Parse(path.AsSpan(start, (end < 0 ? path.Length : end) - start), System.Globalization.CultureInfo.InvariantCulture);
        return "/patches/" + owned[n] + (end < 0 ? "" : path[end..]);
    }

    static MapPrefabChild Split(MapPrefabChild local, string placementKey, string resourceId, IReadOnlyList<MapPrefabReferenceSlot> slots,
        Func<string, string> resourcePath)
    {
        var (walked, occurrences) = MapPrefabReferenceWalker.Child(local, new ToLocalKeys(slots, placementKey, resourceId, resourcePath));
        string key = Key(placementKey, resourceId, local.Key.Key);
        // Definition IDs are not occurrences, so each child takes its compound key here.
        MapPrefabChild defined = walked switch
        {
            MapPrefabSurfaceChild s => s with { Key = new(MapPrefabChildKind.Surface, key), Surface = s.Surface with { Id = key } },
            MapPrefabRecordChild r => r with { Key = new(MapPrefabChildKind.Record, key), Value = r.Value with { Id = key } },
            _ => throw new MapDocumentException("Support resources produce only Surface and Record children."),
        };
        return defined with { References = MapPrefabReferences.Slots(occurrences) };
    }

    sealed class ToLocalKeys(IReadOnlyList<MapPrefabReferenceSlot> slots, string placementKey, string resourceId, Func<string, string> resourcePath)
        : MapPrefabReferenceMap
    {
        readonly Dictionary<string, MapPrefabReferenceSlot> _slots = slots.ToDictionary(s => s.Path, StringComparer.Ordinal);

        internal override (string Id, MapPrefabReferenceScope? Scope) Text(string path, MapPrefabChildKind kind, string id, bool space)
        {
            if (!_slots.TryGetValue(resourcePath(path), out MapPrefabReferenceSlot? slot))
                throw new MapDocumentException($"Support resource '{resourceId}' has no slot for '{resourcePath(path)}'.");
            MapPrefabReferenceScope scope = slot.Target.Scope;
            return scope == MapPrefabReferenceScope.Local && id != MapPrefabReferences.Parent ? (Key(placementKey, resourceId, id), scope) : (id, scope);
        }
    }
}
