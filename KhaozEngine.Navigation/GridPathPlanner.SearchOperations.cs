using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    static int NeighborCount => NeighborDx.Length;

    /// <summary>Looks up the stored links for a node. Both drivers examine the eight grid candidates
    /// in offset order, then the node's links in stored order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool TryGetLinks(int current, [NotNullWhen(true)] out List<PlannerLink>? links)
        => _linkEdges.TryGetValue(current, out links);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void RelaxNeighbor<TGoal>(SearchScratch scratch, PriorityQueue<int, (double F, int Seq)> open,
        ref TGoal goal, ref int sequence, int current, int layer, int x, int z, NavGrid grid, int width,
        int layerBase, double gCurrent, float radius, int index) where TGoal : struct, ISearchGoal
    {
        int nx = x + NeighborDx[index];
        int nz = z + NeighborDz[index];
        if (!grid.InBounds(nx, nz) || Blocks(layer, radius, nx, nz) ||
            !CanWalkNeighbor(layer, x, z, nx, nz)) return;

        bool diagonal = index >= 4;
        if (diagonal && (Blocks(layer, radius, nx, z) || Blocks(layer, radius, x, nz)))
            return; // Both orthogonal companions must be passable.

        int id = layerBase + nz * width + nx;
        if (scratch.IsClosed(id)) return;
        double tentative = gCurrent + PlannerWalkingPrices.Cost(grid.CellSize, diagonal);
        if (RecordImprovement(scratch, id, tentative, current, null))
            Enqueue(open, ref goal, ref sequence, id, tentative, layer, nx, nz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Enqueue<TGoal>(PriorityQueue<int, (double F, int Seq)> open, ref TGoal goal,
        ref int sequence, int id, double score, int layer, int x, int z) where TGoal : struct, ISearchGoal
    {
        double f = score + goal.Priority(layer, x, z);
        open.Enqueue(id, OrderedPriority(f, ref sequence));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void SeedOpen<TGoal>(SearchScratch scratch, PriorityQueue<int, (double F, int Seq)> open,
        ref TGoal goal, ref int sequence, int startId, int layer, int x, int z) where TGoal : struct, ISearchGoal
    {
        scratch.Relax(startId, 0d, -1);
        // The seed uses the heuristic as-is, even when adding zero could change its sign bit.
        double f = goal.Priority(layer, x, z);
        open.Enqueue(startId, OrderedPriority(f, ref sequence));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static (double F, int Seq) OrderedPriority(double priority, ref int sequence)
        => (priority, sequence++);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExpansionCapReached(int expanded, int maxExpanded) => expanded >= maxExpanded;

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
        return SquaredDistance(dx, dy, dz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double SquaredDistance(double dx, double dy, double dz) => dx * dx + dy * dy + dz * dz;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double ReduceProgress(double closest, Vector3 feet, Vector3 member)
        => Math.Min(closest, DistanceSquared(feet, member));
}
