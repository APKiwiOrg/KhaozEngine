using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>Plans existing corner owner changes against the projected coarse and fine vertex sets.</summary>
internal static class MapConversionOwners
{
    internal static IReadOnlyList<MapCornerOwnerChange> Plan(MapSurfaceSet before, MapSurfaceRef source, MapSurfaceRef fine,
        IReadOnlyList<MapConversionCellGeometry> cells, IReadOnlyList<MapSurfacePatch> finePatches, MapFinePatchRequest request)
    {
        MapCornerDependency[] dependencies = before.Patches.Values.SelectMany(p => p.CornerDependencies)
            .Where(d => d.Owner.Patch.SurfaceId == source.Id && d.Owner.Address.Denominator != 1).ToArray();
        if (dependencies.Length == 0) return Array.Empty<MapCornerOwnerChange>();

        // Projection copies model the removal and rim replacement without mutating the conversion candidate.
        var projected = new MapSurfaceSet();
        projected.Refs.Add(source);
        foreach (MapSurfacePatch patch in before.Patches.Values.Where(p => p.Key.SurfaceId == source.Id))
            projected.Patches.Add(patch.Key, patch.Clone());
        foreach (MapConversionCellGeometry cell in cells)
        {
            MapSurfacePatch patch = projected.Patches[cell.Patch.Key];
            patch.SetPresent(cell.X, cell.Z, false);
            patch.EdgeSubdivisions.RemoveAll(e => e.CellX == cell.X && e.CellZ == cell.Z);
        }
        foreach (var (_, edge, coarse, x, z) in MapConversionRim.Edges(projected, source.Id, cells))
            MapConversionRim.Subdivide(coarse, x, z, edge, request.Subdivision);

        var oldVertices = new Dictionary<MapPatchKey, MapBoundarySurface>();
        var retainedVertices = new Dictionary<MapPatchKey, MapBoundarySurface>();
        var fineVertices = new List<(MapPatchKey Key, MapBoundarySurface Geometry)>();
        var changes = new List<MapCornerOwnerChange>();
        foreach (MapSurfacePatch dependent in before.Patches.Values)
            foreach (MapCornerDependency dependency in dependent.CornerDependencies.OrderBy(d => d.CornerZ).ThenBy(d => d.CornerX))
            {
                MapVertexOwner owner = dependency.Owner;
                if (owner.Patch.SurfaceId != source.Id || owner.Address.Denominator == 1) continue;
                if (!retainedVertices.TryGetValue(owner.Patch, out MapBoundarySurface? retained))
                {
                    retained = new(source, projected.Patches[owner.Patch]);
                    retainedVertices.Add(owner.Patch, retained);
                }
                if (retained.Vertices.ContainsKey(owner.Address)) continue;
                if (!oldVertices.TryGetValue(owner.Patch, out MapBoundarySurface? old))
                {
                    old = new(source, before.Patches[owner.Patch]);
                    oldVertices.Add(owner.Patch, old);
                }
                MapExactPoint point = old.Vertices[owner.Address];
                MapLatticeAddress address = fine.Frame.AddressOf(point.X, point.Z);
                if (fineVertices.Count == 0)
                    foreach (MapSurfacePatch patch in finePatches)
                        fineVertices.Add((patch.Key, new(fine, patch)));
                MapVertexOwner? replacement = null;
                foreach (var (key, geometry) in fineVertices)
                    if (geometry.Vertices.TryGetValue(address, out MapExactPoint target) && target == point)
                    {
                        replacement = new(key, address);
                        break;
                    }
                if (replacement is null)
                    throw new MapDocumentException($"owner retarget: dependent patch '{dependent.Key}' corner " +
                        $"({dependency.CornerX}, {dependency.CornerZ}) owner '{owner.Patch}' address " +
                        $"({owner.Address.X}/{owner.Address.Denominator}, {owner.Address.Z}/{owner.Address.Denominator}) " +
                        "has no fine vertex at the same exact point and height");
                changes.Add(new(dependent.Key, dependency.CornerX, dependency.CornerZ, owner, replacement));
            }
        return changes.AsReadOnly();
    }
}
