using System;
using System.Collections.Generic;

namespace KhaozEngine.Navigation;

/// <summary>
/// Generates cross-layer links between the co-registered <see cref="NavGrid"/> layers of a layered
/// overworld bake: where a passable cell in one layer sits Chebyshev-distance 1 from a passable cell
/// in another, the pair is joined by a directed <see cref="NavLinkKind.Stair"/> pair when the rise is
/// within the step budget (a walked seam, e.g. a bridge deck meeting its abutment), or a directed
/// <see cref="NavLinkKind.Hop"/> pair when the rise is in (step, jump] (a cliff edge, a rock top).
/// Same-column (Chebyshev 0) pairs are deliberately not linked: standing under a ledge does not make
/// it jumpable straight up. Render-free, deterministic.
/// </summary>
public static class NavLayerLinks
{
    // Fixed direction order: N, S, E, W, then the four diagonals NE, NW, SE, SW (same as StepMask).
    static readonly (int Dx, int Dz)[] Directions =
    {
        (0, -1), (0, 1), (1, 0), (-1, 0),
        (1, -1), (-1, -1), (1, 1), (-1, 1),
    };

    /// <summary>
    /// Generates the cross-layer links for <paramref name="layers"/>, whose indices in the list are
    /// the layer indices stamped into the links. Every grid must carry surface heights
    /// (<see cref="NavGrid.HasSurfaceHeights"/>) and all grids must share the same dimensions, cell
    /// size, and origin, since cell coordinates are compared across layers. For each ordered layer
    /// pair (a &lt; b), each passable cell in a is tested against its 8 neighbors in b, and a
    /// qualifying pair emits both directed links (a to b, then b to a). Scan order is fixed (layer
    /// pair, then z, then x, then direction), so output order is deterministic.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="layers"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="layers"/> is empty, a grid is null or has
    /// no surface height field, or the grids' dimensions, cell size, or origin differ.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepHeight"/> is negative, or
    /// <paramref name="jumpHeight"/> is not greater than <paramref name="stepHeight"/>.</exception>
    public static IReadOnlyList<NavLink> Generate(
        IReadOnlyList<NavGrid> layers, float stepHeight, float jumpHeight)
    {
        if (layers is null) throw new ArgumentNullException(nameof(layers));
        if (layers.Count == 0) throw new ArgumentException("At least one layer is required.", nameof(layers));
        if (stepHeight < 0f)
            throw new ArgumentOutOfRangeException(nameof(stepHeight), stepHeight, "Step height must be non-negative.");
        if (jumpHeight <= stepHeight)
            throw new ArgumentOutOfRangeException(nameof(jumpHeight), jumpHeight, "Jump height must be greater than step height.");

        NavGrid first = layers[0] ?? throw new ArgumentException("Layer 0 is null.", nameof(layers));
        for (int i = 0; i < layers.Count; i++)
        {
            NavGrid grid = layers[i] ?? throw new ArgumentException($"Layer {i} is null.", nameof(layers));
            if (!grid.HasSurfaceHeights)
                throw new ArgumentException($"Layer {i} has no surface height field. Bake it with NavGrid.FromSurfaces.", nameof(layers));
            if (grid.Width != first.Width || grid.Height != first.Height
                || grid.CellSize != first.CellSize
                || grid.OriginX != first.OriginX || grid.OriginZ != first.OriginZ)
                throw new ArgumentException($"Layer {i} does not share layer 0's dimensions, cell size, and origin.", nameof(layers));
        }

        var links = new List<NavLink>();
        for (int a = 0; a < layers.Count - 1; a++)
        {
            for (int b = a + 1; b < layers.Count; b++)
            {
                NavGrid gridA = layers[a];
                NavGrid gridB = layers[b];

                for (int cz = 0; cz < gridA.Height; cz++)
                {
                    for (int cx = 0; cx < gridA.Width; cx++)
                    {
                        float? ha = gridA.SurfaceHeightAt(cx, cz);
                        if (ha is null) continue;

                        for (int d = 0; d < Directions.Length; d++)
                        {
                            int nx = cx + Directions[d].Dx;
                            int nz = cz + Directions[d].Dz;
                            float? hb = gridB.SurfaceHeightAt(nx, nz);
                            if (hb is null) continue;

                            float rise = MathF.Abs(hb.Value - ha.Value);
                            if (rise <= stepHeight)
                            {
                                links.Add(new NavLink(a, cx, cz, b, nx, nz));
                                links.Add(new NavLink(b, nx, nz, a, cx, cz));
                            }
                            else if (rise <= jumpHeight)
                            {
                                links.Add(new NavLink(a, cx, cz, b, nx, nz) { Kind = NavLinkKind.Hop });
                                links.Add(new NavLink(b, nx, nz, a, cx, cz) { Kind = NavLinkKind.Hop });
                            }
                        }
                    }
                }
            }
        }

        return links;
    }
}
