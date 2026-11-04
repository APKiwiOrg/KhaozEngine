using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Immutable rasterized physical bands and directional endpoints. Built once per body, not per source.</summary>
internal sealed class ContactTerrainBandIndex
{
    readonly List<(bool[] Mask, int[][] End)> _bands = [];
    readonly int _width, _height;
    internal long Bytes { get; private set; }
    internal ContactTerrainBandIndex(PhysicsNavDescentEvidence e, IEnumerable<int[]> components, MoveTuning tuning)
    {
        _width = e.Width; _height = e.Height; var o = e.Options;
        foreach (var component in components)
        {
            var mask = new bool[checked(e.Width * e.Height)];
            foreach (int index in component)
            {
                var face = e.Face(index); double minX = double.PositiveInfinity, minZ = minX, maxX = double.NegativeInfinity, maxZ = maxX;
                foreach (var p in face.Points) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z); }
                int x0 = Math.Max(0, (int)Math.Floor((minX - tuning.CapsuleRadius - o.MinX) / o.CellSize));
                int x1 = Math.Min(e.Width - 1, (int)Math.Floor((maxX + tuning.CapsuleRadius - o.MinX) / o.CellSize));
                int z0 = Math.Max(0, (int)Math.Floor((minZ - tuning.CapsuleRadius - o.MinZ) / o.CellSize));
                int z1 = Math.Min(e.Height - 1, (int)Math.Floor((maxZ + tuning.CapsuleRadius - o.MinZ) / o.CellSize));
                for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
                    if (Touches(face, o.MinX + x * (double)o.CellSize, o.MinZ + z * (double)o.CellSize, o.CellSize, tuning.CapsuleRadius)) mask[z * e.Width + x] = true;
            }
            var ends = new int[8][];
            for (int direction = 0; direction < 8; direction++)
            {
                int dx = NavTraversalLayer.NeighborX(direction), dz = NavTraversalLayer.NeighborZ(direction);
                var end = ends[direction] = new int[mask.Length];
                for (int zi = 0; zi < e.Height; zi++) for (int xi = 0; xi < e.Width; xi++)
                {
                    int z = dz > 0 ? e.Height - 1 - zi : zi, x = dx > 0 ? e.Width - 1 - xi : xi, nx = x + dx, nz = z + dz;
                    if ((uint)nx >= (uint)e.Width || (uint)nz >= (uint)e.Height) continue;
                    int next = nz * e.Width + nx;
                    end[z * e.Width + x] = mask[next] ? end[next] + 1 : 0;
                }
            }
            _bands.Add((mask, ends)); Bytes += mask.Length * 33L;
        }
    }
    internal int Reach(int x, int z, int direction)
    {
        int cell = z * _width + x, reach = 0;
        foreach (var band in _bands)
        {
            if (!band.Mask[cell]) continue;
            // More than one disconnected band reachable at the source is ambiguous.
            if (reach != 0) return 0;
            reach = Math.Min(Math.Max(_width, _height), band.End[direction][cell] + 1);
        }
        return reach;
    }
    static bool Touches(DescentFace face, double left, double bottom, double size, double radius)
    {
        if (DescentGeometry.Contains(face.Points, left + size / 2, bottom + size / 2)) return true;
        for (int i = 0; i < face.Points.Length; i++)
        {
            var a = face.Points[i]; var b = face.Points[(i + 1) % face.Points.Length];
            if (NavFootprintGeometry.Touches(new((float)a.X, 0, (float)a.Z), new((float)b.X, 0, (float)b.Z), left, bottom, size, radius)) return true;
        }
        return false;
    }
}
