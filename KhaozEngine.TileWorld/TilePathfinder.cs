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
/// identical inputs, which server-authoritative movement relies on.
/// <para><see cref="FindPath"/> and <see cref="FindPathToAny"/> run ONE expansion between them, so the step
/// rules, the window bound and the tie-breaking order cannot drift apart. Every step costs one, diagonals
/// included, so a BFS level IS a path length and the two entry points agree on which walk is shorter.</para></summary>
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
        RequireRadius(maxRadius);
        if (start.X == goal.X && start.Z == goal.Z) return TilePath.Empty(new TileCoord(start.X, start.Z, plane));

        var window = new SearchWindow(start, maxRadius, scratch);
        int endIndex = Flood(map, plane, agentSize, window, scratch, goals: null, goal, out _);
        bool reached = endIndex >= 0;
        if (!reached) endIndex = NearestReachable(window, goal);
        return Rebuild(window, endIndex, reached, start, plane);
    }

    /// <summary>Walks from <paramref name="start"/> to the NEAREST of <paramref name="goals"/> in ONE search, and
    /// reports which one through <paramref name="goalIndex"/>. The answer is the one a
    /// <see cref="FindPath"/> per goal gives: same steps, and the same winner on a tie.
    /// <para>The shortest walk wins, and goals that tie on length fall to the LOWEST index in
    /// <paramref name="goals"/>, so a caller's own candidate order is the tie rule and both heads pick the same
    /// goal. The search finishes the BFS level on which the first goal is discovered, which is what makes that tie
    /// total: every goal at the winning length is known before one is chosen. A path here is the path that goal's
    /// own <see cref="FindPath"/> builds, because a cell's parent is written once at first discovery and the
    /// discovery order does not depend on which goal ends the search.</para>
    /// <para>There is NO nearest-reachable fallback, which is the one place this differs from
    /// <see cref="FindPath"/>: a goal SET has no single tile to measure nearness to. An empty list, and a list
    /// none of whose goals the window can reach, both answer a not-reached empty path with a
    /// <paramref name="goalIndex"/> of -1. A goal outside the window is simply never discovered, and a duplicated
    /// goal resolves to its lowest index.</para></summary>
    /// <param name="map">The collision map to path over.</param>
    /// <param name="plane">The plane to search on, overriding the planes carried on the coords.</param>
    /// <param name="start">The tile the agent's anchor stands on.</param>
    /// <param name="goals">The candidate goals, in the caller's own tie-break order.</param>
    /// <param name="agentSize">The agent's NxN footprint edge in tiles, anchored on its south-west tile.</param>
    /// <param name="maxRadius">Half width of the search window, 1..<see cref="MaxSearchRadius"/>.</param>
    /// <param name="scratch">Reusable window memory, or null to allocate one for this search.</param>
    /// <param name="goalIndex">The index in <paramref name="goals"/> of the goal walked to, or -1 when none was
    /// reached.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> or <paramref name="goals"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRadius"/> is below 1 or above
    /// <see cref="MaxSearchRadius"/>.</exception>
    public static TilePath FindPathToAny(TileCollisionMap map, int plane, TileCoord start,
        IReadOnlyList<TileCoord> goals, int agentSize, int maxRadius, TilePathfinderScratch? scratch,
        out int goalIndex)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(goals);
        RequireRadius(maxRadius);

        goalIndex = -1;
        var origin = new TileCoord(start.X, start.Z, plane);
        if (goals.Count == 0) return new TilePath(Array.Empty<TileCoord>(), reached: false, origin);

        var window = new SearchWindow(start, maxRadius, scratch);
        int endIndex = Flood(map, plane, agentSize, window, scratch, goals, default, out goalIndex);
        if (endIndex < 0) return new TilePath(Array.Empty<TileCoord>(), reached: false, origin);
        if (endIndex == window.StartIndex) return TilePath.Empty(origin);
        return Rebuild(window, endIndex, reached: true, start, plane);
    }

    static void RequireRadius(int maxRadius)
    {
        if (maxRadius < 1 || maxRadius > MaxSearchRadius)
            throw new ArgumentOutOfRangeException(nameof(maxRadius), maxRadius, $"maxRadius must be 1..{MaxSearchRadius}");
    }

    // The ONE expansion both entry points run, which is what keeps the step rules, the window bound and the
    // direction order from drifting between them. Returns the window index the walk ends on, or -1.
    //
    // A null `goals` is the single-goal form and stops the instant `single` is discovered, mid level, exactly
    // where it always did. A goal list runs LEVEL BY LEVEL and stops at the top of the first level that finds a
    // goal already discovered: everything queued at the top of a pass is one BFS distance, so expanding the pass
    // discovers the next distance whole, and by the time distance d is dequeued every cell at d or less is
    // written. That decides both the shortest goal and the index tie among the goals sharing its distance.
    // Nothing in the tree depends on which goal ends the search, since a cell's dist and parent are written once
    // at first discovery, so each goal's chain here is the chain its own single-goal search would have built.
    static int Flood(TileCollisionMap map, int plane, int agentSize, in SearchWindow w,
        TilePathfinderScratch? scratch, IReadOnlyList<TileCoord>? goals, TileCoord single, out int goalIndex)
    {
        goalIndex = -1;
        int[] dist = w.Dist;
        byte[] parent = w.Parent;
        Queue<int> queue = w.Queue;
        int side = w.Side, originX = w.OriginX, originZ = w.OriginZ;

        dist[w.StartIndex] = 0;
        queue.Enqueue(w.StartIndex);

        // The window index of each goal once, so the per-level check below is one read per candidate rather than
        // a coordinate comparison per discovered cell.
        int[]? goalCells = goals is null ? null : GoalCells(goals, w);
        int endIndex = -1;
        long expanded = 0;

        // Indexed, not foreach: the neighbour loop runs once per dequeued tile, and IReadOnlyList's enumerator
        // is a heap allocation each time round. The order is still exactly TileDirections.All's, which the
        // tie-breaking depends on.
        IReadOnlyList<TileDirection> dirs = TileDirections.All;
        while (queue.Count > 0)
        {
            if (goalCells is not null)
            {
                int won = Winner(goalCells, dist);
                if (won >= 0) { goalIndex = won; endIndex = goalCells[won]; break; }
            }

            for (int pass = queue.Count; pass > 0 && endIndex < 0; pass--)
            {
                int cur = queue.Dequeue();
                expanded++;
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
                    if (goalCells is null && nx == single.X && nz == single.Z) { goalIndex = 0; endIndex = ni; break; }
                    queue.Enqueue(ni);
                }
            }
            if (endIndex >= 0) break;
        }

        if (scratch is not null) { scratch.CellsExpanded += expanded; scratch.SearchesRun++; }
        return endIndex;
    }

    // -1 for a goal the window cannot hold, which is never discovered and so never wins. In long because a goal
    // far from the window overflows the subtraction in int, and a wrapped offset could land back inside it.
    static int[] GoalCells(IReadOnlyList<TileCoord> goals, in SearchWindow w)
    {
        var cells = new int[goals.Count];
        for (int i = 0; i < goals.Count; i++)
        {
            long wx = (long)goals[i].X - w.OriginX, wz = (long)goals[i].Z - w.OriginZ;
            cells[i] = wx < 0 || wx >= w.Side || wz < 0 || wz >= w.Side ? -1 : (int)(wz * w.Side + wx);
        }
        return cells;
    }

    // The shortest discovered goal, ties falling to the lowest index: the >= keeps the FIRST of a tie.
    static int Winner(int[] goalCells, int[] dist)
    {
        int best = -1, bestDist = int.MaxValue;
        for (int i = 0; i < goalCells.Length; i++)
        {
            int cell = goalCells[i];
            if (cell < 0) continue;
            int d = dist[cell];
            if (d < 0 || d >= bestDist) continue;
            best = i;
            bestDist = d;
        }
        return best;
    }

    static TilePath Rebuild(in SearchWindow w, int endIndex, bool reached, TileCoord start, int plane)
    {
        if (endIndex == w.StartIndex) return new TilePath(Array.Empty<TileCoord>(), reached: false, new TileCoord(start.X, start.Z, plane));

        var reversed = new List<TileCoord>();
        int idx = endIndex;
        while (idx != w.StartIndex)
        {
            int x = w.OriginX + idx % w.Side, z = w.OriginZ + idx / w.Side;
            reversed.Add(new TileCoord(x, z, plane));
            (int pdx, int pdz) = TileDirections.Delta((TileDirection)w.Parent[idx]);
            idx = (z - pdz - w.OriginZ) * w.Side + (x - pdx - w.OriginX);
        }
        reversed.Reverse();
        return new TilePath(reversed, reached, new TileCoord(start.X, start.Z, plane));
    }

    static int NearestReachable(in SearchWindow w, TileCoord goal)
    {
        int[] dist = w.Dist;
        int best = -1, bestDist = int.MaxValue;
        long bestSq = long.MaxValue;
        for (int i = 0; i < w.Cells; i++)
        {
            if (dist[i] < 0) continue;
            long ex = w.OriginX + i % w.Side - goal.X, ez = w.OriginZ + i / w.Side - goal.Z;
            long sq = ex * ex + ez * ez;
            if (sq < bestSq || (sq == bestSq && dist[i] < bestDist))
            {
                best = i; bestSq = sq; bestDist = dist[i];
            }
        }
        return best;
    }

    // The window one search floods, sized and reset in one place so both entry points bound themselves the same
    // way. A scratch hands back arrays it has already handed out, so every read is bounded by Cells rather than
    // by Length: a scratch sized for a bigger radius is longer than this window needs.
    readonly struct SearchWindow
    {
        internal readonly int[] Dist;
        internal readonly byte[] Parent;
        internal readonly Queue<int> Queue;
        internal readonly int Side;
        internal readonly int Cells;
        internal readonly int OriginX;
        internal readonly int OriginZ;
        internal readonly int StartIndex;

        internal SearchWindow(TileCoord start, int maxRadius, TilePathfinderScratch? scratch)
        {
            Side = 2 * maxRadius + 1;
            Cells = Side * Side;
            OriginX = start.X - maxRadius;
            OriginZ = start.Z - maxRadius;
            StartIndex = maxRadius * Side + maxRadius;
            if (scratch is null)
            {
                Dist = new int[Cells];
                Parent = new byte[Cells];
                Array.Fill(Dist, -1);
                Queue = new Queue<int>();
            }
            else
            {
                scratch.Reset(Cells);
                Dist = scratch.Dist;
                Parent = scratch.Parent;
                Queue = scratch.Queue;
            }
        }
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

    // Test seam (InternalsVisibleTo), and the reason it is not a public diagnostic is that a game has no use for
    // it. Cells dequeued and searches run through this instance, ACCUMULATED and never cleared by Reset, because
    // what a multi-goal search is worth is the TOTAL across a call that used to run one flood per candidate, which
    // a per-search number cannot show. The flood counts into a local and folds it in once, so the loop itself
    // carries no branch for this.
    internal long CellsExpanded;
    internal int SearchesRun;

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

    internal void ClearCounters()
    {
        CellsExpanded = 0;
        SearchesRun = 0;
    }

    void Grow(int cells)
    {
        Dist = new int[cells];
        Parent = new byte[cells];
    }
}
