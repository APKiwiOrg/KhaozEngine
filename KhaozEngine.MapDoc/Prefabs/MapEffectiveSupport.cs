using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Implicit support children of prefab leaves. A consuming placement's support resource yields one Surface
/// child per resource surface, keyed <c>placement/resource/surface</c>, and one Record child per embedded record,
/// keyed <c>placement/resource/record</c> and anchored on its remapped surface key. Patches never keep embedded
/// records. Local resource references become those keys, World references stay exact.</summary>
internal static class MapEffectiveSupport
{
    internal static IReadOnlyList<MapPrefabChild> Enumerate(MapAssetClosure assets, IReadOnlyList<MapPrefabChild> children,
        IReadOnlyList<MapPrefabLocalMaterialization>? materialized = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(children);
        var suppressed = Suppressed(materialized, MapPrefabAttachmentKind.Support);
        var result = new List<MapPrefabChild>();
        foreach (MapPrefabPlacementChild placement in Placements(children))
            foreach (string resourceId in assets.GetAsset(placement.Value.AssetId!).SupportResourceIds)
            {
                if (suppressed.Contains((placement.Key.Key, resourceId))) continue;
                MapSupportResource resource = MapSupportResourceFile.Read(assets.GetResource(resourceId).Bytes, resourceId);
                result.AddRange(Children(placement.Key.Key, resourceId, resource));
            }
        return result;
    }

    internal static string Key(string placementKey, string resourceId, string localId) => placementKey + "/" + resourceId + "/" + localId;

    internal static IEnumerable<MapPrefabPlacementChild> Placements(IReadOnlyList<MapPrefabChild> children) =>
        children.OfType<MapPrefabPlacementChild>().OrderBy(c => c.Key.Key, StringComparer.Ordinal);

    internal static HashSet<(string Placement, string Resource)> Suppressed(IReadOnlyList<MapPrefabLocalMaterialization>? rows, MapPrefabAttachmentKind kind) =>
        (rows ?? []).Where(r => r.Kind == kind).Select(r => (r.Placement.Id, r.ResourceId)).ToHashSet();

    static IEnumerable<MapPrefabChild> Children(string placementKey, string resourceId, MapSupportResource resource)
    {
        var produced = new HashSet<(MapPrefabChildKind, string)>();
        foreach (MapSurfaceRef surface in resource.Surfaces.Refs) produced.Add((MapPrefabChildKind.Surface, Key(placementKey, resourceId, surface.Id)));
        foreach (MapTopologyRecord record in resource.Surfaces.Patches.Values.SelectMany(p => p.Records))
            produced.Add((MapPrefabChildKind.Record, Key(placementKey, resourceId, record.Id)));
        var (mapped, occurrences) = MapPrefabReferenceWalker.Resource(resource, new ToLocalKeys(resource.References, placementKey, resourceId));
        // Scope follows the slot, never the text. A World reference spelling a produced key would be ambiguous here.
        foreach (MapPrefabOccurrence occurrence in occurrences)
            if (occurrence.Scope == MapPrefabReferenceScope.World && produced.Contains((occurrence.Kind, occurrence.Id)))
                throw new MapDocumentException($"Support resource '{resourceId}' has a World reference that spells local key '{occurrence.Id}'.");
        MapPrefabChild Scoped(MapPrefabChild child)
        {
            var (_, found) = MapPrefabReferenceWalker.Child(child, MapPrefabReferenceMap.Identity);
            return child with
            {
                References = MapPrefabReferences.Slots(found.Select(o => o with
                {
                    Scope = o.Id == MapPrefabReferences.Parent || produced.Contains((o.Kind, o.Id)) ? MapPrefabReferenceScope.Local : MapPrefabReferenceScope.World,
                })),
            };
        }
        // Definition IDs are not occurrences, so each child takes its compound key here. Patch keys are Local slots.
        foreach (MapSurfaceRef surface in mapped.Surfaces.Refs.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            string key = Key(placementKey, resourceId, surface.Id);
            var patches = mapped.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == key).Select(p =>
            {
                MapSurfacePatch copy = p.Clone();
                copy.Records.Clear();
                return copy;
            }).ToArray();
            yield return Scoped(new MapPrefabSurfaceChild(new(MapPrefabChildKind.Surface, key), surface with { Id = key }, patches));
        }
        foreach (MapSurfacePatch patch in mapped.Surfaces.Patches.Values)
            foreach (MapTopologyRecord record in patch.Records.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                string key = Key(placementKey, resourceId, record.Id);
                yield return Scoped(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, key), record with { Id = key }, patch.Key));
            }
    }

    sealed class ToLocalKeys(IReadOnlyList<MapPrefabReferenceSlot> slots, string placementKey, string resourceId) : MapPrefabReferenceMap
    {
        readonly Dictionary<string, MapPrefabReferenceSlot> _slots = slots.ToDictionary(s => s.Path, StringComparer.Ordinal);

        internal override (string Id, MapPrefabReferenceScope? Scope) Text(string path, MapPrefabChildKind kind, string id, bool space)
        {
            MapPrefabReferenceScope scope = _slots[path].Target.Scope;
            return scope == MapPrefabReferenceScope.Local && id != MapPrefabReferences.Parent ? (Key(placementKey, resourceId, id), scope) : (id, scope);
        }
    }
}
