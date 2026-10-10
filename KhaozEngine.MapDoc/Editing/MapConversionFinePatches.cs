using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>Assembles exact fine cells into the fixed 64-cell slots, retaining holes between regions.</summary>
internal static class MapConversionFinePatches
{
    internal static IReadOnlyList<MapSurfacePatch> Build(IReadOnlyList<MapConversionCellGeometry> cells,
        IReadOnlyList<MapCellConversion> classifications, MapFinePatchRequest request)
    {
        int k = request.Subdivision;
        var extents = new SortedDictionary<MapPatchKey, (int MinX, int MinZ, int MaxX, int MaxZ)>();
        foreach (MapConversionCellGeometry cell in cells)
        {
            long startX = checked(cell.CellX * k), startZ = checked(cell.CellZ * k);
            for (int z = 0; z < k; z++)
                for (int x = 0; x < k; x++)
                {
                    MapPatchKey key = MapPatchKey.ForCell(request.FineSurfaceId, checked(startX + x), checked(startZ + z));
                    int localX = checked((int)(startX + x - key.SlotX * 64));
                    int localZ = checked((int)(startZ + z - key.SlotZ * 64));
                    extents[key] = extents.TryGetValue(key, out var old)
                        ? (Math.Min(old.MinX, localX), Math.Min(old.MinZ, localZ), Math.Max(old.MaxX, localX + 1), Math.Max(old.MaxZ, localZ + 1))
                        : (localX, localZ, localX + 1, localZ + 1);
                }
        }
        var patches = new SortedDictionary<MapPatchKey, MapSurfacePatch>();
        var assigned = new Dictionary<MapPatchKey, bool[]>();
        foreach (var row in extents)
        {
            int width = row.Value.MaxX - row.Value.MinX, depth = row.Value.MaxZ - row.Value.MinZ;
            var patch = new MapSurfacePatch
            {
                Key = row.Key,
                CellMinX = row.Value.MinX,
                CellMinZ = row.Value.MinZ,
                Width = width,
                Depth = depth,
                Heights = new int[(width + 1) * (depth + 1)],
                Cells = new MapSurfaceCell[width * depth],
                Presence = new ulong[(width * depth + 63) / 64],
            };
            patches.Add(row.Key, patch);
            assigned.Add(row.Key, new bool[patch.Heights.Length]);
        }
        for (int i = 0; i < cells.Count; i++)
        {
            MapConversionCellGeometry source = cells[i];
            long startX = checked(source.CellX * k), startZ = checked(source.CellZ * k);
            var heights = new int[(k + 1) * (k + 1)];
            for (int z = 0; z <= k; z++)
                for (int x = 0; x <= k; x++) heights[z * (k + 1) + x] = source.FineHeight(x, z, k);
            for (int z = 0; z < k; z++)
                for (int x = 0; x < k; x++)
                {
                    MapPatchKey key = MapPatchKey.ForCell(request.FineSurfaceId, checked(startX + x), checked(startZ + z));
                    MapSurfacePatch patch = patches[key];
                    int px = checked((int)(startX + x - key.SlotX * 64 - patch.CellMinX));
                    int pz = checked((int)(startZ + z - key.SlotZ * 64 - patch.CellMinZ));
                    patch.Cells[pz * patch.Width + px] = source.FineCell(x, z, k, classifications[i].Class);
                    patch.SetPresent(px, pz, true);
                    for (int dz = 0; dz <= 1; dz++)
                        for (int dx = 0; dx <= 1; dx++)
                        {
                            int index = (pz + dz) * (patch.Width + 1) + px + dx;
                            int height = heights[(z + dz) * (k + 1) + x + dx];
                            if (assigned[key][index] && patch.Heights[index] != height)
                                throw new MapDocumentException("shared fine corner height is not representable");
                            patch.Heights[index] = height;
                            assigned[key][index] = true;
                        }
                }
        }
        return Array.AsReadOnly(patches.Values.ToArray());
    }
}
