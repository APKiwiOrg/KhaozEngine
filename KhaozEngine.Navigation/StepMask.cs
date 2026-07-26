using System;
using System.Collections.Generic;

namespace KhaozEngine.Navigation;

/// <summary>
/// Step-reachability plus headroom pass. Turns per-cell surface data (standable flag, surface height,
/// headroom) into the boolean blocked mask the clearance transform consumes, folding the
/// rise-within-StepHeight rule into which cells are passable so the planner needs no per-edge logic.
/// A cell is blocked when it is not standable, its headroom is below the agent height, or its surface
/// drops to any standable 8-neighbor by more than the step height (the higher side of every too-tall
/// step bakes blocked, which provably stops the grid planner from crossing it). One carve-out keeps a
/// lone standable top alive for a jump: a cell that every standable neighbor sits more than a step below,
/// and whose whole 8-neighborhood is blocked, survives instead of eroding, so
/// <see cref="NavHopLinks"/> can still link it. Surviving hands the planner no walk edge, because every
/// neighbor of such a cell is blocked, so it stays reachable only across a link. Deterministic: fixed scan
/// and neighbor order, float compares only.
/// </summary>
internal static class StepMask
{
    // Fixed neighbor order: N, S, E, W, then the four diagonals NE, NW, SE, SW.
    static readonly (int Dx, int Dz)[] Neighbors =
    {
        (0, -1), (0, 1), (1, 0), (-1, 0),
        (1, -1), (-1, -1), (1, 1), (-1, 1),
    };

    /// <summary>
    /// Computes the blocked mask over a <paramref name="width"/> by <paramref name="height"/> grid.
    /// <paramref name="standable"/>, <paramref name="surfaceHeight"/>, and <paramref name="headroom"/>
    /// are row-major, length <c>width * height</c> (cell (cx, cz) at <c>cz * width + cx</c>).
    /// <paramref name="surfaceHeight"/> and <paramref name="headroom"/> are read only for standable
    /// cells. A standable cell every standable 8-neighbor sits more than <paramref name="stepHeight"/>
    /// below, with a fully blocked 8-neighborhood, is kept passable rather than eroded (see the type
    /// remarks). Returns a fresh <c>bool[]</c>, true = blocked.
    /// </summary>
    internal static bool[] Compute(
        bool[] standable, float[] surfaceHeight, float[] headroom,
        int width, int height, float stepHeight, float agentHeight)
    {
        if (standable is null) throw new ArgumentNullException(nameof(standable));
        if (surfaceHeight is null) throw new ArgumentNullException(nameof(surfaceHeight));
        if (headroom is null) throw new ArgumentNullException(nameof(headroom));
        if (width <= 0 || height <= 0
            || standable.Length != width * height
            || surfaceHeight.Length != width * height
            || headroom.Length != width * height)
            throw new ArgumentException("Array dimensions must be positive and match width * height.");

        var blocked = new bool[width * height];
        List<int>? islands = null;
        for (int cz = 0; cz < height; cz++)
        {
            for (int cx = 0; cx < width; cx++)
            {
                int i = cz * width + cx;
                if (!standable[i]) { blocked[i] = true; continue; }
                if (headroom[i] < agentHeight) { blocked[i] = true; continue; }

                bool eroded = false;
                bool stepReachable = false;
                for (int n = 0; n < Neighbors.Length; n++)
                {
                    int nx = cx + Neighbors[n].Dx;
                    int nz = cz + Neighbors[n].Dz;
                    if (nx < 0 || nz < 0 || nx >= width || nz >= height) continue;
                    int j = nz * width + nx;
                    if (!standable[j]) continue;
                    if (surfaceHeight[i] - surfaceHeight[j] > stepHeight) eroded = true;
                    else stepReachable = true;
                }
                blocked[i] = eroded;

                // An eroded cell with no step-reachable standable neighbor at all is a lone top rather
                // than the edge of a connected field, so it is a candidate to keep for a hop. Whether it
                // actually survives is settled below, once the whole mask exists.
                if (eroded && !stepReachable) (islands ??= new List<int>()).Add(i);
            }
        }

        if (islands is not null) PreserveIsolatedIslands(blocked, islands, width, height);

        return blocked;
    }

    /// <summary>
    /// Un-blocks every island candidate whose 8-neighborhood is entirely blocked, so a lone standable top
    /// the erosion rule would otherwise erase survives for <see cref="NavHopLinks"/> to link. Keeping one
    /// costs nothing anywhere else. It gains no grid edge, since every one of its neighbors is blocked, so
    /// the planner still reaches it only across a link, and no other cell's clearance moves either,
    /// because a blocked ring cell always lies between the island and any outside cell and is therefore
    /// the nearer obstacle. A candidate that still touches a passable cell is left eroded: un-blocking
    /// that one WOULD hand the planner a walk edge straight up the drop the erosion rule exists to block.
    /// Order-independent, since every isolation test reads the first pass's mask and the writes all come
    /// after.
    /// </summary>
    static void PreserveIsolatedIslands(bool[] blocked, List<int> candidates, int width, int height)
    {
        int keep = 0;
        for (int k = 0; k < candidates.Count; k++)
        {
            int i = candidates[k];
            if (IsSurroundedByBlocked(blocked, i % width, i / width, width, height)) candidates[keep++] = i;
        }

        for (int k = 0; k < keep; k++) blocked[candidates[k]] = false;
    }

    /// <summary>True when every 8-neighbor of (<paramref name="cx"/>, <paramref name="cz"/>) is blocked.
    /// Space outside the grid counts as blocked, matching <see cref="ClearanceTransform"/>.</summary>
    static bool IsSurroundedByBlocked(bool[] blocked, int cx, int cz, int width, int height)
    {
        for (int n = 0; n < Neighbors.Length; n++)
        {
            int nx = cx + Neighbors[n].Dx;
            int nz = cz + Neighbors[n].Dz;
            if (nx < 0 || nz < 0 || nx >= width || nz >= height) continue;
            if (!blocked[nz * width + nx]) return false;
        }

        return true;
    }
}
