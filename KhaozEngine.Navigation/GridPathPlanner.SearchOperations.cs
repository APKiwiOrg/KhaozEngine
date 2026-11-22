using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    static int NeighborCount => NeighborDx.Length;

    /// <summary>Only after the ordered grid candidates, visit the node's links in stored order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    List<PlannerLink>? LinksAfterNeighbors(int current, int nextNeighbor)
        => nextNeighbor == NeighborCount && _linkEdges.TryGetValue(current, out List<PlannerLink>? links) ? links : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void RelaxNeighbor<TGoal>(SearchScratch scratch, PriorityQueue<int, (double F, int Seq)> open,
        ref TGoal goal, ref int sequence, int current, int layer, int x, int z, NavGrid grid, int width,
        int layerBase, double gCurrent, float radius, int index) where TGoal : struct, ISearchGoal
    {
        int nx = x + NeighborDx[index];
        int nz = z + NeighborDz[index];
        if (!grid.InBounds(nx, nz) || Blocks(grid, layer, radius, nx, nz) ||
            !CanWalkNeighbor(layer, x, z, nx, nz)) return;

        bool diagonal = index >= 4;
        if (diagonal && (Blocks(grid, layer, radius, nx, z) || Blocks(grid, layer, radius, x, nz)))
            return; // Both orthogonal companions must be passable.

        int id = layerBase + nz * width + nx;
        if (scratch.IsClosed(id)) return;
        double tentative = gCurrent + PlannerWalkingPrices.Cost(grid.CellSize, diagonal);
        if (RecordImprovement(scratch, id, tentative, current, null))
            Enqueue(open, ref goal, ref sequence, id, tentative, layer, nx, nz);
    }

    void RelaxLink<TGoal>(SearchScratch scratch, PriorityQueue<int, (double F, int Seq)> open,
        ref TGoal goal, ref int sequence, int current, int layer, int x, int z, double gCurrent,
        float radius, bool walkOnly, PlannerLink link) where TGoal : struct, ISearchGoal
    {
        (int target, float cost, NavLinkKind kind) = link;
        if (walkOnly && kind is NavLinkKind.Hop or NavLinkKind.Drop) return;
        if (scratch.IsClosed(target)) return;

        (int toLayer, int toX, int toZ) = Decode(target);
        if (Blocks(toLayer, radius, toX, toZ) || !CanTraverse(layer, x, z, toLayer, toX, toZ)) return;
        double tentative = gCurrent + cost;
        if (RecordImprovement(scratch, target, tentative, current, kind))
            Enqueue(open, ref goal, ref sequence, target, tentative, toLayer, toX, toZ);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool RecordImprovement(SearchScratch scratch, int id, double tentative, int current, NavLinkKind? kind)
    {
        if (!(tentative < scratch.GScore(id))) return false;
        scratch.Relax(id, tentative, current, kind);
        return true;
    }

    static void Enqueue<TGoal>(PriorityQueue<int, (double F, int Seq)> open, ref TGoal goal,
        ref int sequence, int id, double score, int layer, int x, int z) where TGoal : struct, ISearchGoal
    {
        double f = score + goal.Priority(layer, x, z);
        Enqueue(open, id, f, ref sequence);
    }

    static void SeedOpen<TGoal>(SearchScratch scratch, PriorityQueue<int, (double F, int Seq)> open,
        ref TGoal goal, ref int sequence, int startId, int layer, int x, int z) where TGoal : struct, ISearchGoal
    {
        scratch.Relax(startId, 0d, -1);
        // The seed uses the heuristic as-is, even when adding zero could change its sign bit.
        Enqueue(open, startId, goal.Priority(layer, x, z), ref sequence);
    }

    static void Enqueue(PriorityQueue<int, (double F, int Seq)> open, int id, double priority, ref int sequence)
        => open.Enqueue(id, (priority, sequence++));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool CanPop(PriorityQueue<int, (double F, int Seq)> open, int expanded, int maxExpanded)
        => open.Count > 0 && expanded < maxExpanded;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryClosePoppedNode(SearchScratch scratch, int current, ref int expanded)
    {
        if (scratch.IsClosed(current)) return false; // Stale duplicate left by an earlier relaxation.
        scratch.Close(current);
        expanded++;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool SelectPoppedEndpoint<TGoal>(ref TGoal goal, int current, int layer, int x, int z,
        double progress, ref int closest, ref double closestProgress) where TGoal : struct, ISearchGoal
    {
        if (progress < closestProgress)
        {
            closestProgress = progress;
            closest = current;
        }

        if (!goal.IsGoal(layer, x, z)) return false;
        closest = current; // Pin a popped success even if it ties the start's progress score.
        return true;
    }

    static bool ShouldReconstruct(bool reachedGoal, int closest, int startId) => reachedGoal || closest != startId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double DistanceSquared(Vector3 feet, Vector3 member)
    {
        double dx = (double)feet.X - member.X;
        double dy = (double)feet.Y - member.Y;
        double dz = (double)feet.Z - member.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double ReduceProgress(double closest, Vector3 feet, Vector3 member)
        => Math.Min(closest, DistanceSquared(feet, member));
}
