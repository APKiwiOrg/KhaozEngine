using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>Joins coarse rim edges to the fine slot boundaries using the shared boundary records.</summary>
internal static class MapConversionRim
{
    internal static IReadOnlyList<(long X, long Z)> Apply(MapSurfaceSet candidate, MapSurfaceRef source,
        IReadOnlyList<MapConversionCellGeometry> cells, MapFinePatchRequest request,
        ISet<MapPatchKey> writes, ISet<string> records, IList<MapCornerOwnerChange> owners)
    {
        var changed = new HashSet<(long X, long Z)>();
        var ids = candidate.AllRecords().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        int k = request.Subdivision;
        foreach (MapConversionCellGeometry cell in cells)
            foreach (MapCellEdge edge in Enum.GetValues<MapCellEdge>())
            {
                (long nx, long nz) = edge switch
                {
                    MapCellEdge.South => (cell.CellX, checked(cell.CellZ - 1)),
                    MapCellEdge.East => (checked(cell.CellX + 1), cell.CellZ),
                    MapCellEdge.North => (cell.CellX, checked(cell.CellZ + 1)),
                    _ => (checked(cell.CellX - 1), cell.CellZ),
                };
                if (!MapFinePatchConversion.TryCell(candidate, source.Id, nx, nz, out MapSurfacePatch? coarse, out int cx, out int cz) ||
                    !coarse!.IsPresent(cx, cz)) continue;
                MapCellEdge facing = (MapCellEdge)(((int)edge + 2) % 4);
                coarse.EdgeSubdivisions.RemoveAll(s => s.CellX == cx && s.CellZ == cz && s.Edge == facing);
                coarse.EdgeSubdivisions.Add(new(cx, cz, facing, k));
                changed.Add((nx, nz));
                writes.Add(coarse.Key);

                bool horizontal = edge is MapCellEdge.South or MapCellEdge.North;
                long x0 = checked(cell.CellX + (edge == MapCellEdge.East ? 1 : 0));
                long z0 = checked(cell.CellZ + (edge == MapCellEdge.North ? 1 : 0));
                MapLatticeAddress from = MapLatticeAddress.Corner(x0, z0);
                MapLatticeAddress to = MapLatticeAddress.Corner(checked(x0 + (horizontal ? 1 : 0)), checked(z0 + (horizontal ? 0 : 1)));
                var boundary = new MapBoundarySurface(source, coarse);
                MapLatticeAddress[] sequence = boundary.Vertices.Keys.Where(a => OnEdge(a, from, to)).OrderBy(a => a).ToArray();

                // A coarse edge can cross a fine slot boundary. Each seam then owns one contiguous patch span.
                int start = 0;
                while (start < k)
                {
                    MapPatchKey fineKey = FineKey(start);
                    int end = start + 1;
                    while (end < k && FineKey(end) == fineKey) end++;
                    MapSurfacePatch fine = candidate.Patches[fineKey];
                    var pairs = new List<(MapLatticeVertex First, MapLatticeVertex Second)>();
                    foreach (MapLatticeAddress address in sequence)
                    {
                        MapExactValue step = Along(address).Multiply(new(k, 1));
                        if (step.CompareTo(new(start, 1)) < 0 || step.CompareTo(new(end, 1)) > 0) continue;
                        MapLatticeAddress fineAddress = Scale(address, k);
                        pairs.Add((new(source.Id, address), new(request.FineSurfaceId, fineAddress)));
                        if (fineAddress.Denominator == 1) AddOwner(fine, fineAddress, new(coarse.Key, address));
                        else
                        {
                            // A legacy corner-cut midpoint survives odd refinement as a half-edge vertex.
                            if (step.Denominator != 2) throw new MapDocumentException("rim subdivision is not representable");
                            long absoluteX = checked(cell.CellX * k + (horizontal ? step.Floor() : edge == MapCellEdge.East ? k - 1 : 0));
                            long absoluteZ = checked(cell.CellZ * k + (!horizontal ? step.Floor() : edge == MapCellEdge.North ? k - 1 : 0));
                            int fx = checked((int)(absoluteX - fine.Key.SlotX * 64 - fine.CellMinX));
                            int fz = checked((int)(absoluteZ - fine.Key.SlotZ * 64 - fine.CellMinZ));
                            fine.EdgeSubdivisions.Add(new(fx, fz, edge, 2));
                        }
                    }
                    string id = $"{request.FineSurfaceId}-rim-{cell.CellX}-{cell.CellZ}-{(int)edge}";
                    if (start != 0 || end != k) id += $"-segment-{start}";
                    if (!ids.Add(id)) throw new MapDocumentException($"duplicate seam id '{id}'");
                    if (pairs.Count < 2) throw new MapDocumentException("rim seam is not representable");
                    var seam = new MapSurfaceSeam(id,
                        new(coarse.Key, pairs[0].First, pairs[^1].First),
                        new(fine.Key, pairs[0].Second, pairs[^1].Second), pairs.AsReadOnly());
                    coarse.Records.Add(seam);
                    records.Add(id);
                    start = end;
                }

                MapExactValue Along(MapLatticeAddress a) => horizontal
                    ? new MapExactValue(a.X, a.Denominator).Subtract(new(x0, 1))
                    : new MapExactValue(a.Z, a.Denominator).Subtract(new(z0, 1));

                MapPatchKey FineKey(int step)
                {
                    long fx = checked(cell.CellX * k + (horizontal ? step : edge == MapCellEdge.East ? k - 1 : 0));
                    long fz = checked(cell.CellZ * k + (!horizontal ? step : edge == MapCellEdge.North ? k - 1 : 0));
                    return MapPatchKey.ForCell(request.FineSurfaceId, fx, fz);
                }
            }
        return Array.AsReadOnly(changed.OrderBy(c => c.Z).ThenBy(c => c.X).ToArray());

        void AddOwner(MapSurfacePatch fine, MapLatticeAddress address, MapVertexOwner owner)
        {
            int x = checked((int)(address.X - fine.Key.SlotX * 64 - fine.CellMinX));
            int z = checked((int)(address.Z - fine.Key.SlotZ * 64 - fine.CellMinZ));
            MapCornerDependency? existing = fine.CornerDependencies.FirstOrDefault(d => d.CornerX == x && d.CornerZ == z);
            if (existing is not null) return;
            fine.CornerDependencies.Add(new(x, z, owner));
            owners.Add(new(fine.Key, x, z, null, owner));
        }
    }

    static MapLatticeAddress Scale(MapLatticeAddress address, int k)
    {
        Int128 x = (Int128)address.X * k, z = (Int128)address.Z * k;
        Int128 gcd = MapExactValue.Gcd(MapExactValue.Gcd(x, z), address.Denominator);
        x /= gcd;
        z /= gcd;
        if (x < long.MinValue || x > long.MaxValue || z < long.MinValue || z > long.MaxValue)
            throw new MapExactOverflowException();
        return MapLatticeAddress.Create((long)x, (long)z, address.Denominator / (int)gcd);
    }

    static bool OnEdge(MapLatticeAddress a, MapLatticeAddress from, MapLatticeAddress to)
    {
        MapExactValue x = new(a.X, a.Denominator), z = new(a.Z, a.Denominator);
        return from.X == to.X ? x == new MapExactValue(from.X, 1) && z.CompareTo(new(from.Z, 1)) >= 0 && z.CompareTo(new(to.Z, 1)) <= 0
            : z == new MapExactValue(from.Z, 1) && x.CompareTo(new(from.X, 1)) >= 0 && x.CompareTo(new(to.X, 1)) <= 0;
    }
}
