using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>Canonical deltas determine publication and consequences from the final composite candidate.</summary>
internal static class MapTerrainEditChanges
{
    internal static MapTerrainEditResult Describe(MapSurfaceSet before, MapSurfaceSet after, MapNativeInvalidation invalidates) =>
        Describe(before, after, invalidates, includeBounds: true, out _);

    internal static MapTerrainEditResult Describe(MapSurfaceSet before, MapSurfaceSet after, MapNativeInvalidation invalidates,
        bool includeBounds, out (MapTerrainPatchEffects Before, MapTerrainPatchEffects After) geometry)
    {
        var oldDigests = before.Patches.ToDictionary(p => p.Key, p => MapSurfaceSemantics.PatchDigest(p.Value));
        var newDigests = after.Patches.ToDictionary(p => p.Key, p => MapSurfaceSemantics.PatchDigest(p.Value));
        MapPatchKey[] patchWrites = oldDigests.Keys.Union(newDigests.Keys).Order()
            .Where(key => oldDigests.GetValueOrDefault(key) != newDigests.GetValueOrDefault(key)).ToArray();
        var affectedSurfaces = patchWrites.Select(p => p.SurfaceId).ToHashSet(StringComparer.Ordinal);
        var oldRefs = before.Refs.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (MapSurfaceRef surface in after.Refs)
            if (!oldRefs.TryGetValue(surface.Id, out MapSurfaceRef? old) || Metadata(old) != Metadata(surface))
                affectedSurfaces.Add(surface.Id);
        foreach (string id in oldRefs.Keys.Except(after.Refs.Select(s => s.Id), StringComparer.Ordinal)) affectedSurfaces.Add(id);
        for (int i = 0; i < after.Refs.Count; i++)
            if (affectedSurfaces.Contains(after.Refs[i].Id))
            {
                MapSurfaceRef surface = after.Refs[i];
                after.Refs[i] = surface with
                {
                    SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, newDigests.Where(p => p.Key.SurfaceId == surface.Id)),
                };
            }
        var newRefs = after.Refs.ToDictionary(s => s.Id, StringComparer.Ordinal);
        string[] surfaces = oldRefs.Keys.Union(newRefs.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(id => !oldRefs.TryGetValue(id, out MapSurfaceRef? old) || !newRefs.TryGetValue(id, out MapSurfaceRef? current) ||
                old.SemanticSha256 != current.SemanticSha256 || Metadata(old) != Metadata(current)).ToArray();
        var changes = patchWrites.Select(key => new MapDigestChange($"patch/{Key(key)}",
            oldDigests.GetValueOrDefault(key), newDigests.GetValueOrDefault(key))).ToList();
        foreach (string id in surfaces)
            changes.Add(new($"surface/{id}", Digest(before, id, oldDigests), Digest(after, id, newDigests)));

        Dictionary<string, string> oldRecords = Records(before), newRecords = Records(after);
        string[] records = oldRecords.Keys.Union(newRecords.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(id => oldRecords.GetValueOrDefault(id) != newRecords.GetValueOrDefault(id)).ToArray();
        foreach (string id in records)
            changes.Add(new($"record/{id}", oldRecords.GetValueOrDefault(id), newRecords.GetValueOrDefault(id)));
        var owners = new List<MapCornerOwnerChange>();
        foreach (MapPatchKey key in patchWrites)
            if (after.Patches.TryGetValue(key, out MapSurfacePatch? patch))
            {
                before.Patches.TryGetValue(key, out MapSurfacePatch? old);
                var coordinates = patch.CornerDependencies.Select(d => (d.CornerX, d.CornerZ))
                    .Union(old?.CornerDependencies.Select(d => (d.CornerX, d.CornerZ)) ?? Array.Empty<(int, int)>())
                    .OrderBy(c => c.CornerZ).ThenBy(c => c.CornerX);
                foreach (var (x, z) in coordinates)
                {
                    if (x > patch.Width || z > patch.Depth) continue;
                    MapVertexOwner? previous = old?.CornerDependencies.Find(d => d.CornerX == x && d.CornerZ == z)?.Owner;
                    MapVertexOwner? current = patch.CornerDependencies.Find(d => d.CornerX == x && d.CornerZ == z)?.Owner;
                    if (previous != current) owners.Add(new(key, x, z, previous, current ?? new(key, patch.CornerAddress(x, z))));
                }
            }

        var dependencies = new SortedSet<string>(records, StringComparer.Ordinal);
        var spaces = new SortedSet<string>(StringComparer.Ordinal);
        var materials = new SortedSet<ushort>();
        Collect(before);
        Collect(after);
        var writeSet = new MapNativeWriteSet(Array.AsReadOnly(patchWrites), Array.AsReadOnly(surfaces), Array.AsReadOnly(records),
            Array.AsReadOnly(spaces.ToArray()), owners.AsReadOnly(), Array.AsReadOnly(materials.ToArray()), false);

        geometry = MapTerrainPatchEffects.Collect(before, after, oldRefs, writeSet.Patches);
        var (oldGeometry, newGeometry) = geometry;
        var effectPatches = Array.AsReadOnly(oldGeometry.Patches.Union(newGeometry.Patches).Order().ToArray());
        var effects = new MapNativeEditEffects(includeBounds ? oldGeometry.Bounds(before) : null,
            includeBounds ? newGeometry.Bounds(after) : null, effectPatches,
            writeSet.SpaceIds, Array.AsReadOnly(dependencies.ToArray()), changes.AsReadOnly(), invalidates);
        return new(after, writeSet, effects);

        void Collect(MapSurfaceSet set)
        {
            foreach (MapPatchKey key in patchWrites)
                if (set.Patches.TryGetValue(key, out MapSurfacePatch? patch))
                {
                    foreach (MapCornerDependency dependency in patch.CornerDependencies) dependencies.Add(Key(dependency.Owner.Patch));
                    foreach (MapTopologyRecord record in patch.Records)
                    {
                        dependencies.Add(record.Id);
                        foreach (MapRecordRef reference in MapSurfaceReferences.Records(record)) dependencies.Add(reference.Id);
                    }
                    spaces.UnionWith(MapSurfaceReferences.Spaces(set, patch, Array.Empty<MapRecordRef>()));
                    foreach (MapSurfaceCell cell in patch.Cells) { Material(cell.Underlay); Material(cell.Overlay); }
                    foreach (MapWallStrip strip in patch.Records.OfType<MapWallStrip>()) Material(strip.MaterialId);
                }
            MapTopologyRecord[] all = set.AllRecords().ToArray();
            foreach (MapSpaceFootprint footprint in all.OfType<MapSpaceFootprint>())
                if (affectedSurfaces.Contains(footprint.Lower.SurfaceId ?? "") || affectedSurfaces.Contains(footprint.Upper.SurfaceId ?? "") ||
                    patchWrites.Contains(footprint.Lattice))
                {
                    spaces.Add(footprint.Space.Id);
                    dependencies.Add(footprint.Id);
                }
            // Referrers can live outside the changed payloads. Walk to a fixed point without assuming record order.
            bool expanded;
            do
            {
                expanded = false;
                foreach (MapTopologyRecord record in all)
                    if (MapSurfaceReferences.Records(record).Any(r => dependencies.Contains(r.Id)))
                    {
                        expanded |= dependencies.Add(record.Id);
                        if (record is MapSpaceDoc space) spaces.Add(space.Id);
                    }
            } while (expanded);
            foreach (MapSurfaceRef surface in set.Refs.Where(s => affectedSurfaces.Contains(s.Id)))
                if (surface.IndoorSpan is { } span) spaces.Add(span.ParentSpace.Id);
        }

        void Material(ushort id) { if (id != 0) materials.Add(id); }
    }

    static string Key(MapPatchKey key) => FormattableString.Invariant($"{key.SurfaceId}/{key.SlotX}/{key.SlotZ}");
    static string Metadata(MapSurfaceRef surface) => MapSurfaceSemantics.SurfaceDigest(surface,
        Array.Empty<KeyValuePair<MapPatchKey, string>>());
    static string? Digest(MapSurfaceSet set, string id, Dictionary<MapPatchKey, string> digests) =>
        set.Refs.Find(s => s.Id == id) is { } surface
            ? MapSurfaceSemantics.SurfaceDigest(surface, digests.Where(p => p.Key.SurfaceId == id)) : null;

    static Dictionary<string, string> Records(MapSurfaceSet set)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (MapSurfacePatch patch in set.Patches.Values)
            foreach (MapTopologyRecord record in patch.Records)
                result.Add(record.Id, MapSurfaceSemantics.RecordDigest(patch.Key, record));
        return result;
    }
}
