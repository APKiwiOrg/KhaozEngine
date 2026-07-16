using System;

namespace KhaozEngine.Navigation;

/// <summary>
/// Step-reachability plus headroom pass. Turns per-cell surface data (standable flag, surface height,
/// headroom) into the boolean blocked mask the clearance transform consumes, folding the
/// rise-within-StepHeight rule into which cells are passable so the planner needs no per-edge logic.
/// A cell is blocked when it is not standable, its headroom is below the agent height, or its surface
/// drops to any standable 8-neighbor by more than the step height (the higher side of every too-tall
/// step bakes blocked, which provably stops the grid planner from crossing it). Deterministic: fixed
/// scan and neighbor order, float compares only.
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
    /// cells. Returns a fresh <c>bool[]</c>, true = blocked.
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
        for (int cz = 0; cz < height; cz++)
        {
            for (int cx = 0; cx < width; cx++)
            {
                int i = cz * width + cx;
                if (!standable[i]) { blocked[i] = true; continue; }
                if (headroom[i] < agentHeight) { blocked[i] = true; continue; }

                bool eroded = false;
                for (int n = 0; n < Neighbors.Length; n++)
                {
                    int nx = cx + Neighbors[n].Dx;
                    int nz = cz + Neighbors[n].Dz;
                    if (nx < 0 || nz < 0 || nx >= width || nz >= height) continue;
                    int j = nz * width + nx;
                    if (!standable[j]) continue;
                    if (surfaceHeight[i] - surfaceHeight[j] > stepHeight) { eroded = true; break; }
                }
                blocked[i] = eroded;
            }
        }

        return blocked;
    }
}
