using System;
using System.Collections.Generic;

namespace KhaozEngine.Navigation;

/// <summary>
/// Generates same-grid vertical hop links from a step-baked <see cref="NavGrid"/>: where a standable cell
/// sits across a run of blocked cells from a standable cell whose rise is above the step budget but within
/// a jump budget, a <see cref="NavLinkKind.Hop"/> link joins them so the planner and follower can cross it.
/// Render-free, deterministic.
/// </summary>
public static class NavHopLinks
{
    // Fixed direction order: N, S, E, W, then the four diagonals NE, NW, SE, SW (same as StepMask).
    static readonly (int Dx, int Dz)[] Directions =
    {
        (0, -1), (0, 1), (1, 0), (-1, 0),
        (1, -1), (-1, -1), (1, 1), (-1, 1),
    };

    /// <summary>
    /// Generates the hop links for <paramref name="grid"/>. For each passable cell and each of the 8
    /// neighbor directions, walks outward: the adjacent cell must be blocked (a rim, not open ground) and
    /// every intervening cell up to the first passable cell reached (at distance 2..<paramref name="maxHopCells"/>)
    /// must be blocked too, and that reached cell is the landing when the absolute rise between the two
    /// surface heights is in (<paramref name="stepHeight"/>, <paramref name="jumpHeight"/>]. Because the
    /// scan runs from every passable cell, a jump-band pair yields both directed links (jump up and its
    /// matching drop down). Every emitted link spans a Chebyshev distance of at least 2, so the planner
    /// never mistakes it for a grid step. <paramref name="layer"/> is the index the grid occupies in its
    /// owning <see cref="NavSpace"/> (0 for a single-layer space), stamped into each link's
    /// <see cref="NavLink.FromLayer"/> and <see cref="NavLink.ToLayer"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="grid"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="grid"/> has no surface height field
    /// (<see cref="NavGrid.HasSurfaceHeights"/> is false).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepHeight"/> is negative,
    /// <paramref name="jumpHeight"/> is not greater than <paramref name="stepHeight"/>,
    /// <paramref name="maxHopCells"/> is less than 2, or <paramref name="layer"/> is negative.</exception>
    public static IReadOnlyList<NavLink> Generate(
        NavGrid grid, float stepHeight, float jumpHeight, int maxHopCells = 2, int layer = 0)
    {
        if (grid is null) throw new ArgumentNullException(nameof(grid));
        if (!grid.HasSurfaceHeights)
            throw new ArgumentException("Grid has no surface height field. Bake it with NavGrid.FromSurfaces.", nameof(grid));
        if (stepHeight < 0f)
            throw new ArgumentOutOfRangeException(nameof(stepHeight), stepHeight, "Step height must be non-negative.");
        if (jumpHeight <= stepHeight)
            throw new ArgumentOutOfRangeException(nameof(jumpHeight), jumpHeight, "Jump height must be greater than step height.");
        if (maxHopCells < 2)
            throw new ArgumentOutOfRangeException(nameof(maxHopCells), maxHopCells, "Max hop cells must be at least 2.");
        if (layer < 0)
            throw new ArgumentOutOfRangeException(nameof(layer), layer, "Layer must be non-negative.");

        var links = new List<NavLink>();
        for (int lz = 0; lz < grid.Height; lz++)
        {
            for (int lx = 0; lx < grid.Width; lx++)
            {
                float? hL = grid.SurfaceHeightAt(lx, lz);
                if (hL is null) continue;

                for (int d = 0; d < Directions.Length; d++)
                {
                    (int dx, int dz) = Directions[d];
                    for (int k = 1; k <= maxHopCells; k++)
                    {
                        int curX = lx + dx * k;
                        int curZ = lz + dz * k;
                        if (!grid.InBounds(curX, curZ)) break;

                        float? curHeight = grid.SurfaceHeightAt(curX, curZ);
                        if (k == 1)
                        {
                            if (curHeight is not null) break;
                            continue;
                        }

                        if (curHeight is null) continue;

                        float rise = MathF.Abs(curHeight.Value - hL.Value);
                        if (rise > stepHeight && rise <= jumpHeight)
                            links.Add(new NavLink(layer, lx, lz, layer, curX, curZ) { Kind = NavLinkKind.Hop });
                        break;
                    }
                }
            }
        }

        return links;
    }
}
