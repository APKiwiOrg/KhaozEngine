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

        public double Progress(int layer, int x, int z)
        {
            Vector3 feet = planner.CellFeet(layer, x, z);
            double dx = (double)feet.X - goalFeet.X;
            double dy = (double)feet.Y - goalFeet.Y;
            double dz = (double)feet.Z - goalFeet.Z;
            return SquaredDistance(dx, dy, dz);
        }
    }

    // Priority bounds remaining route cost. Progress only chooses a partial endpoint.
    NavPath RunSearch<TGoal>(SearchScratch scratch, int startLayer, Vector2 startPoint, TGoal goal,
        Vector2? goalPoint, float agentRadius, PathQueryBudget budget, bool useCertificate)
        where TGoal : struct, ISearchGoal
    {
        NavGrid startGrid = _space.Layers[startLayer];
        (int startX, int startZ) = startGrid.CellOf(startPoint.X, startPoint.Y);
        int startId = _layerOffset[startLayer] + startZ * startGrid.Width + startX;

        if (useCertificate && TryCertifyNoProgress(startLayer, startX, startZ, startId, goal))
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

        while (open.Count > 0)
        {
            if (ExpansionCapReached(expanded, budget.MaxExpandedNodes)) break;

            int current = open.Dequeue();
            if (!TryClosePoppedNode(scratch, current, ref expanded)) continue;

            (int layer, int cx, int cz) = Decode(current);
            NavGrid grid = _space.Layers[layer];
            int width = grid.Width;
            int baseOffset = _layerOffset[layer];

            double progress = goal.Progress(layer, cx, cz);
            if (SelectPoppedEndpoint(ref goal, current, layer, cx, cz, progress, ref closestNode, ref closestProgress))
            {
                reachedGoal = true;
                break;
            }

            double gCurrent = scratch.GScore(current);
            for (int i = 0; i < NeighborDx.Length; i++)
            {
                RelaxNeighbor(scratch, open, ref goal, ref seq, current, layer, cx, cz, grid, width,
                    baseOffset, gCurrent, agentRadius, i);
            }

            if (TryGetLinks(current, out List<PlannerLink>? links))
            {
                foreach (PlannerLink link in links)
                {
                    RelaxLink(scratch, open, ref goal, ref seq, current, layer, cx, cz, gCurrent,
                        agentRadius, budget.WalkOnly, link);
                }
            }
        }

        if (!ShouldReconstruct(reachedGoal, closestNode, startId)) return NavPath.Unreachable;
        return Reconstruct(scratch, closestNode, reachedGoal, goalPoint, agentRadius);
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
