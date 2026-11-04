using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Sparse physical bands and directional endpoints, bounded before allocation by the capture budget.</summary>
internal sealed class ContactTerrainBandIndex
{
    readonly Dictionary<int, int[]?> _cells = [];
    readonly int _width, _height;
    internal long Bytes { get; private set; }
    internal ContactTerrainBandIndex(PhysicsNavDescentEvidence e, IEnumerable<int[]> components, MoveTuning tuning)
    {
        _width = e.Width; _height = e.Height; var o = e.Options;
        long records = 0;
        foreach (var component in components)
        {
            var mask = new HashSet<int>();
            foreach (int index in component)
            {
                var face = e.Face(index); double minX = double.PositiveInfinity, minZ = minX, maxX = double.NegativeInfinity, maxZ = maxX;
                foreach (var p in face.Points) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z); }
                int x0 = (int)Math.Clamp(Math.Floor((minX - tuning.CapsuleRadius - o.MinX) / o.CellSize), 0, e.Width - 1);
                int x1 = (int)Math.Clamp(Math.Floor((maxX + tuning.CapsuleRadius - o.MinX) / o.CellSize), 0, e.Width - 1);
                int z0 = (int)Math.Clamp(Math.Floor((minZ - tuning.CapsuleRadius - o.MinZ) / o.CellSize), 0, e.Height - 1);
                int z1 = (int)Math.Clamp(Math.Floor((maxZ + tuning.CapsuleRadius - o.MinZ) / o.CellSize), 0, e.Height - 1);
                for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
                {
                    int cell = checked(z * e.Width + x);
                    if (mask.Contains(cell) || !Touches(face, o.MinX + x * (double)o.CellSize,
                            o.MinZ + z * (double)o.CellSize, o.CellSize, tuning.CapsuleRadius)) continue;
                    if (records >= o.MaxLayerCells)
                        throw new PhysicsNavDescentEvidenceException(PhysicsNavDescentEvidenceReason.CapacityExceeded,
                            PhysicsEvidenceStatus.CapacityExceeded);
                    records = checked(records + 1);
                    mask.Add(cell);
                }
            }
            // Count component/cell incidences, including overlapping components, before endpoint arrays exist.
            // An overlap is ambiguous for lookup, but still consumes construction work and its bounded scratch.
            var ends = mask.ToDictionary(cell => cell, _ => new int[8]);
            int[] ordered = mask.Order().ToArray();
            for (int direction = 0; direction < 8; direction++)
            {
                int dx = NavTraversalLayer.NeighborX(direction), dz = NavTraversalLayer.NeighborZ(direction);
                bool ascending = dz < 0 || dz == 0 && dx < 0;
                for (int i = 0; i < ordered.Length; i++)
                {
                    int cell = ordered[ascending ? i : ordered.Length - 1 - i];
                    int x = cell % e.Width, z = cell / e.Width, nx = x + dx, nz = z + dz;
                    if ((uint)nx >= (uint)e.Width || (uint)nz >= (uint)e.Height) continue;
                    int next = nz * e.Width + nx;
                    ends[cell][direction] = ends.TryGetValue(next, out var end) ? checked(end[direction] + 1) : 0;
                }
            }
            foreach (var (cell, end) in ends)
                if (!_cells.TryAdd(cell, end)) _cells[cell] = null;
        }
        // Numeric construction-payload upper bound. Managed dictionary overhead is not included.
        Bytes = checked(records * 36);
    }
    internal int Reach(int x, int z, int direction)
    {
        if ((uint)x >= (uint)_width || (uint)z >= (uint)_height ||
            !_cells.TryGetValue(z * _width + x, out var end) || end is null) return 0;
        return Math.Min(Math.Max(_width, _height), checked(end[direction] + 1));
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
