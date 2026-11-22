using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

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
    /// precomputed meters cost (a <see cref="NavLinkKind.Stair"/> at least its spatial span and the source layer's
    /// <see cref="NavGrid.CellSize"/>, a <see cref="NavLinkKind.Hop"/> the constructor's hop cost), skipped
    /// when the link's far endpoint is not passable for the agent radius on its own layer. A construction-time
    /// spatial cost ratio scales one metric across every layer: octile in a shared frame, world XZ distance
    /// otherwise. The geometric bound is admissible and consistent in exact arithmetic. Double cost and
    /// priority errors follow the numeric contract in the Navigation README. Closed nodes are not reopened.
    /// Progress uses physical 3D feet distance independently of priority. The search stops
    /// when the goal is popped (<see cref="NavPathStatus.Complete"/>),
    /// the open set empties, or <paramref name="budget"/>'s <see cref="PathQueryBudget.MaxExpandedNodes"/>
    /// expansions are spent. On a non-goal stop it reconstructs to the closest-approach node (least
    /// physical distance among popped nodes, earliest on ties) as a <see cref="NavPathStatus.Partial"/> path, or
    /// <see cref="NavPath.Unreachable"/> when that closest node is still the start.
    /// </summary>
    NavPath RunAStar(SearchScratch scratch,
        int startLayer, Vector2 startPoint, int goalLayer, Vector2 goalPoint,
        float agentRadius, PathQueryBudget budget, bool useCertificate)
    {
        NavGrid goalGrid = _space.Layers[goalLayer];
        (int goalX, int goalZ) = goalGrid.CellOf(goalPoint.X, goalPoint.Y);
        return RunSearch(scratch, startLayer, startPoint,
            new PointGoal(this, goalLayer, goalX, goalZ, CellFeet(goalLayer, goalX, goalZ)),
            goalPoint, agentRadius, budget, useCertificate);
    }

    /// <summary>What one search is looking for. A struct keeps goal calls free of delegate allocations.</summary>
    interface ISearchGoal
    {
        bool IsGoal(int layer, int x, int z);
        int MemberCount { get; }
        bool IsDisconnected(PlannerConnectivity connectivity, int component);

        /// <summary>Lower is closer. Only chooses a partial endpoint.</summary>
        double Progress(int layer, int x, int z);

        /// <summary>Added to the g-score for the open-set order. Bounds the remaining route cost.</summary>
        double Priority(int layer, int x, int z);
    }

    /// <summary>A point query's goal cell, ordered by the planner's cached spatial cost bound.</summary>
    readonly struct PointGoal(GridPathPlanner planner, int goalLayer, int goalX, int goalZ,
        Vector3 goalFeet) : ISearchGoal
    {
        public bool IsGoal(int layer, int x, int z) => layer == goalLayer && x == goalX && z == goalZ;
        public int MemberCount => 1;

        bool ISearchGoal.IsDisconnected(PlannerConnectivity connectivity, int component)
        {
            NavGrid grid = planner._space.Layers[goalLayer];
            return grid.InBounds(goalX, goalZ) &&
                CertificateMemberIsDisconnected(connectivity, component,
                    planner._layerOffset[goalLayer] + goalZ * grid.Width + goalX);
        }

        public double Priority(int layer, int x, int z)
            => planner._spatialBound.Point(layer, x, z, goalLayer, goalX, goalZ);

        double ISearchGoal.Progress(int layer, int x, int z)
            => DistanceSquared(planner.CellFeet(layer, x, z), goalFeet);
    }

    // Priority bounds remaining route cost. Progress only chooses a partial endpoint.
    NavPath RunSearch<TGoal>(SearchScratch scratch, int startLayer, Vector2 startPoint, TGoal goal,
        Vector2? goalPoint, float agentRadius, PathQueryBudget budget, bool useCertificate)
        where TGoal : struct, ISearchGoal
    {
        NavGrid startGrid = _space.Layers[startLayer];
        (int startX, int startZ) = startGrid.CellOf(startPoint.X, startPoint.Y);
        int startId = _layerOffset[startLayer] + startZ * startGrid.Width + startX;

        if (useCertificate && TryCertifyNoProgress(startLayer, startX, startZ, startId, ref goal))
        {
            Interlocked.Increment(ref _certificatesForTests);
            return NavPath.Unreachable;
        }

        Interlocked.Increment(ref _searchesForTests);
        scratch.BeginSynchronousSearch();
        PriorityQueue<int, (double F, int Seq)> open = scratch.Open;
        int seq = 0;
        SeedOpen(scratch, open, ref goal, ref seq, startId, startLayer, startX, startZ);

        int closestNode = startId;
        double closestProgress = goal.Progress(startLayer, startX, startZ);
        int expanded = 0;
        bool reachedGoal = false;

        while (CanPop(open, expanded, budget.MaxExpandedNodes))
        {
            int current = open.Dequeue();
            if (!TryClosePoppedNode(scratch, current, ref expanded)) continue;

            (int layer, int cx, int cz) = Decode(current);
            NavGrid grid = _space.Layers[layer];
            int width = grid.Width;
            int baseOffset = _layerOffset[layer];

            double progress = goal.Progress(layer, cx, cz);
            reachedGoal = SelectPoppedEndpoint(ref goal, current, layer, cx, cz, progress, ref closestNode, ref closestProgress);
            if (reachedGoal) break;

            double gCurrent = scratch.GScore(current);
            int neighbor = 0;
            for (; neighbor < NeighborCount; neighbor++)
                RelaxNeighbor(scratch, open, ref goal, ref seq, current, layer, cx, cz, grid, width,
                    baseOffset, gCurrent, agentRadius, neighbor);

            if (LinksAfterNeighbors(current, neighbor) is { } links)
                foreach (PlannerLink link in links)
                    RelaxLink(scratch, open, ref goal, ref seq, current, layer, cx, cz, gCurrent,
                        agentRadius, budget.WalkOnly, link);
        }

        if (!ShouldReconstruct(reachedGoal, closestNode, startId)) return NavPath.Unreachable;
        return Reconstruct(scratch, closestNode, reachedGoal, goalPoint, agentRadius);
    }

    /// <summary>Pulls a reversed-to-travel-order chain for legacy point queries. Special links split runs.
    /// Drop source and landing anchors are mandatory, including on the first edge.</summary>
    NavPath PullPointPath(SearchScratch scratch, bool reachedGoal, Vector2 goalPoint, float agentRadius)
    {
        List<int> chain = scratch.Chain;
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
                waypoints.Add(WaypointFromIncoming(landing, layer, scratch.Incoming(chain[runStart])));
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
                    if (waypoints[^1].Position != goalPoint)
                        waypoints.Add(new NavWaypoint(goalPoint, waypoints[^1].Layer));
                }
                else waypoints[^1] = waypoints[^1] with { Position = goalPoint };
            }
            else
            {
                // Single-cell chain (start cell == goal cell): the pull emitted nothing, so the exact
                // goal is the whole path. Reachable only defensively today, the same-layer fast path
                // already returns a one-waypoint result for a zero-length query.
                waypoints.Add(new NavWaypoint(goalPoint, cells[count - 1].Layer));
            }
        }

        return new NavPath(PathStatus(reachedGoal), waypoints.ToArray());
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

}
