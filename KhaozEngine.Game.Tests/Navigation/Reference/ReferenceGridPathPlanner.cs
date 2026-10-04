// Frozen copy of the v20.24.0 GridPathPlanner algorithm (KhaozEngine#1288). The equality corpus compares the
// production planner against it, so this file must never change with the production planner.
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Navigation;

/// <summary>
/// Grid A* implementation of <see cref="IPathPlanner"/> over a <see cref="NavSpace"/>. A query snaps
/// both endpoints onto a passable cell, then takes a line-of-sight fast path when the goal is directly
/// visible on the start's layer, otherwise runs an 8-connected A* search. The search prevents diagonal
/// corner-cutting (a diagonal step needs both orthogonal companions passable), crosses layers over
/// <see cref="NavSpace.Links"/> (each link is a graph edge whose meters cost is precomputed per
/// <see cref="NavLinkKind"/>: a <see cref="NavLinkKind.Stair"/> costs one cell of its source layer, a
/// <see cref="NavLinkKind.Hop"/> costs the constructor's <c>hopCostCells</c> cells, its far endpoint
/// re-checked for the agent radius), caps its work at <see cref="PathQueryBudget.MaxExpandedNodes"/>
/// expansions, and on an unreachable goal returns a <see cref="NavPathStatus.Partial"/> route to the
/// closest node it reached (or <see cref="NavPath.Unreachable"/> when it never got past the start).
/// The raw cell chain is then string-pulled: within each same-layer run it greedily keeps only the
/// farthest cell still in clear line of sight from the current anchor, collapsing collinear or
/// diagonally-clear runs to a few turn waypoints. Both endpoints of every link crossing are always
/// emitted (paths never smooth across a layer change), a <see cref="NavLinkKind.Hop"/> crossing's landing
/// waypoint carries <see cref="NavWaypointKind.Hop"/>, and a completed path's final waypoint follows
/// the exact-goal rule from snapping (keeping the replaced waypoint's kind). Node addressing spans every layer
/// (<c>layerOffset[layer] + z * width + x</c>), so the search, the closed set, and the link map share
/// one flat index space. Deterministic: fixed neighbor order and a monotone insertion counter break
/// every tie the same way.
/// </summary>
internal sealed partial class ReferenceGridPathPlanner : IRegionPathPlanner
{
    /// <summary>Diagonal step-cost multiplier over the orthogonal step (in cell units).</summary>
    static readonly float Sqrt2 = MathF.Sqrt(2f);

    /// <summary>Neighbor X offsets, fixed order: the four orthogonals first, then the four diagonals.
    /// Iterating this in order (paired with <see cref="NeighborDz"/>) keeps expansion deterministic.</summary>
    static readonly int[] NeighborDx = { 1, -1, 0, 0, 1, 1, -1, -1 };

    /// <summary>Neighbor Z offsets, paired index-for-index with <see cref="NeighborDx"/>. Indices 0-3
    /// are orthogonal steps, indices 4-7 are diagonal steps.</summary>
    static readonly int[] NeighborDz = { 0, 0, 1, -1, 1, -1, 1, -1 };

    readonly NavSpace _space;

    /// <summary>Running sum of each earlier layer's cell count, so node id
    /// <c>layerOffset[layer] + z * layer.Width + x</c> is unique across the whole space.</summary>
    readonly int[] _layerOffset;

    /// <summary>Total node count across every layer, the length of the search's per-node arrays.</summary>
    readonly int _totalNodes;

    /// <summary>Link edges as an adjacency list: source node id to its reachable node ids each paired with
    /// its precomputed traversal cost in meters. Built once from <see cref="NavSpace.Links"/> (directed, so
    /// a two-way stair contributes two entries). A <see cref="NavLinkKind.Stair"/> link costs one cell of
    /// its source layer (<see cref="NavGrid.CellSize"/>, the historical hardcoded value), a
    /// <see cref="NavLinkKind.Hop"/> link costs <see cref="_hopCostCells"/> of them. The far endpoint's own
    /// passability is still checked at expansion time.</summary>
    readonly Dictionary<int, List<(int ToId, float CostMeters)>> _linkEdges;

    /// <summary>The directed node-id pairs of every <see cref="NavLinkKind.Hop"/> link, keyed by the same
    /// flat node ids the adjacency uses. Reconstruction consults this to stamp
    /// <see cref="NavWaypointKind.Hop"/> on a hop crossing's landing waypoint.</summary>
    readonly HashSet<(int FromId, int ToId)> _hopEdges;

    /// <summary>Cost of crossing a <see cref="NavLinkKind.Hop"/> link, in multiples of the source layer's
    /// <see cref="NavGrid.CellSize"/>. Set from the constructor knob.</summary>
    readonly float _hopCostCells;

    /// <summary>Builds a planner that searches <paramref name="space"/>. <paramref name="hopCostCells"/> is
    /// the cost of crossing a <see cref="NavLinkKind.Hop"/> link, in multiples of the source layer's
    /// <see cref="NavGrid.CellSize"/> (default 4). It must be positive. A <see cref="NavLinkKind.Stair"/>
    /// link keeps its one-cell cost. Keep <paramref name="hopCostCells"/> at or above the longest hop's
    /// octile displacement (about 2.83 at a two-cell hop) to keep the A* heuristic admissible and the search
    /// optimal. Below it the search stays correct but may return a valid non-optimal route, the same caveat
    /// the far-jumping-link heuristic already documents.</summary>
    public ReferenceGridPathPlanner(NavSpace space, float hopCostCells = 4f)
        : this(space, hopCostCells, null)
    {
    }

    ReferenceGridPathPlanner(NavSpace space, float hopCostCells, NavTraversalGraph? traversal)
    {
        _space = space ?? throw new ArgumentNullException(nameof(space));
        _traversal = traversal;
        if (hopCostCells <= 0f)
            throw new ArgumentOutOfRangeException(nameof(hopCostCells), hopCostCells, "Hop cost cells must be positive.");
        _hopCostCells = hopCostCells;

        IReadOnlyList<NavGrid> layers = _space.Layers;
        _layerOffset = new int[layers.Count];
        int total = 0;
        for (int i = 0; i < layers.Count; i++)
        {
            _layerOffset[i] = total;
            total += layers[i].Width * layers[i].Height;
        }
        _totalNodes = total;

        _linkEdges = new Dictionary<int, List<(int ToId, float CostMeters)>>();
        _hopEdges = new HashSet<(int FromId, int ToId)>();
        foreach (NavLink link in traversal?.Links ?? _space.Links)
        {
            int fromId = _layerOffset[link.FromLayer] + link.FromZ * layers[link.FromLayer].Width + link.FromX;
            int toId = _layerOffset[link.ToLayer] + link.ToZ * layers[link.ToLayer].Width + link.ToX;

            // A Stair keeps exactly the source layer's cell size, the historical hardcoded link cost, so a
            // hop-free space plans byte-identically. A Hop is charged the knob's multiple of it.
            float costCells = link.Kind == NavLinkKind.Hop ? _hopCostCells : 1f;
            float costMeters = costCells * layers[link.FromLayer].CellSize;

            if (!_linkEdges.TryGetValue(fromId, out List<(int ToId, float CostMeters)>? targets))
            {
                targets = new List<(int ToId, float CostMeters)>();
                _linkEdges[fromId] = targets;
            }
            targets.Add((toId, costMeters));

            if (link.Kind == NavLinkKind.Hop)
                _hopEdges.Add((fromId, toId));
        }
    }

    /// <summary>
    /// Finds a route from <paramref name="start"/> to <paramref name="goal"/> for an agent of
    /// <paramref name="agentRadius"/>, within <paramref name="budget"/>. Resolves each endpoint's
    /// layer via <see cref="NavSpace.LayerAt"/> (surface-aware for layered bakes, falling back to
    /// the <see cref="NavSpace.LayerOf"/> Y band for height-less grids), snaps both onto a passable cell
    /// within <see cref="PathQueryBudget.SnapRadius"/> (failing that, returns
    /// <see cref="NavPath.Unreachable"/>), then tries the same-layer line-of-sight fast path before
    /// falling through to the A* search, which routes within and across layers.
    /// </summary>
    public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget)
    {
        ValidateTraversalRadius(agentRadius);
        int startLayer = _space.LayerAt(start);
        int goalLayer = _space.LayerAt(goal);

        NavGrid startGrid = _space.Layers[startLayer];
        NavGrid goalGrid = _space.Layers[goalLayer];

        var startXz = new Vector2(start.X, start.Z);
        var goalXz = new Vector2(goal.X, goal.Z);

        Vector2? startPoint = SnapToPassable(startLayer, startGrid, startXz, agentRadius, budget.SnapRadius, out _);
        if (startPoint is null)
        {
            return NavPath.Unreachable;
        }

        Vector2? goalSnap = SnapToPassable(goalLayer, goalGrid, goalXz, agentRadius, budget.SnapRadius, out bool goalSnappedOwnCell);
        if (goalSnap is null)
        {
            return NavPath.Unreachable;
        }

        // The goal keeps its exact query position only when that position's own cell was passable and
        // won the snap outright. Otherwise the nearest passable cell center stands in for it.
        Vector2 goalPoint = goalSnappedOwnCell && _traversal is null ? goalXz : goalSnap.Value;

        // Same layer with a clear straight shot is the trivial one-waypoint case. Everything else runs
        // the A* search, which also carries cross-layer routing over the link edges.
        if (_traversal is null && startLayer == goalLayer && HasLineOfSight(startGrid, startPoint.Value, goalPoint, agentRadius))
        {
            return new NavPath(NavPathStatus.Complete, new[] { new NavWaypoint(goalPoint, goalLayer) });
        }

        return RunAStar(startLayer, startPoint.Value, goalLayer, goalPoint, agentRadius, budget);
    }

    /// <summary>
    /// Finds the passable cell nearest <paramref name="worldXz"/> in <paramref name="grid"/> for an
    /// agent of <paramref name="agentRadius"/>. Searches Chebyshev rings centered on the cell
    /// containing <paramref name="worldXz"/>, from ring 0 up to
    /// <c>ceil(snapRadius / grid.CellSize)</c>. The first ring with any passable cell wins, and within
    /// that ring the cell minimizing squared distance from <paramref name="worldXz"/> to its center
    /// wins (ties keep whichever is found first, scanning z low to high then x low to high within the
    /// ring). Returns the winning cell's world-space center, or null when no passable cell was found
    /// in range. <paramref name="snappedToOwnCell"/> reports whether the winning cell is the one
    /// <paramref name="worldXz"/> itself falls in.
    /// </summary>
    Vector2? SnapToPassable(int layer, NavGrid grid, Vector2 worldXz, float agentRadius, float snapRadius, out bool snappedToOwnCell)
    {
        (int queryX, int queryZ) = grid.CellOf(worldXz.X, worldXz.Y);
        if (_traversal is not null) return SnapTraversal(layer, queryX, queryZ, out snappedToOwnCell);
        int maxRing = (int)MathF.Ceiling(snapRadius / grid.CellSize);

        for (int ring = 0; ring <= maxRing; ring++)
        {
            bool found = false;
            int bestX = 0, bestZ = 0;
            Vector2 bestCenter = default;
            float bestDistanceSq = float.PositiveInfinity;

            for (int z = queryZ - ring; z <= queryZ + ring; z++)
            {
                for (int x = queryX - ring; x <= queryX + ring; x++)
                {
                    if (Math.Max(Math.Abs(x - queryX), Math.Abs(z - queryZ)) != ring)
                    {
                        continue;
                    }

                    if (!grid.InBounds(x, z) || Blocks(layer, agentRadius, x, z))
                    {
                        continue;
                    }

                    Vector2 center = grid.CellCenter(x, z);
                    float distanceSq = Vector2.DistanceSquared(center, worldXz);
                    if (distanceSq < bestDistanceSq)
                    {
                        bestDistanceSq = distanceSq;
                        bestCenter = center;
                        bestX = x;
                        bestZ = z;
                        found = true;
                    }
                }
            }

            if (found)
            {
                snappedToOwnCell = bestX == queryX && bestZ == queryZ;
                return bestCenter;
            }
        }

        snappedToOwnCell = false;
        return null;
    }

    /// <summary>
    /// True when a straight line from <paramref name="fromWorldXz"/> to <paramref name="toWorldXz"/>
    /// crosses no cell that <see cref="Blocks(NavGrid, float, int, int)"/> for <paramref name="agentRadius"/>, via
    /// <see cref="GridRay.IsClear"/>.
    /// </summary>
    /// <remarks>
    /// GridRay walks axis-aligned local cells. Both endpoints are inverse-transformed through the
    /// grid's translation and yaw before tracing, so path smoothing tests the same cells as A*.
    /// </remarks>
    static bool HasLineOfSight(NavGrid grid, Vector2 fromWorldXz, Vector2 toWorldXz, float agentRadius)
    {
        Vector2 localFrom = grid.WorldToLocal(fromWorldXz);
        Vector2 localTo = grid.WorldToLocal(toWorldXz);

        return GridRay.IsClear(
            localFrom, localTo, grid.CellSize,
            (x, z) => Blocks(grid, agentRadius, x, z),
            includeEndpointCells: false);
    }

    /// <summary>True when an agent of <paramref name="agentRadius"/> does not fit at
    /// (<paramref name="cx"/>, <paramref name="cz"/>) in <paramref name="grid"/>. The shared blocked
    /// predicate for snapping, the line-of-sight fast path, A* neighbor and link expansion, and the
    /// string pull.</summary>
    static bool Blocks(NavGrid grid, float agentRadius, int cx, int cz) => !grid.IsPassable(cx, cz, agentRadius);
}
