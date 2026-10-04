// Frozen copy of the v20.24.0 GridPathPlanner algorithm (KhaozEngine#1288). The equality corpus compares the
// production planner against it, so this file must never change with the production planner.
using System;
using System.Collections.Generic;
using System.Numerics;

using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Navigation;

internal sealed partial class ReferenceGridPathPlanner
{
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
        NavGrid goalGrid = _space.Layers[goalLayer];
        (int goalX, int goalZ) = goalGrid.CellOf(goalPoint.X, goalPoint.Y);
        float PointHeuristic(int layer, int x, int z)
            => Heuristic(layer, x, z, goalLayer, goalX, goalZ, goalGrid.CellSize);
        double PointProgress(int layer, int x, int z) => PointHeuristic(layer, x, z);
        return RunSearch(startLayer, startPoint,
            (layer, x, z) => layer == goalLayer && x == goalX && z == goalZ,
            PointHeuristic, PointProgress, goalPoint, agentRadius, budget);
    }

    // Priority must bound remaining route cost. Progress only chooses a partial endpoint, so region
    // queries can measure useful approach independently when a safe priority has to be zero.
    NavPath RunSearch(int startLayer, Vector2 startPoint,
        Func<int, int, int, bool> isGoal, Func<int, int, int, float> priority,
        Func<int, int, int, double> progress, Vector2? goalPoint, float agentRadius, PathQueryBudget budget)
    {
        NavGrid startGrid = _space.Layers[startLayer];
        (int startX, int startZ) = startGrid.CellOf(startPoint.X, startPoint.Y);
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
        float startHeuristic = priority(startLayer, startX, startZ);
        open.Enqueue(startId, (startHeuristic, seq++));

        int closestNode = startId;
        double closestHeuristic = progress(startLayer, startX, startZ);
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

            double heuristic = progress(layer, cx, cz);
            if (heuristic < closestHeuristic)
            {
                closestHeuristic = heuristic;
                closestNode = current;
            }

            if (isGoal(layer, cx, cz))
            {
                // Pin a popped success even when it ties the start's partial-progress score.
                reachedGoal = true;
                closestNode = current;
                break;
            }

            float gCurrent = gScore[current];
            for (int i = 0; i < NeighborDx.Length; i++)
            {
                int nx = cx + NeighborDx[i];
                int nz = cz + NeighborDz[i];
                if (!grid.InBounds(nx, nz) || Blocks(layer, agentRadius, nx, nz) ||
                    !CanTraverse(layer, cx, cz, layer, nx, nz))
                {
                    continue;
                }

                bool diagonal = i >= 4;
                if (diagonal &&
                    (Blocks(layer, agentRadius, nx, cz) || Blocks(layer, agentRadius, cx, nz)))
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
                    float f = tentative + priority(layer, nx, nz);
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
                    if (Blocks(tLayer, agentRadius, tx, tz) || !CanTraverse(layer, cx, cz, tLayer, tx, tz))
                    {
                        continue;
                    }

                    float tentative = gCurrent + costMeters;
                    if (tentative < gScore[targetId])
                    {
                        gScore[targetId] = tentative;
                        cameFrom[targetId] = current;
                        float f = tentative + priority(tLayer, tx, tz);
                        open.Enqueue(targetId, (f, seq++));
                    }
                }
            }
        }

        // Success pins the endpoint. Otherwise retain the earliest strict progress improvement.
        if (!reachedGoal && closestNode == startId)
        {
            return NavPath.Unreachable;
        }

        return Reconstruct(cameFrom, closestNode, reachedGoal, goalPoint, agentRadius);
    }

    /// <summary>
    /// Walks <paramref name="cameFrom"/> back from <paramref name="target"/> to the start, then
    /// emits its validated cell edges for guarded and region queries. Legacy point queries
    /// string-pull the chain into world-space waypoints. The chain is split into same-layer runs at
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
    NavPath Reconstruct(int[] cameFrom, int target, bool reachedGoal, Vector2? goalPoint, float agentRadius)
    {
        var chain = new List<int>();
        for (int node = target; node != -1; node = cameFrom[node])
        {
            chain.Add(node);
        }
        chain.Reverse();
        if (_traversal is not null || goalPoint is null) return ReconstructTraversal(chain, reachedGoal);

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
                waypoints[^1] = waypoints[^1] with { Position = goalPoint.Value };
            }
            else
            {
                // Single-cell chain (start cell == goal cell): the pull emitted nothing, so the exact
                // goal is the whole path. Reachable only defensively today, the same-layer fast path
                // already returns a one-waypoint result for a zero-length query.
                waypoints.Add(new NavWaypoint(goalPoint.Value, cells[^1].Layer));
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

}
