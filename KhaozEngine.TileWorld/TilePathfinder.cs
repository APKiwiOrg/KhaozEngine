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
    public static TilePath FindPath(TileCollisionMap map, int plane, TileCoord start, TileCoord goal, int agentSize = 1, int maxRadius = DefaultMaxRadius, TilePathfinderScratch? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (maxRadius < 1 || maxRadius > MaxSearchRadius)
            throw new ArgumentOutOfRangeException(nameof(maxRadius), maxRadius, $"maxRadius must be 1..{MaxSearchRadius}");
        if (start.X == goal.X && start.Z == goal.Z) return TilePath.Empty(new TileCoord(start.X, start.Z, plane));

        int side = 2 * maxRadius + 1;
        int cells = side * side;
        int originX = start.X - maxRadius, originZ = start.Z - maxRadius;
        // A scratch hands back arrays it has already handed out, so every read below is bounded by cells rather
        // than by Length: a scratch sized for a bigger radius is longer than this window needs.
        int[] dist;
        byte[] parent;
        Queue<int> queue;
        if (scratch is null)
        {
            dist = new int[cells];
            parent = new byte[cells];
            Array.Fill(dist, -1);
            queue = new Queue<int>();
        }
        else
        {
            scratch.Reset(cells);
            dist = scratch.Dist;
            parent = scratch.Parent;
            queue = scratch.Queue;
        }

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
        int endIndex = reached ? goalIndex : NearestReachable(dist, cells, side, originX, originZ, goal);
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

    static int NearestReachable(int[] dist, int cells, int side, int originX, int originZ, TileCoord goal)
    {
        int best = -1, bestDist = int.MaxValue;
        long bestSq = long.MaxValue;
        for (int i = 0; i < cells; i++)
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

/// <summary>Reusable working memory for <see cref="TilePathfinder.FindPath"/>: the two <c>(2r + 1)^2</c> window
/// arrays and the BFS queue, kept across calls so a caller that paths on a tick stops allocating about 83 KB per
/// search at the default radius. Hand the same instance to every call on one thread.
/// <para>NOT thread safe, and deliberately so: it is one mutable buffer set. A server gives each worker its own,
/// and two searches sharing one instance corrupt each other's window. It also holds its arrays for as long as it
/// lives, so a scratch sized for a huge radius keeps that memory resident.</para>
/// <para>It changes NOTHING about the walk. <see cref="TilePathfinder.FindPath"/> resets the window to exactly
/// what freshly allocated arrays hold before every search, so a scratch-fed path is byte identical to the
/// allocating one, which server-authoritative movement relies on.</para></summary>
public sealed class TilePathfinderScratch
{
    // Internal rather than properties: TilePathfinder reads them directly on the hot path, and nothing outside
    // this assembly has any business seeing a half-reset window.
    internal int[] Dist = Array.Empty<int>();
    internal byte[] Parent = Array.Empty<byte>();
    internal readonly Queue<int> Queue = new();

    /// <summary>An empty scratch that sizes itself on its first search.</summary>
    public TilePathfinderScratch() { }

    /// <summary>A scratch pre-sized for searches up to <paramref name="maxRadius"/>, so the first search does not
    /// allocate either. A bigger radius later still works, growing the arrays once.</summary>
    /// <param name="maxRadius">Half width of the largest window this scratch should hold, 1..<see
    /// cref="TilePathfinder.MaxSearchRadius"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRadius"/> is outside that range.</exception>
    public TilePathfinderScratch(int maxRadius)
    {
        if (maxRadius < 1 || maxRadius > TilePathfinder.MaxSearchRadius)
            throw new ArgumentOutOfRangeException(nameof(maxRadius), maxRadius, $"maxRadius must be 1..{TilePathfinder.MaxSearchRadius}");
        int side = 2 * maxRadius + 1;
        Grow(side * side);
    }

    /// <summary>Window cells this scratch can hold without growing, <c>(2r + 1)^2</c> for the radius it was sized
    /// to and 0 for one that has never searched.</summary>
    public int Capacity => Dist.Length;

    // Both arrays are put back into their freshly-allocated state over the cells this search will touch: dist
    // filled with -1, parent zeroed. Zeroing parent is not strictly needed, since a cell's parent is written
    // before anything walks back through it, but it costs a quarter of the fill that is already happening and it
    // makes "identical to fresh arrays" true of the whole buffer rather than of an argument about read order.
    internal void Reset(int cells)
    {
        if (Dist.Length < cells) Grow(cells);
        Array.Fill(Dist, -1, 0, cells);
        Array.Clear(Parent, 0, cells);
        Queue.Clear();
    }

    void Grow(int cells)
    {
        Dist = new int[cells];
        Parent = new byte[cells];
    }
}
