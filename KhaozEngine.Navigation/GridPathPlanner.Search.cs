using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    /// <summary>
    /// Runs 8-connected A* from the snapped start cell to the goal cell, both resolved from world XZ via
    /// <see cref="NavGrid.CellOf"/> on their own layers. Node ids span every layer of the space
    /// (<c>layerOffset[layer] + z * layer.Width + x</c>), so the scratch's g-score, parent and closed
    /// stamps and the link adjacency all index one flat space. Grid step costs are in meters:
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
    NavPath RunAStar(SearchScratch scratch,
        int startLayer, Vector2 startPoint, int goalLayer, Vector2 goalPoint,
        float agentRadius, PathQueryBudget budget)
    {
        NavGrid goalGrid = _space.Layers[goalLayer];
        (int goalX, int goalZ) = goalGrid.CellOf(goalPoint.X, goalPoint.Y);
        return RunSearch(scratch, startLayer, startPoint,
            new PointGoal(goalLayer, goalX, goalZ, goalGrid.CellSize, !_hasDrops || PointLinksPreserveOctile()), goalPoint, agentRadius, budget);
    }

    /// <summary>What one search is looking for. A struct implementation keeps the per-node calls free of
    /// delegates and their closure allocations.</summary>
    interface ISearchGoal
    {
        bool IsGoal(int layer, int x, int z);

        /// <summary>Added to the g-score for the open-set order. Must bound the remaining route cost.</summary>
        float Priority(int layer, int x, int z);

        /// <summary>Lower is closer. Only chooses a partial endpoint.</summary>
        double Progress(int layer, int x, int z);
    }

    /// <summary>A point query's goal cell, ordered by <see cref="Heuristic"/>.</summary>
    readonly struct PointGoal(int goalLayer, int goalX, int goalZ, float goalCellSize, bool spatialBound) : ISearchGoal
    {
        public bool IsGoal(int layer, int x, int z) => layer == goalLayer && x == goalX && z == goalZ;

        public float Priority(int layer, int x, int z)
            => spatialBound ? Heuristic(layer, x, z, goalLayer, goalX, goalZ, goalCellSize) : 0f;

        public double Progress(int layer, int x, int z) => Heuristic(layer, x, z, goalLayer, goalX, goalZ, goalCellSize);
    }

    // Priority must bound remaining route cost. Progress only chooses a partial endpoint, so region
    // queries can measure useful approach independently when a safe priority has to be zero.
    NavPath RunSearch<TGoal>(SearchScratch scratch, int startLayer, Vector2 startPoint, TGoal goal,
        Vector2? goalPoint, float agentRadius, PathQueryBudget budget)
        where TGoal : struct, ISearchGoal
    {
        NavGrid startGrid = _space.Layers[startLayer];
        (int startX, int startZ) = startGrid.CellOf(startPoint.X, startPoint.Y);
        int startId = _layerOffset[startLayer] + startZ * startGrid.Width + startX;

        scratch.BeginSearch();
        PriorityQueue<int, (float F, int Seq)> open = scratch.Open;
        int seq = 0;

        scratch.Relax(startId, 0f, -1);
        float startHeuristic = goal.Priority(startLayer, startX, startZ);
        open.Enqueue(startId, (startHeuristic, seq++));

        int closestNode = startId;
        double closestHeuristic = goal.Progress(startLayer, startX, startZ);
        int expanded = 0;
        bool reachedGoal = false;

        while (open.Count > 0)
        {
            if (expanded >= budget.MaxExpandedNodes)
            {
                break;
            }

            int current = open.Dequeue();
            if (scratch.IsClosed(current))
            {
                continue; // A stale duplicate left behind by an earlier relaxation.
            }

            scratch.Close(current);
            expanded++;

            (int layer, int cx, int cz) = Decode(current);
            NavGrid grid = _space.Layers[layer];
            int width = grid.Width;
            int baseOffset = _layerOffset[layer];

            double heuristic = goal.Progress(layer, cx, cz);
            if (heuristic < closestHeuristic)
            {
                closestHeuristic = heuristic;
                closestNode = current;
            }

            if (goal.IsGoal(layer, cx, cz))
            {
                // Pin a popped success even when it ties the start's partial-progress score.
                reachedGoal = true;
                closestNode = current;
                break;
            }

            float gCurrent = scratch.GScore(current);
            for (int i = 0; i < NeighborDx.Length; i++)
            {
                int nx = cx + NeighborDx[i];
                int nz = cz + NeighborDz[i];
                if (!grid.InBounds(nx, nz) || Blocks(layer, agentRadius, nx, nz) ||
                    !(_traversal?.CanWalkNeighbor(layer, cx, cz, nx, nz) ?? true))
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
                if (scratch.IsClosed(neighborId))
                {
                    continue;
                }

                float tentative = gCurrent + (diagonal ? grid.CellSize * Sqrt2 : grid.CellSize);
                if (tentative < scratch.GScore(neighborId))
                {
                    scratch.Relax(neighborId, tentative, current);
                    float f = tentative + goal.Priority(layer, nx, nz);
                    open.Enqueue(neighborId, (f, seq++));
                }
            }

            // Cross-layer (and same-grid hop) edges: each link out of this cell carries its own precomputed
            // meters cost (a Stair one cell of the source layer, a Hop the knob's multiple). The source
            // endpoint is passable by construction (it is a reached search node). The far endpoint must
            // still fit the agent on its own layer, else the link is not traversable.
            if (_linkEdges.TryGetValue(current, out List<PlannerLink>? links))
            {
                foreach ((int targetId, float costMeters, NavLinkKind kind) in links)
                {
                    if (scratch.IsClosed(targetId))
                    {
                        continue;
                    }

                    (int tLayer, int tx, int tz) = Decode(targetId);
                    if (Blocks(tLayer, agentRadius, tx, tz) || !CanTraverse(layer, cx, cz, tLayer, tx, tz))
                    {
                        continue;
                    }

                    float tentative = gCurrent + costMeters;
                    if (tentative < scratch.GScore(targetId))
                    {
                        scratch.Relax(targetId, tentative, current, kind);
                        float f = tentative + goal.Priority(tLayer, tx, tz);
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

        return Reconstruct(scratch, closestNode, reachedGoal, goalPoint, agentRadius);
    }

    /// <summary>Reconstructs the selected incoming edges. Guarded and region queries retain cell centers.
    /// Legacy point queries string-pull only ordinary runs. Drop source and landing anchors are mandatory,
    /// including the first edge. Exact-goal adjustment appends a Walk after a final Drop when needed.</summary>
    NavPath Reconstruct(SearchScratch scratch, int target, bool reachedGoal, Vector2? goalPoint, float agentRadius)
    {
        List<int> chain = scratch.Chain;
        chain.Clear();
        for (int node = target; node != -1; node = scratch.CameFrom(node))
        {
            chain.Add(node);
        }
        chain.Reverse();
        if (_traversal is not null || goalPoint is null) return ReconstructTraversal(scratch, reachedGoal);

        int count = chain.Count;
        (int Layer, int Cx, int Cz)[] cells = scratch.CellsFor(count);
        for (int i = 0; i < count; i++)
        {
            cells[i] = Decode(chain[i]);
        }

        List<NavWaypoint> waypoints = scratch.Waypoints;
        waypoints.Clear();
        bool firstRun = true;
        int runStart = 0;
        while (runStart < count)
        {
            // Runs contain ordinary selected exits only. An adjacent special link is still a boundary.
            int runEnd = runStart;
            while (runEnd + 1 < count && scratch.Incoming(chain[runEnd + 1]) is null)
            {
                runEnd++;
            }

            int layer = cells[runStart].Layer;
            NavGrid grid = _space.Layers[layer];

            // A selected link starts a new run. Its actual incoming kind determines the landing marker.
            if (!firstRun)
            {
                Vector2 landing = grid.CellCenter(cells[runStart].Cx, cells[runStart].Cz);
                NavWaypointKind kind = WaypointKind(scratch.Incoming(chain[runStart]));
                waypoints.Add(new NavWaypoint(landing, layer)
                {
                    Kind = kind,
                });
            }

            if (firstRun && runEnd == runStart && count > 1 && scratch.Incoming(chain[1]) == NavLinkKind.Drop)
                waypoints.Add(new NavWaypoint(grid.CellCenter(cells[runStart].Cx, cells[runStart].Cz), layer));

            int anchor = runStart;
            while (anchor < runEnd)
            {
                Vector2 anchorCenter = grid.CellCenter(cells[anchor].Cx, cells[anchor].Cz);
                int best = anchor + 1;
                for (int candidate = runEnd; candidate > anchor; candidate--)
                {
                    Vector2 candidateCenter = grid.CellCenter(cells[candidate].Cx, cells[candidate].Cz);
                    if (scratch.HasLineOfSight(grid, anchorCenter, candidateCenter, agentRadius))
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
                if (waypoints[^1].Kind == NavWaypointKind.Drop)
                {
                    if (waypoints[^1].Position != goalPoint.Value)
                        waypoints.Add(new NavWaypoint(goalPoint.Value, waypoints[^1].Layer));
                }
                else waypoints[^1] = waypoints[^1] with { Position = goalPoint.Value };
            }
            else
            {
                // Single-cell chain (start cell == goal cell): the pull emitted nothing, so the exact
                // goal is the whole path. Reachable only defensively today, the same-layer fast path
                // already returns a one-waypoint result for a zero-length query.
                waypoints.Add(new NavWaypoint(goalPoint.Value, cells[count - 1].Layer));
            }
        }

        return new NavPath(reachedGoal ? NavPathStatus.Complete : NavPathStatus.Partial, waypoints.ToArray());
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
