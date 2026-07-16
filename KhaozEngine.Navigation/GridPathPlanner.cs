using System;
using System.Numerics;
using KhaozEngine.Collision;

namespace KhaozEngine.Navigation;

/// <summary>
/// Grid A* implementation of <see cref="IPathPlanner"/> over a <see cref="NavSpace"/>. This task ships
/// only the pieces every later search needs: endpoint snapping onto a passable cell, and a
/// line-of-sight fast path that skips the search entirely when the goal is directly visible. When the
/// start and goal resolve to different layers, or no clear line exists between them on the same layer,
/// this returns <see cref="NavPath.Unreachable"/>. The grid A* search that replaces that fallback
/// lands in the next task, reusing the same snapped endpoints and <see cref="Blocks"/> predicate built
/// here.
/// </summary>
public sealed class GridPathPlanner : IPathPlanner
{
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

        if (startLayer == goalLayer && HasLineOfSight(startGrid, startPoint.Value, goalPoint, agentRadius))
        {
            return new NavPath(NavPathStatus.Complete, new[] { new NavWaypoint(goalPoint, goalLayer) });
        }

        // A* lands in the next task.
        return NavPath.Unreachable;
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
