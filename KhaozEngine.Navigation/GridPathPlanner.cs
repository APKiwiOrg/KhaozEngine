using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;

namespace KhaozEngine.Navigation;

/// <summary>
/// Grid A* implementation of <see cref="IPathPlanner"/> over a <see cref="NavSpace"/>. A query snaps
/// both endpoints onto a passable cell, then takes a line-of-sight fast path when the goal is directly
/// visible on the start's layer, otherwise runs an 8-connected A* search across that layer. The search
/// prevents diagonal corner-cutting (a diagonal step needs both orthogonal companions passable), caps
/// its work at <see cref="PathQueryBudget.MaxExpandedNodes"/> expansions, and on an unreachable goal
/// returns a <see cref="NavPathStatus.Partial"/> route to the closest node it reached (or
/// <see cref="NavPath.Unreachable"/> when it never got past the start). Waypoints are raw cell centers
/// (string-pulling arrives later), with the final waypoint following the exact-goal rule from snapping.
/// Node addressing already spans every layer (see <see cref="RunAStar"/>), so cross-layer routing over
/// <see cref="NavSpace.Links"/> can be added without restructuring. Until then a query whose start and
/// goal resolve to different layers returns <see cref="NavPath.Unreachable"/>. Deterministic: fixed
/// neighbor order and a monotone insertion counter break every tie the same way.
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

    /// <summary>Builds a planner that searches <paramref name="space"/>.</summary>
    public GridPathPlanner(NavSpace space)
    {
        _space = space ?? throw new ArgumentNullException(nameof(space));
    }

    /// <summary>
    /// Finds a route from <paramref name="start"/> to <paramref name="goal"/> for an agent of
    /// <paramref name="agentRadius"/>, within <paramref name="budget"/>. Resolves each endpoint's
    /// layer from its world Y via <see cref="NavSpace.LayerOf"/>, snaps both onto a passable cell
    /// within <see cref="PathQueryBudget.SnapRadius"/> (failing that, returns
    /// <see cref="NavPath.Unreachable"/>), then tries the line-of-sight fast path described on
    /// <see cref="GridPathPlanner"/>.
    /// </summary>
    public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget)
    {
        int startLayer = _space.LayerOf(start.Y);
        int goalLayer = _space.LayerOf(goal.Y);

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

        if (startLayer == goalLayer)
        {
            if (HasLineOfSight(startGrid, startPoint.Value, goalPoint, agentRadius))
            {
                return new NavPath(NavPathStatus.Complete, new[] { new NavWaypoint(goalPoint, goalLayer) });
            }

            return RunAStar(startGrid, startLayer, startPoint.Value, goalPoint, agentRadius, budget);
        }

        // Cross-layer routing rides the NavSpace links, which the search does not yet follow, so a query
        // whose endpoints land on different layers is unreachable for now. The node addressing below is
        // already sized across every layer so those link edges can be added without restructuring.
        return NavPath.Unreachable;
    }

    /// <summary>
    /// Runs 8-connected A* across a single layer from the snapped start cell to the goal cell, both
    /// resolved from world XZ via <see cref="NavGrid.CellOf"/>. Node ids span every layer of the space
    /// (<c>layerOffset[layer] + z * grid.Width + x</c>, where <c>layerOffset</c> is the running sum of
    /// each earlier layer's cell count), so the <c>gScore</c>/<c>cameFrom</c> arrays and the open queue
    /// are already laid out for the cross-layer edges a later task adds. Costs are in meters:
    /// an orthogonal step is <see cref="NavGrid.CellSize"/>, a diagonal <see cref="NavGrid.CellSize"/> *
    /// sqrt(2), and the heuristic is octile distance to the goal cell (admissible and consistent for
    /// this cost model, so a popped node is final). A diagonal step is taken only when both orthogonal
    /// companions are passable, blocking corner cuts. The search stops when the goal is popped
    /// (<see cref="NavPathStatus.Complete"/>), the open set empties, or
    /// <paramref name="budget"/>'s <see cref="PathQueryBudget.MaxExpandedNodes"/> expansions are spent.
    /// On a non-goal stop it reconstructs to the closest-approach node (least heuristic among popped
    /// nodes, earliest on ties) as a <see cref="NavPathStatus.Partial"/> path, or
    /// <see cref="NavPath.Unreachable"/> when that closest node is still the start. The final waypoint of
    /// a completed path uses <paramref name="goalPoint"/> directly, which already carries the exact-goal
    /// rule from snapping.
    /// </summary>
    NavPath RunAStar(
        NavGrid grid, int layer, Vector2 startPoint, Vector2 goalPoint,
        float agentRadius, PathQueryBudget budget)
    {
        IReadOnlyList<NavGrid> layers = _space.Layers;
        var layerOffset = new int[layers.Count];
        int totalNodes = 0;
        for (int i = 0; i < layers.Count; i++)
        {
            layerOffset[i] = totalNodes;
            totalNodes += layers[i].Width * layers[i].Height;
        }

        int width = grid.Width;
        int baseOffset = layerOffset[layer];

        (int startX, int startZ) = grid.CellOf(startPoint.X, startPoint.Y);
        (int goalX, int goalZ) = grid.CellOf(goalPoint.X, goalPoint.Y);

        int startId = baseOffset + startZ * width + startX;

        var gScore = new float[totalNodes];
        var cameFrom = new int[totalNodes];
        var closed = new bool[totalNodes];
        for (int i = 0; i < totalNodes; i++)
        {
            gScore[i] = float.PositiveInfinity;
            cameFrom[i] = -1;
        }

        var open = new PriorityQueue<int, (float F, int Seq)>();
        int seq = 0;

        gScore[startId] = 0f;
        float startHeuristic = Octile(startX, startZ, goalX, goalZ, grid.CellSize);
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

            int local = current - baseOffset;
            int cx = local % width;
            int cz = local / width;

            float heuristic = Octile(cx, cz, goalX, goalZ, grid.CellSize);
            if (heuristic < closestHeuristic)
            {
                closestHeuristic = heuristic;
                closestNode = current;
            }

            if (cx == goalX && cz == goalZ)
            {
                reachedGoal = true;
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
                    float f = tentative + Octile(nx, nz, goalX, goalZ, grid.CellSize);
                    open.Enqueue(neighborId, (f, seq++));
                }
            }
        }

        // On a completed path the goal was popped last, so its zero heuristic made it the closest node.
        if (!reachedGoal && closestNode == startId)
        {
            return NavPath.Unreachable;
        }

        return Reconstruct(grid, layer, baseOffset, width, cameFrom, closestNode, reachedGoal, goalPoint);
    }

    /// <summary>
    /// Walks <paramref name="cameFrom"/> back from <paramref name="target"/> to the start, then emits the
    /// chain (start excluded, matching the line-of-sight fast path) as world-space waypoints. Every
    /// intermediate node is its <see cref="NavGrid.CellCenter"/>. On a completed path the final waypoint
    /// is replaced with <paramref name="goalPoint"/> to honor the exact-goal rule.
    /// </summary>
    static NavPath Reconstruct(
        NavGrid grid, int layer, int baseOffset, int width, int[] cameFrom, int target, bool reachedGoal,
        Vector2 goalPoint)
    {
        var chain = new List<int>();
        for (int node = target; node != -1; node = cameFrom[node])
        {
            chain.Add(node);
        }
        chain.Reverse();

        var waypoints = new NavWaypoint[chain.Count - 1];
        for (int i = 1; i < chain.Count; i++)
        {
            int local = chain[i] - baseOffset;
            int cx = local % width;
            int cz = local / width;
            waypoints[i - 1] = new NavWaypoint(grid.CellCenter(cx, cz), layer);
        }

        if (reachedGoal)
        {
            waypoints[^1] = new NavWaypoint(goalPoint, layer);
        }

        return new NavPath(reachedGoal ? NavPathStatus.Complete : NavPathStatus.Partial, waypoints);
    }

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
    /// Gotcha: <see cref="GridRay"/> assumes its own grid's origin is at (0, 0), with cell =
    /// floor(world / cellSize). That only lines up with <paramref name="grid"/>'s own cell indices
    /// when <paramref name="grid"/> was also baked at origin (0, 0). A <see cref="NavGrid"/> baked at
    /// a non-zero <see cref="NavGrid.OriginX"/>/<see cref="NavGrid.OriginZ"/> would otherwise be walked
    /// against the wrong cells entirely. Both points are translated into grid-local space
    /// (world - (OriginX, OriginZ)) before the call to correct for that.
    /// </remarks>
    static bool HasLineOfSight(NavGrid grid, Vector2 fromWorldXz, Vector2 toWorldXz, float agentRadius)
    {
        var origin = new Vector2(grid.OriginX, grid.OriginZ);
        Vector2 localFrom = fromWorldXz - origin;
        Vector2 localTo = toWorldXz - origin;

        return GridRay.IsClear(
            localFrom, localTo, grid.CellSize,
            (x, z) => Blocks(grid, agentRadius, x, z),
            includeEndpointCells: false);
    }

    /// <summary>True when an agent of <paramref name="agentRadius"/> does not fit at
    /// (<paramref name="cx"/>, <paramref name="cz"/>) in <paramref name="grid"/>. The shared blocked
    /// predicate for snapping, the line-of-sight fast path, and (from the next task) A* neighbor
    /// expansion.</summary>
    static bool Blocks(NavGrid grid, float agentRadius, int cx, int cz) => !grid.IsPassable(cx, cz, agentRadius);
}
