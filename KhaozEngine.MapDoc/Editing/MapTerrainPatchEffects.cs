using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>Includes patches whose compiled geometry can change through a surface declaration edit.</summary>
internal static class MapTerrainPatchEffects
{
    internal static IReadOnlyList<MapPatchKey> Collect(MapSurfaceSet before, MapSurfaceSet after,
        IReadOnlyDictionary<string, MapSurfaceRef> oldRefs, IReadOnlyList<MapPatchKey> patchWrites)
    {
        var changedGeometry = new HashSet<string>(StringComparer.Ordinal);
        var changedFrames = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapSurfaceRef surface in after.Refs)
        {
            if (!oldRefs.TryGetValue(surface.Id, out MapSurfaceRef? old)) continue;
            bool frameChanged = old.Frame != surface.Frame;
            if (frameChanged) changedFrames.Add(surface.Id);
            if (frameChanged || old.Role != surface.Role || old.PresencePolicy != surface.PresencePolicy)
                changedGeometry.Add(surface.Id);
        }
        if (changedGeometry.Count == 0) return patchWrites;

        var affected = new SortedSet<MapPatchKey>(patchWrites);
        Include(before);
        Include(after);
        return Array.AsReadOnly(affected.ToArray());

        void Include(MapSurfaceSet set)
        {
            affected.UnionWith(set.Patches.Keys.Where(key => changedGeometry.Contains(key.SurfaceId)));
            if (changedFrames.Count == 0) return;

            // A strip can read either chain from another patch, and a chain can use another surface's frame.
            var chains = set.Patches.Values.SelectMany(patch => patch.Records.OfType<MapBoundaryChain>()
                .Where(chain => chain.Vertices.Any(vertex => changedFrames.Contains(vertex.Vertex.SurfaceId)))
                .Select(chain => new MapRecordRef(chain.Id, patch.Key))).ToHashSet();
            if (chains.Count == 0) return;
            foreach (MapSurfacePatch patch in set.Patches.Values)
                if (patch.Records.OfType<MapWallStrip>().Any(strip => chains.Contains(strip.LowerChain) || chains.Contains(strip.UpperChain)))
                    affected.Add(patch.Key);
        }
    }
}
