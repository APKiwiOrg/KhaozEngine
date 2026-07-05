using System;
using System.Collections.Generic;

namespace KhaozEngine.Replication;

/// <summary>
/// A uniform spatial hash mapping positions to <see cref="NetId"/> values, for area-of-interest queries: given
/// a client's viewpoint and radius, which entities are relevant. Rebuild it each tick (<see cref="Clear"/> then
/// <see cref="Insert"/> every replicated entity's position), then <c>Query</c> per client. Sparse
/// (only occupied cells exist), so it handles large/negative-coordinate worlds.
/// </summary>
public sealed class InterestGrid
{
    private readonly float cellSize;
    private readonly Dictionary<long, List<(long netId, float x, float y)>> cells = new();

    /// <param name="cellSize">Cell edge length in world units (tune ~ to the typical query radius). Must be &gt; 0.</param>
    public InterestGrid(float cellSize)
    {
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "must be positive");
        this.cellSize = cellSize;
    }

    /// <summary>Drops all entries (call before re-inserting the current tick's positions).</summary>
    public void Clear() => cells.Clear();

    /// <summary>Adds an entity's position.</summary>
    public void Insert(long netId, float x, float y)
    {
        long key = PackKey(CellCoord(x), CellCoord(y));
        if (!cells.TryGetValue(key, out List<(long, float, float)>? list))
        {
            list = new List<(long, float, float)>();
            cells[key] = list;
        }
        list.Add((netId, x, y));
    }

    /// <summary>
    /// Adds every NetId within <paramref name="radius"/> of (<paramref name="cx"/>,<paramref name="cy"/>) to
    /// <paramref name="results"/> (exact distance, not just cell overlap). Sweeps only the cells the query AABB
    /// covers.
    /// </summary>
    public void Query(float cx, float cy, float radius, ICollection<long> results)
    {
        if (results is null) throw new ArgumentNullException(nameof(results));
        int minX = CellCoord(cx - radius), maxX = CellCoord(cx + radius);
        int minY = CellCoord(cy - radius), maxY = CellCoord(cy + radius);
        float r2 = radius * radius;
        for (int gy = minY; gy <= maxY; gy++)
        {
            for (int gx = minX; gx <= maxX; gx++)
            {
                if (!cells.TryGetValue(PackKey(gx, gy), out List<(long netId, float x, float y)>? list)) continue;
                foreach ((long netId, float x, float y) in list)
                {
                    float dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy <= r2) results.Add(netId);
                }
            }
        }
    }

    /// <summary>Convenience: the set of NetIds within <paramref name="radius"/> of the point.</summary>
    public HashSet<long> Query(float cx, float cy, float radius)
    {
        var set = new HashSet<long>();
        Query(cx, cy, radius, set);
        return set;
    }

    private int CellCoord(float v) => (int)MathF.Floor(v / cellSize);

    private static long PackKey(int gx, int gy) => ((long)gx << 32) | (uint)gy;
}
