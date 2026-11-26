using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>The parts of one snapshot that contribute to terrain edit bounds.</summary>
internal sealed class MapTerrainPatchEffects(IReadOnlyList<MapPatchKey> surfacePatches,
    IReadOnlyList<MapPatchKey> patchWrites, IReadOnlyList<(MapPatchKey Anchor, MapWallStrip Strip)> walls)
{
    internal IEnumerable<MapPatchKey> Patches => surfacePatches.Concat(walls.Select(wall => wall.Anchor));

    internal static (MapTerrainPatchEffects Before, MapTerrainPatchEffects After) Collect(MapSurfaceSet before, MapSurfaceSet after,
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
        if (changedGeometry.Count == 0)
        {
            var written = new MapTerrainPatchEffects(patchWrites, patchWrites, []);
            return (written, written);
        }
        return (Select(before), Select(after));

        MapTerrainPatchEffects Select(MapSurfaceSet set)
        {
            var surfaces = patchWrites.Union(set.Patches.Keys.Where(key => changedGeometry.Contains(key.SurfaceId))).Order().ToArray();
            if (changedFrames.Count == 0) return new(surfaces, patchWrites, []);

            // A strip can read either chain from another patch, and a chain can use another surface's frame.
            var chains = set.Patches.Values.SelectMany(patch => patch.Records.OfType<MapBoundaryChain>()
                .Where(chain => chain.Vertices.Any(vertex => changedFrames.Contains(vertex.Vertex.SurfaceId)))
                .Select(chain => new MapRecordRef(chain.Id, patch.Key))).ToHashSet();
            if (chains.Count == 0) return new(surfaces, patchWrites, []);
            var strips = set.Patches.Values.OrderBy(patch => patch.Key).SelectMany(patch => patch.Records.OfType<MapWallStrip>()
                .Where(strip => chains.Contains(strip.LowerChain) || chains.Contains(strip.UpperChain))
                .Select(strip => (patch.Key, strip))).ToArray();
            return new(surfaces, patchWrites, strips);
        }
    }

    internal MapBox3? Bounds(MapSurfaceSet set, IReadOnlyList<MapStandaloneResourceTransform>? transforms = null)
    {
        var poses = transforms?.Where(pose => pose.Kind == MapPrefabChildKind.Surface)
            .ToDictionary(pose => pose.OwnerId, pose => new MapResourceTransform(pose.Transform), StringComparer.Ordinal);
        MapBox3? bounds = null;
        MapScopedSurfaces? view = null;
        var visited = new HashSet<MapRecordRef>();
        foreach (MapPatchKey key in surfacePatches)
            if (set.Patches.TryGetValue(key, out MapSurfacePatch? patch))
            {
                MapSurfaceRef surface = set.Refs.Single(s => s.Id == key.SurfaceId);
                foreach (MapExactPoint point in new MapBoundarySurface(surface, patch).Vertices.Values)
                    Add(WorldPoint(key.SurfaceId, point));
            }
        foreach (MapPatchKey key in patchWrites)
            if (set.Patches.TryGetValue(key, out MapSurfacePatch? patch))
            {
                foreach (MapBoundaryChain chain in patch.Records.OfType<MapBoundaryChain>()) IncludeChain(new(chain.Id, key));
                foreach (MapWallStrip strip in patch.Records.OfType<MapWallStrip>()) IncludeStrip(strip);
            }
        foreach (var (_, strip) in walls) IncludeStrip(strip);
        return bounds;

        MapExactPoint WorldPoint(string surfaceId, MapExactPoint point) =>
            poses is not null && poses.TryGetValue(surfaceId, out MapResourceTransform? pose) ? pose.Point(point) : point;

        void IncludeStrip(MapWallStrip strip)
        {
            IncludeChain(strip.LowerChain);
            IncludeChain(strip.UpperChain);
        }

        void IncludeChain(MapRecordRef reference)
        {
            if (!visited.Add(reference) || !set.TryGetRecord(reference, out MapTopologyRecord? record) || record is not MapBoundaryChain chain)
                return;
            view ??= MapScopedSurfaces.CompleteView(set);
            MapChainResolution resolution = MapBoundaryGeometry.ResolveChain(chain, view, null, WorldPoint);
            if (resolution.Status != MapResolveStatus.Resolved) throw resolution.Refuse(resolution.Detail!, reference);
            foreach (MapExactPoint point in resolution.Points) Add(point);
        }

        void Add(MapExactPoint point)
        {
            double x = point.X.ToDouble(), y = point.Y.ToDouble(), z = point.Z.ToDouble();
            var position = new MapBox3(x, y, z, x, y, z);
            bounds = bounds?.Union(position) ?? position;
        }
    }
}
