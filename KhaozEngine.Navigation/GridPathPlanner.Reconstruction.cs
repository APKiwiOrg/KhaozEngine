using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    /// <summary>Reconstructs the selected incoming edges. Guarded and region queries retain cell centers.
    /// Legacy point queries string-pull only ordinary runs. Drop source and landing anchors are mandatory,
    /// including the first edge. Exact-goal adjustment appends a Walk after a final Drop when needed.</summary>
    NavPath Reconstruct(SearchScratch scratch, int target, bool reachedGoal, Vector2? goalPoint, float agentRadius)
    {
        List<int> chain = scratch.Chain;
        chain.Clear();
        for (int node = target; node != -1;)
        {
            node = AppendParent(scratch, chain, node);
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

        return new NavPath(PathStatus(reachedGoal), waypoints.ToArray());
    }

    NavPath ReconstructTraversal(SearchScratch scratch, bool reachedGoal)
    {
        List<int> chain = scratch.Chain;
        List<NavWaypoint> waypoints = scratch.Waypoints;
        waypoints.Clear();
        int first = FirstRawWaypoint(scratch, chain);
        for (int i = first; i < chain.Count; i++)
        {
            waypoints.Add(DecodeRawWaypoint(scratch, chain, i));
        }
        return new NavPath(PathStatus(reachedGoal), waypoints.ToArray());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int AppendParent(SearchScratch scratch, List<int> chain, int node)
    {
        chain.Add(node);
        return scratch.CameFrom(node);
    }

    // A link on the first edge keeps its source as well as the landing, including a first-edge Drop.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int FirstRawWaypoint(SearchScratch scratch, List<int> chain)
        => chain.Count > 1 && scratch.Incoming(chain[1]) is null ? 1 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    NavWaypoint DecodeRawWaypoint(SearchScratch scratch, List<int> chain, int index)
    {
        (int layer, int x, int z) = Decode(chain[index]);
        NavWaypointKind kind = index > 0 ? WaypointKind(scratch.Incoming(chain[index])) : NavWaypointKind.Walk;
        return new NavWaypoint(_space.Layers[layer].CellCenter(x, z), layer) { Kind = kind };
    }

    static NavPathStatus PathStatus(bool reachedGoal) => reachedGoal ? NavPathStatus.Complete : NavPathStatus.Partial;
}
