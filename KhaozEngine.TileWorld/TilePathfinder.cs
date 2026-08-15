using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>A path result. <see cref="Tiles"/> are the steps AFTER the start. <see cref="Reached"/> is false
/// when the goal was unreachable within the search window and the path ends at the nearest reachable tile.</summary>
public sealed class TilePath
{
    /// <summary>The steps after the start, in walk order. Empty when the start is the goal or nothing moved.</summary>
    public IReadOnlyList<TileCoord> Tiles { get; }

    /// <summary>True when the walk ends on the requested goal tile.</summary>
    public bool Reached { get; }

    /// <summary>The last tile of the walk, or the start when there are no steps.</summary>
    public TileCoord End { get; }

    /// <summary>Wraps a step list, its reached flag, and the start the walk fell back to when it is empty.</summary>
    public TilePath(IReadOnlyList<TileCoord> tiles, bool reached, TileCoord start)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        Tiles = tiles;
        Reached = reached;
        End = tiles.Count == 0 ? start : tiles[tiles.Count - 1];
    }

    /// <summary>The zero step path that already stands on its goal.</summary>
    public static TilePath Empty(TileCoord start) => new(Array.Empty<TileCoord>(), reached: true, start);
}

/// <summary>Deterministic BFS over the collision map with OSRS's rules: eight-connected through
/// <see cref="TileCollision.CanStep"/> in the fixed W, E, S, N, SW, SE, NW, NE order, bounded to a square
/// window around the start, and an unreachable goal yields the path to the nearest reachable tile (squared
/// Euclidean distance to the goal, then BFS distance, then scan order). Both heads replay identical paths for
/// identical inputs, which server-authoritative movement relies on.</summary>
public static class TilePathfinder
{
    /// <summary>The default half width of the search window, in tiles.</summary>
    public const int DefaultMaxRadius = 64;

    /// <summary>The largest half width <see cref="FindPath"/> accepts. The window's scratch arrays are
    /// <c>(2r + 1)^2</c> entries EACH, so this cap is already about 335 MB of allocation on one call. A radius
    /// above it is far likelier to be a unit mix-up than a search anyone meant to run.</summary>
    public const int MaxSearchRadius = 4096;

    /// <summary>Walks from <paramref name="start"/> toward <paramref name="goal"/> on <paramref name="plane"/>,
    /// which overrides the planes carried on both coords. A start standing on a Blocked tile is treated like any
    /// other start, because <see cref="TileCollision.CanStep"/> allows egress from a tile that was blocked under
    /// the agent, so the search proceeds normally rather than refusing to move. <paramref name="maxRadius"/>
    /// must be 1..<see cref="MaxSearchRadius"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRadius"/> is below 1 or above
    /// <see cref="MaxSearchRadius"/>.</exception>
    public static TilePath FindPath(TileCollisionMap map, int plane, TileCoord start, TileCoord goal, int agentSize = 1, int maxRadius = DefaultMaxRadius)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (maxRadius < 1 || maxRadius > MaxSearchRadius)
            throw new ArgumentOutOfRangeException(nameof(maxRadius), maxRadius, $"maxRadius must be 1..{MaxSearchRadius}");
        if (start.X == goal.X && start.Z == goal.Z) return TilePath.Empty(new TileCoord(start.X, start.Z, plane));

        int side = 2 * maxRadius + 1;
        int originX = start.X - maxRadius, originZ = start.Z - maxRadius;
        var dist = new int[side * side];
        var parent = new byte[side * side];
        Array.Fill(dist, -1);
        var queue = new Queue<int>();

        int startIndex = maxRadius * side + maxRadius;
        dist[startIndex] = 0;
        queue.Enqueue(startIndex);
        int goalIndex = -1;

        // Indexed, not foreach: the neighbour loop runs once per dequeued tile, and IReadOnlyList's enumerator
        // is a heap allocation each time round. The order is still exactly TileDirections.All's, which the
        // tie-breaking depends on.
        IReadOnlyList<TileDirection> dirs = TileDirections.All;
        while (queue.Count > 0 && goalIndex < 0)
        {
            int cur = queue.Dequeue();
            int cx = originX + cur % side, cz = originZ + cur / side;
            for (int i = 0; i < dirs.Count; i++)
            {
                TileDirection d = dirs[i];
                (int dx, int dz) = TileDirections.Delta(d);
                int nx = cx + dx, nz = cz + dz;
                int wx = nx - originX, wz = nz - originZ;
                if ((uint)wx >= (uint)side || (uint)wz >= (uint)side) continue;
                int ni = wz * side + wx;
                if (dist[ni] >= 0) continue;
                if (!TileCollision.CanStep(map, cx, cz, plane, d, agentSize)) continue;
                dist[ni] = dist[cur] + 1;
                parent[ni] = (byte)d;
                if (nx == goal.X && nz == goal.Z) { goalIndex = ni; break; }
                queue.Enqueue(ni);
            }
        }

        bool reached = goalIndex >= 0;
        int endIndex = reached ? goalIndex : NearestReachable(dist, side, originX, originZ, goal);
        if (endIndex == startIndex) return new TilePath(Array.Empty<TileCoord>(), reached: false, new TileCoord(start.X, start.Z, plane));

        var reversed = new List<TileCoord>();
        int idx = endIndex;
        while (idx != startIndex)
        {
            int x = originX + idx % side, z = originZ + idx / side;
            reversed.Add(new TileCoord(x, z, plane));
            (int pdx, int pdz) = TileDirections.Delta((TileDirection)parent[idx]);
            idx = (z - pdz - originZ) * side + (x - pdx - originX);
        }
        reversed.Reverse();
        return new TilePath(reversed, reached, new TileCoord(start.X, start.Z, plane));
    }

    static int NearestReachable(int[] dist, int side, int originX, int originZ, TileCoord goal)
    {
        int best = -1, bestDist = int.MaxValue;
        long bestSq = long.MaxValue;
        for (int i = 0; i < dist.Length; i++)
        {
            if (dist[i] < 0) continue;
            long ex = originX + i % side - goal.X, ez = originZ + i / side - goal.Z;
            long sq = ex * ex + ez * ez;
            if (sq < bestSq || (sq == bestSq && dist[i] < bestDist))
            {
                best = i; bestSq = sq; bestDist = dist[i];
            }
        }
        return best;
    }
}
