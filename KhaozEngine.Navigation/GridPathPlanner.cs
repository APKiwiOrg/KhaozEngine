using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;

namespace KhaozEngine.Navigation;

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
public sealed class GridPathPlanner : IPathPlanner
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
    public GridPathPlanner(NavSpace space, float hopCostCells = 4f)
    {
        _space = space ?? throw new ArgumentNullException(nameof(space));
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
        foreach (NavLink link in _space.Links)
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
        int startLayer = _space.LayerAt(start);
        int goalLayer = _space.LayerAt(goal);

        NavGrid startGrid = _space.Layers[startLayer];
        NavGrid goalGrid = _space.Layers[goalLayer];

        var startXz = new Vector2(start.X, start.Z);
        var goalXz = new Vector2(goal.X, goal.Z);

        Vector2? startPoint = SnapToPassable(startGrid, startXz, agentRadius, budget.SnapRadius, out _);
        if (startPoint is null)
        {
            return NavPath.Unreachable;
        }

        Vector2? goalSnap = SnapToPassable(goalGrid, goalXz, agentRadius, budget.SnapRadius, out bool goalSnappedOwnCell);
        if (goalSnap is null)
        {
            return NavPath.Unreachable;
        }

        // The goal keeps its exact query position only when that position's own cell was passable and
        // won the snap outright. Otherwise the nearest passable cell center stands in for it.
        Vector2 goalPoint = goalSnappedOwnCell ? goalXz : goalSnap.Value;

        // Same layer with a clear straight shot is the trivial one-waypoint case. Everything else runs
        // the A* search, which also carries cross-layer routing over the link edges.
        if (startLayer == goalLayer && HasLineOfSight(startGrid, startPoint.Value, goalPoint, agentRadius))
        {
            return new NavPath(NavPathStatus.Complete, new[] { new NavWaypoint(goalPoint, goalLayer) });
        }

        return RunAStar(startLayer, startPoint.Value, goalLayer, goalPoint, agentRadius, budget);
    }

    /// <summary>
    /// Runs 8-connected A* from the snapped start cell to the goal cell, both resolved from world XZ via
    /// <see cref="NavGrid.CellOf"/> on their own layers. Node ids span every layer of the space
    /// (<c>layerOffset[layer] + z * layer.Width + x</c>), so the <c>gScore</c>/<c>cameFrom</c> arrays,
    /// the closed set, and the link adjacency all index one flat space. Grid step costs are in meters:
    /// an orthogonal step is <see cref="NavGrid.CellSize"/>, a diagonal <see cref="NavGrid.CellSize"/> *
    /// sqrt(2). A diagonal step is taken only when both orthogonal companions are passable, blocking
    /// corner cuts. After the eight grid neighbors, each link out of the current node is expanded at its
    /// precomputed meters cost (a <see cref="NavLinkKind.Stair"/> the source layer's
    /// <see cref="NavGrid.CellSize"/>, a <see cref="NavLinkKind.Hop"/> the constructor's hop cost), skipped
    /// when the link's far endpoint is not passable for the agent radius on its own layer. The heuristic is the
    /// octile distance to the goal cell while the node is on the goal's layer, and zero otherwise
    /// (an admissible lower bound across a link, degrading the off-layer search to Dijkstra). Off the
    /// goal layer, the zero heuristic pins the start as the closest-approach minimum, so a cross-layer
    /// query that reaches the goal's layer but cannot reach the goal returns Unreachable rather than
    /// Partial (single-layer Partial behavior is unaffected). A popped node is final for the ascend-to-goal
    /// case the links model. The search stops when the goal is popped (<see cref="NavPathStatus.Complete"/>),
    /// the open set empties, or <paramref name="budget"/>'s <see cref="PathQueryBudget.MaxExpandedNodes"/>
    /// expansions are spent. On a non-goal stop it reconstructs to the closest-approach node (least
    /// heuristic among popped nodes, earliest on ties) as a <see cref="NavPathStatus.Partial"/> path, or
    /// <see cref="NavPath.Unreachable"/> when that closest node is still the start.
    /// </summary>
    NavPath RunAStar(
        int startLayer, Vector2 startPoint, int goalLayer, Vector2 goalPoint,
        float agentRadius, PathQueryBudget budget)
    {
        NavGrid startGrid = _space.Layers[startLayer];
        NavGrid goalGrid = _space.Layers[goalLayer];

        (int startX, int startZ) = startGrid.CellOf(startPoint.X, startPoint.Y);
        (int goalX, int goalZ) = goalGrid.CellOf(goalPoint.X, goalPoint.Y);

        int startId = _layerOffset[startLayer] + startZ * startGrid.Width + startX;

        var gScore = new float[_totalNodes];
        var cameFrom = new int[_totalNodes];
        var closed = new bool[_totalNodes];
        for (int i = 0; i < _totalNodes; i++)
        {
            gScore[i] = float.PositiveInfinity;
            cameFrom[i] = -1;
        }

        var open = new PriorityQueue<int, (float F, int Seq)>();
        int seq = 0;

        gScore[startId] = 0f;
        float startHeuristic = Heuristic(startLayer, startX, startZ, goalLayer, goalX, goalZ, goalGrid.CellSize);
        open.Enqueue(startId, (startHeuristic, seq++));

        int closestNode = startId;
        float closestHeuristic = startHeuristic;
        int expanded = 0;
        bool reachedGoal = false;

        while (open.Count > 0)
        {
            if (expanded >= budget.MaxExpandedNodes)
            {
                break;
            }

            int current = open.Dequeue();
            if (closed[current])
            {
                continue; // A stale duplicate left behind by an earlier relaxation.
            }

            closed[current] = true;
            expanded++;

            (int layer, int cx, int cz) = Decode(current);
            NavGrid grid = _space.Layers[layer];
            int width = grid.Width;
            int baseOffset = _layerOffset[layer];

            float heuristic = Heuristic(layer, cx, cz, goalLayer, goalX, goalZ, goalGrid.CellSize);
            if (heuristic < closestHeuristic)
            {
                closestHeuristic = heuristic;
                closestNode = current;
            }

            if (layer == goalLayer && cx == goalX && cz == goalZ)
            {
                // Reconstruct from the goal itself. Its zero heuristic makes it the closest node in the
                // single-layer case, but off the goal layer the start also scores zero, so the strict
                // less-than never lets the goal displace it. Pin it here so the target is unambiguous.
                reachedGoal = true;
                closestNode = current;
                break;
            }

            float gCurrent = gScore[current];
            for (int i = 0; i < NeighborDx.Length; i++)
            {
                int nx = cx + NeighborDx[i];
                int nz = cz + NeighborDz[i];
                if (!grid.InBounds(nx, nz) || Blocks(grid, agentRadius, nx, nz))
                {
                    continue;
                }

                bool diagonal = i >= 4;
                if (diagonal &&
                    (Blocks(grid, agentRadius, nx, cz) || Blocks(grid, agentRadius, cx, nz)))
                {
                    continue; // Corner-cut prevention: both orthogonal companions must be passable.
                }

                int neighborId = baseOffset + nz * width + nx;
                if (closed[neighborId])
                {
                    continue;
                }

                float tentative = gCurrent + (diagonal ? grid.CellSize * Sqrt2 : grid.CellSize);
                if (tentative < gScore[neighborId])
                {
                    gScore[neighborId] = tentative;
                    cameFrom[neighborId] = current;
                    float f = tentative + Heuristic(layer, nx, nz, goalLayer, goalX, goalZ, goalGrid.CellSize);
                    open.Enqueue(neighborId, (f, seq++));
                }
            }

            // Cross-layer (and same-grid hop) edges: each link out of this cell carries its own precomputed
            // meters cost (a Stair one cell of the source layer, a Hop the knob's multiple). The source
            // endpoint is passable by construction (it is a reached search node). The far endpoint must
            // still fit the agent on its own layer, else the link is not traversable.
            if (_linkEdges.TryGetValue(current, out List<(int ToId, float CostMeters)>? links))
            {
                foreach ((int targetId, float costMeters) in links)
                {
                    if (closed[targetId])
                    {
                        continue;
                    }

                    (int tLayer, int tx, int tz) = Decode(targetId);
                    NavGrid targetGrid = _space.Layers[tLayer];
                    if (Blocks(targetGrid, agentRadius, tx, tz))
                    {
                        continue;
                    }

                    float tentative = gCurrent + costMeters;
                    if (tentative < gScore[targetId])
                    {
                        gScore[targetId] = tentative;
                        cameFrom[targetId] = current;
                        float f = tentative + Heuristic(tLayer, tx, tz, goalLayer, goalX, goalZ, goalGrid.CellSize);
                        open.Enqueue(targetId, (f, seq++));
                    }
                }
            }
        }

        // On a completed path the goal was popped last, so its zero heuristic made it the closest node.
        if (!reachedGoal && closestNode == startId)
        {
            return NavPath.Unreachable;
        }

        return Reconstruct(cameFrom, closestNode, reachedGoal, goalPoint, agentRadius);
    }

    /// <summary>
    /// Walks <paramref name="cameFrom"/> back from <paramref name="target"/> to the start, then
    /// string-pulls the chain into world-space waypoints. The chain is split into same-layer runs at
    /// every link crossing (any edge that is not an in-layer 8-neighbor step). Within a run the pull is
    /// greedy: the anchor starts at the run's first cell, and each step keeps the farthest later cell
    /// still in clear <see cref="HasLineOfSight"/> from the anchor, emits it, and re-anchors there. A
    /// run's first cell is never emitted by the pull itself, so the start cell is dropped (matching the
    /// line-of-sight fast path). Both link endpoints are always emitted: a run's last cell falls out of
    /// the pull, and the next run's first cell (the link's far endpoint) is emitted explicitly before
    /// its pull begins, keeping the pair adjacent and un-smoothed. When that boundary edge is a
    /// <see cref="NavLinkKind.Hop"/> (looked up in the hop-edge set by the two chain node ids), the emitted
    /// landing carries <see cref="NavWaypointKind.Hop"/>, a stair crossing emits a Walk landing. On a
    /// completed path the final waypoint is moved to <paramref name="goalPoint"/> to honor the exact-goal
    /// rule, preserving its kind so a hop landing that is also the goal stays a hop. A
    /// single-cell chain that reached the goal returns exactly that one exact-goal waypoint rather than
    /// indexing an empty list.
    /// </summary>
    NavPath Reconstruct(int[] cameFrom, int target, bool reachedGoal, Vector2 goalPoint, float agentRadius)
    {
        var chain = new List<int>();
        for (int node = target; node != -1; node = cameFrom[node])
        {
            chain.Add(node);
        }
        chain.Reverse();

        int count = chain.Count;
        var cells = new (int Layer, int Cx, int Cz)[count];
        for (int i = 0; i < count; i++)
        {
            cells[i] = Decode(chain[i]);
        }

        var waypoints = new List<NavWaypoint>();
        bool firstRun = true;
        int runStart = 0;
        while (runStart < count)
        {
            // A run is the maximal same-layer span of 8-neighbor steps starting at runStart. It ends
            // where the next edge is a link crossing (a different layer, or a same-layer jump wider
            // than one cell, which only a link can produce).
            int runEnd = runStart;
            while (runEnd + 1 < count && IsGridStep(cells[runEnd], cells[runEnd + 1]))
            {
                runEnd++;
            }

            int layer = cells[runStart].Layer;
            NavGrid grid = _space.Layers[layer];

            // A run after a link crossing opens with its first cell: the link's far endpoint, always a
            // waypoint so the crossing is never smoothed over. When that boundary edge is a hop, the landing
            // carries NavWaypointKind.Hop so the follower surfaces the jump. The boundary is the directed
            // edge the search traversed (cameFrom[chain[runStart]] == chain[runStart - 1]), so the hop set
            // lookup is by that exact directed node-id pair. A stair crossing emits a Walk landing as before.
            if (!firstRun)
            {
                Vector2 landing = grid.CellCenter(cells[runStart].Cx, cells[runStart].Cz);
                bool isHop = _hopEdges.Contains((chain[runStart - 1], chain[runStart]));
                waypoints.Add(new NavWaypoint(landing, layer)
                {
                    Kind = isHop ? NavWaypointKind.Hop : NavWaypointKind.Walk,
                });
            }

            int anchor = runStart;
            while (anchor < runEnd)
            {
                Vector2 anchorCenter = grid.CellCenter(cells[anchor].Cx, cells[anchor].Cz);
                int best = anchor + 1;
                for (int candidate = runEnd; candidate > anchor; candidate--)
                {
                    Vector2 candidateCenter = grid.CellCenter(cells[candidate].Cx, cells[candidate].Cz);
                    if (HasLineOfSight(grid, anchorCenter, candidateCenter, agentRadius))
                    {
                        best = candidate;
                        break;
                    }
                }

                waypoints.Add(new NavWaypoint(grid.CellCenter(cells[best].Cx, cells[best].Cz), layer));
                anchor = best;
            }

            firstRun = false;
            runStart = runEnd + 1;
        }

        if (reachedGoal)
        {
            if (waypoints.Count > 0)
            {
                // Move the final waypoint to the exact goal while keeping its Layer and Kind, so a hop
                // landing that is also the goal stays NavWaypointKind.Hop.
                waypoints[^1] = waypoints[^1] with { Position = goalPoint };
            }
            else
            {
                // Single-cell chain (start cell == goal cell): the pull emitted nothing, so the exact
                // goal is the whole path. Reachable only defensively today, the same-layer fast path
                // already returns a one-waypoint result for a zero-length query.
                waypoints.Add(new NavWaypoint(goalPoint, cells[^1].Layer));
            }
        }

        return new NavPath(reachedGoal ? NavPathStatus.Complete : NavPathStatus.Partial, waypoints);
    }

    /// <summary>True when the step from <paramref name="a"/> to <paramref name="b"/> is an in-layer
    /// 8-neighbor move (same layer, Chebyshev distance exactly one). Anything else in a reconstructed
    /// chain is a link crossing, since within a layer A* only ever steps to an adjacent cell. Same-layer
    /// links joining Chebyshev-adjacent cells are indistinguishable from grid steps and get smoothed like
    /// one. No shipped adapter emits such links (DungeonNav stairs always cross layers). An adapter that
    /// emits a same-layer diagonally adjacent link should prefer orthogonally adjacent cells, a smoothed
    /// diagonal link segment is corner-checked by the line walk on only one of the two companion cells,
    /// not both like a real diagonal grid step.</summary>
    static bool IsGridStep((int Layer, int Cx, int Cz) a, (int Layer, int Cx, int Cz) b)
    {
        if (a.Layer != b.Layer)
        {
            return false;
        }
        int dx = Math.Abs(a.Cx - b.Cx);
        int dz = Math.Abs(a.Cz - b.Cz);
        return dx <= 1 && dz <= 1 && (dx != 0 || dz != 0);
    }

    /// <summary>Decodes a flat node id into its layer index and grid cell (cx, cz), inverting the
    /// <c>layerOffset[layer] + z * width + x</c> addressing.</summary>
    (int Layer, int Cx, int Cz) Decode(int id)
    {
        int layer = _layerOffset.Length - 1;
        while (layer > 0 && id < _layerOffset[layer])
        {
            layer--;
        }
        int local = id - _layerOffset[layer];
        int width = _space.Layers[layer].Width;
        return (layer, local % width, local / width);
    }

    /// <summary>Octile distance to the goal cell while the node is on the goal's layer, zero otherwise.
    /// On the goal layer this heuristic is admissible only when all links move at most one cell in XZ
    /// (as in DungeonNav stair links). A link that jumps far in XZ for its flat one-cell cost, or an
    /// optimal path that leaves and re-enters the goal layer through such links, can cause the heuristic
    /// to overestimate, making A* return a valid but suboptimal Complete path. Zero off the goal layer is
    /// an admissible lower bound across a link (no octile estimate spans two coordinate frames), which
    /// keeps the popped-node-is-final guarantee for a search that ascends into the goal's layer and never
    /// leaves it, at the cost of a Dijkstra-like sweep before the crossing.</summary>
    static float Heuristic(int layer, int cx, int cz, int goalLayer, int goalX, int goalZ, float goalCellSize)
        => layer == goalLayer ? Octile(cx, cz, goalX, goalZ, goalCellSize) : 0f;

    /// <summary>Octile distance in meters from cell (<paramref name="x"/>, <paramref name="z"/>) to the
    /// goal cell (<paramref name="goalX"/>, <paramref name="goalZ"/>):
    /// <paramref name="cellSize"/> * (sqrt(2) * min + (max - min)) over the axis deltas.</summary>
    static float Octile(int x, int z, int goalX, int goalZ, float cellSize)
    {
        int adx = Math.Abs(x - goalX);
        int adz = Math.Abs(z - goalZ);
        int min = Math.Min(adx, adz);
        int max = Math.Max(adx, adz);
        return cellSize * (Sqrt2 * min + (max - min));
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
    static Vector2? SnapToPassable(NavGrid grid, Vector2 worldXz, float agentRadius, float snapRadius, out bool snappedToOwnCell)
    {
        (int queryX, int queryZ) = grid.CellOf(worldXz.X, worldXz.Y);
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

                    if (!grid.InBounds(x, z) || Blocks(grid, agentRadius, x, z))
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
    /// crosses no cell that <see cref="Blocks"/> for <paramref name="agentRadius"/>, via
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
