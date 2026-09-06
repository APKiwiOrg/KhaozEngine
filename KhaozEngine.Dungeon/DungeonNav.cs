using System;
using System.Collections.Generic;
using KhaozEngine.Navigation;

namespace KhaozEngine.Dungeon;

/// <summary>
/// Bakes a <see cref="DungeonLayout"/> into a <see cref="NavSpace"/> for NPC pathfinding: one
/// <see cref="NavGrid"/> layer per floor, stacked in world-Y bands, joined by the stair
/// <see cref="NavLink"/> connections a climber crosses between floors. Headroom-aware: in a
/// <see cref="DungeonCeilingMode.Roofed"/> layout, a cell whose ceiling clearance is below the agent
/// height baked into <c>Bake</c> blocks, even though its cell kind is walkable. Render-free,
/// deterministic: the same layout always bakes the same space. See <c>Bake</c>.
/// </summary>
public static class DungeonNav
{
    /// <summary>Neighbor X offsets for the four orthogonal (4-adjacent) cells, paired index-for-index
    /// with <see cref="AdjDz"/>.</summary>
    static readonly int[] AdjDx = { 1, -1, 0, 0 };

    /// <summary>Neighbor Z offsets paired with <see cref="AdjDx"/>.</summary>
    static readonly int[] AdjDz = { 0, 0, 1, -1 };

    /// <summary>Full standing height (world metres) <c>Bake</c> checks a
    /// <see cref="DungeonCeilingMode.Roofed"/> layout's headroom against when the caller supplies none.
    /// 1.8, matching the shipped character capsule (<c>MoveTuning.Default.CapsuleHalfHeight</c> 0.9
    /// doubled) and the figure <c>NavGridBaker.BakeOverworldSteps</c> already uses for the same quantity
    /// in its own docs and examples.</summary>
    public const float DefaultAgentHeight = 1.8f;

    /// <summary>
    /// Bakes <paramref name="layout"/> into a navigable <see cref="NavSpace"/>. Each floor f becomes one
    /// <see cref="NavGrid"/> layer covering the world-Y band
    /// [<paramref name="baseY"/> + f * <see cref="DungeonLayout.FloorHeightMeters"/>,
    /// <paramref name="baseY"/> + (f + 1) * <see cref="DungeonLayout.FloorHeightMeters"/>], with a cell
    /// walkable exactly when <see cref="DungeonLayout.IsWalkable"/> is true for its
    /// <see cref="DungeonLayout.GetCell"/> kind on that floor AND its headroom to the ceiling clears
    /// <paramref name="agentHeight"/>. Headroom is <see cref="DungeonLayout.CeilingHeightMeters"/> in a
    /// <see cref="DungeonCeilingMode.Roofed"/> layout and unbounded (open sky) in a
    /// <see cref="DungeonCeilingMode.Open"/> one, so an unroofed layout never blocks on headroom no
    /// matter how tall <paramref name="agentHeight"/> is, and bakes exactly as it did before headroom
    /// awareness was added. The XZ plane is anchored at (<paramref name="originX"/>,
    /// <paramref name="originZ"/>) with cell size <see cref="DungeonLayout.CellSizeMeters"/>, matching
    /// the dungeon sinks' tile-to-world mapping.
    /// <para>
    /// Every stair run contributes a pair of directed links joining its top tread
    /// (<see cref="DungeonCellKind.StairUpper"/>, on the lower floor) to its landing
    /// (<see cref="DungeonCellKind.StairTop"/>, one cell past the top tread on the floor above): one link
    /// each way, so a path can climb or descend the stair. Landings are found by scanning every
    /// <see cref="DungeonCellKind.StairTop"/> cell on each floor f >= 1 and matching it to the single
    /// 4-adjacent <see cref="DungeonCellKind.StairUpper"/> cell on floor f - 1. Stair links are keyed off
    /// cell kind alone, so headroom blocking never removes one, even when the grid cell it lands on bakes
    /// blocked for a too-tall agent.
    /// </para>
    /// </summary>
    /// <param name="layout">The generated dungeon to bake. Must not be null.</param>
    /// <param name="originX">World X of grid cell (0, 0)'s minimum corner on every layer.</param>
    /// <param name="originZ">World Z of grid cell (0, 0)'s minimum corner on every layer.</param>
    /// <param name="baseY">World Y of floor 0's lower band edge. Higher floors stack above it, one
    /// <see cref="DungeonLayout.FloorHeightMeters"/> apart.</param>
    /// <param name="agentHeight">Full standing height, world metres, an agent needs clear above a cell's
    /// floor for that cell to count as walkable in a <see cref="DungeonCeilingMode.Roofed"/> layout.
    /// Ignored entirely in <see cref="DungeonCeilingMode.Open"/>. Defaults to
    /// <see cref="DefaultAgentHeight"/>.</param>
    /// <returns>A <see cref="NavSpace"/> with one layer per floor and the stair links between them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is null.</exception>
    public static NavSpace Bake(
        DungeonLayout layout, float originX = 0f, float originZ = 0f, float baseY = 0f,
        float agentHeight = DefaultAgentHeight)
        => Bake(layout, new DungeonPlotTransform(originX, originZ, baseY, 0), agentHeight);

    /// <summary>
    /// Bakes navigation at the same translation and yaw as the dungeon geometry sinks. Cell centers,
    /// world-space queries and path smoothing use the plot transform. Stair links keep local cell
    /// coordinates and floor heights use <see cref="DungeonPlotTransform.BaseY"/>.
    /// </summary>
    public static NavSpace Bake(DungeonLayout layout, DungeonPlotTransform plot,
        float agentHeight = DefaultAgentHeight)
    {
        ArgumentNullException.ThrowIfNull(layout);

        // Roofed: headroom is the resolved ceiling height above every floor (DungeonStamp places the
        // ceiling slab's underside at exactly floorY + CeilingHeightMeters, so that is the true
        // clearance). Open: there is no ceiling, so headroom never blocks regardless of agentHeight.
        float headroom = layout.CeilingMode == DungeonCeilingMode.Roofed
            ? layout.CeilingHeightMeters
            : float.PositiveInfinity;

        var layers = new NavGrid[layout.Floors];
        for (int f = 0; f < layout.Floors; f++)
        {
            int floor = f; // Capture for the per-cell sample closure below.
            float floorY = plot.BaseY + f * layout.FloorHeightMeters;

            // stepHeight 0: every cell on a floor shares the same floorY, so the surface never "rises"
            // between neighbors and the step-erosion rule in NavGrid.FromSurfaces never trips. Only
            // headroom (blocked when below agentHeight) can newly block a cell here.
            layers[f] = NavGrid.FromSurfaces(
                layout.Width, layout.Depth, layout.CellSizeMeters, plot.OriginX, plot.OriginZ,
                (x, z) => new NavSurfaceSample(
                    DungeonLayout.IsWalkable(layout.GetCell(x, z, floor)), floorY, headroom),
                stepHeight: 0f, agentHeight: agentHeight,
                yMin: floorY,
                yMax: floorY + layout.FloorHeightMeters, yawRadians: plot.YawRadians);
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
