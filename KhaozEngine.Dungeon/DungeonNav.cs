using System;
using System.Collections.Generic;
using KhaozEngine.Navigation;

namespace KhaozEngine.Dungeon;

/// <summary>
/// Bakes a <see cref="DungeonLayout"/> into a <see cref="NavSpace"/> for NPC pathfinding: one
/// <see cref="NavGrid"/> layer per floor, stacked in world-Y bands, joined by the stair
/// <see cref="NavLink"/> connections a climber crosses between floors. Render-free, deterministic:
/// the same layout always bakes the same space. See <see cref="Bake"/>.
/// </summary>
public static class DungeonNav
{
    /// <summary>Neighbor X offsets for the four orthogonal (4-adjacent) cells, paired index-for-index
    /// with <see cref="AdjDz"/>.</summary>
    static readonly int[] AdjDx = { 1, -1, 0, 0 };

    /// <summary>Neighbor Z offsets paired with <see cref="AdjDx"/>.</summary>
    static readonly int[] AdjDz = { 0, 0, 1, -1 };

    /// <summary>
    /// Bakes <paramref name="layout"/> into a navigable <see cref="NavSpace"/>. Each floor f becomes one
    /// <see cref="NavGrid"/> layer covering the world-Y band
    /// [<paramref name="baseY"/> + f * <see cref="DungeonLayout.FloorHeightMeters"/>,
    /// <paramref name="baseY"/> + (f + 1) * <see cref="DungeonLayout.FloorHeightMeters"/>], with a cell
    /// walkable exactly when <see cref="DungeonLayout.IsWalkable"/> is true for its
    /// <see cref="DungeonLayout.GetCell"/> kind on that floor. The XZ plane is anchored at
    /// (<paramref name="originX"/>, <paramref name="originZ"/>) with cell size
    /// <see cref="DungeonLayout.CellSizeMeters"/>, matching the dungeon sinks' tile-to-world mapping.
    /// <para>
    /// Every stair run contributes a pair of directed links joining its top tread
    /// (<see cref="DungeonCellKind.StairUpper"/>, on the lower floor) to its landing
    /// (<see cref="DungeonCellKind.StairTop"/>, one cell past the top tread on the floor above): one link
    /// each way, so a path can climb or descend the stair. Landings are found by scanning every
    /// <see cref="DungeonCellKind.StairTop"/> cell on each floor f >= 1 and matching it to the single
    /// 4-adjacent <see cref="DungeonCellKind.StairUpper"/> cell on floor f - 1.
    /// </para>
    /// </summary>
    /// <param name="layout">The generated dungeon to bake. Must not be null.</param>
    /// <param name="originX">World X of grid cell (0, 0)'s minimum corner on every layer.</param>
    /// <param name="originZ">World Z of grid cell (0, 0)'s minimum corner on every layer.</param>
    /// <param name="baseY">World Y of floor 0's lower band edge. Higher floors stack above it, one
    /// <see cref="DungeonLayout.FloorHeightMeters"/> apart.</param>
    /// <returns>A <see cref="NavSpace"/> with one layer per floor and the stair links between them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is null.</exception>
    public static NavSpace Bake(DungeonLayout layout, float originX = 0f, float originZ = 0f, float baseY = 0f)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var layers = new NavGrid[layout.Floors];
        for (int f = 0; f < layout.Floors; f++)
        {
            int floor = f; // Capture for the per-cell walkable closure below.
            layers[f] = NavGrid.FromWalkable(
                layout.Width, layout.Depth, layout.CellSizeMeters, originX, originZ,
                (x, z) => DungeonLayout.IsWalkable(layout.GetCell(x, z, floor)),
                yMin: baseY + f * layout.FloorHeightMeters,
                yMax: baseY + (f + 1) * layout.FloorHeightMeters);
        }

        var links = new List<NavLink>();
        for (int f = 1; f < layout.Floors; f++)
        {
            for (int z = 0; z < layout.Depth; z++)
            {
                for (int x = 0; x < layout.Width; x++)
                {
                    if (layout.GetCell(x, z, f) != DungeonCellKind.StairTop)
                    {
                        continue;
                    }

                    // The landing sits one cell past the top tread, so exactly one 4-adjacent cell on the
                    // floor below is the run's StairUpper. Emit both directed links for that stair.
                    for (int a = 0; a < AdjDx.Length; a++)
                    {
                        int nx = x + AdjDx[a];
                        int nz = z + AdjDz[a];
                        if (layout.GetCell(nx, nz, f - 1) != DungeonCellKind.StairUpper)
                        {
                            continue;
                        }

                        links.Add(new NavLink(f - 1, nx, nz, f, x, z));
                        links.Add(new NavLink(f, x, z, f - 1, nx, nz));
                    }
                }
            }
        }

        return new NavSpace(layers, links);
    }
}
