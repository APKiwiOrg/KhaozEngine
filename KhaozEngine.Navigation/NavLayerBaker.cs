using System;
using System.Collections.Generic;

namespace KhaozEngine.Navigation;

/// <summary>
/// The layered overworld bake, phase 2 of vertical worlds (see NAV-LAYERED-SURFACES-DESIGN.md):
/// reads every standable surface per XZ column from an <see cref="INavColumnProvider"/>, decomposes
/// them into <see cref="NavGrid"/> layers via <see cref="NavLayerExtractor"/>, and joins the layers
/// with generated links, so bridges, overhangs, and roofed interiors where two walkable surfaces
/// coexist at one XZ all become navigable. <see cref="NavGridBaker"/>'s single-layer bakes are
/// untouched. This is the additive multi-layer entry point.
/// </summary>
public static class NavLayerBaker
{
    /// <summary>
    /// Bakes a multi-layer <see cref="NavSpace"/> over the rectangular XZ region
    /// [<paramref name="minX"/>, <paramref name="maxX"/>) by [<paramref name="minZ"/>, <paramref name="maxZ"/>)
    /// from <paramref name="columns"/>. Surfaces whose headroom is below <paramref name="agentHeight"/>
    /// are dropped, the rest are decomposed into layers (regions grown with 8-adjacency within
    /// <paramref name="stepHeight"/>, single-valued per column, merged when disjoint, assigned so
    /// that two regions share a layer only when they have no column overlap and no adjacency), and
    /// each layer bakes through <see cref="NavGrid.FromSurfaces"/> with its own surface-height Y band.
    /// Links: same-grid hop links per layer (<see cref="NavHopLinks.Generate"/> with
    /// <paramref name="jumpHeight"/> and <paramref name="maxHopCells"/>), then cross-layer links
    /// (<see cref="NavLayerLinks.Generate"/>: walked <see cref="NavLinkKind.Stair"/> seams within
    /// <paramref name="stepHeight"/>, <see cref="NavLinkKind.Hop"/> links in the jump band).
    /// <paramref name="maxSurfacesPerColumn"/> caps the per-column sample buffer.
    /// <paramref name="extraBlocked"/> excludes a whole column at its cell center, matching the
    /// single-layer bakes. Width and height are derived as
    /// <c>(int)MathF.Ceiling((max - min) / cellSize)</c> per axis (same as
    /// <see cref="NavGridBaker.BakeOverworld"/>). A world with no standable surface at all returns a
    /// single fully-blocked layer, so the result is always a valid <see cref="NavSpace"/>.
    /// Deterministic when the provider is.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound, size, or budget argument is out of
    /// range, per the same rules as <see cref="NavGridBaker.BakeOverworldHops"/>, or
    /// <paramref name="maxSurfacesPerColumn"/> is less than 1.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="columns"/> violated its contract:
    /// returned a count outside [0, buffer length], or wrote standable surfaces out of ascending
    /// height order.</exception>
    public static NavSpace BakeOverworldLayered(
        INavColumnProvider columns,
        float minX, float minZ, float maxX, float maxZ,
        float cellSize, float stepHeight, float agentHeight, float jumpHeight,
        int maxSurfacesPerColumn = 4,
        int maxHopCells = 2,
        Func<float, float, bool>? extraBlocked = null)
    {
        if (columns is null) throw new ArgumentNullException(nameof(columns));
        if (maxX <= minX) throw new ArgumentOutOfRangeException(nameof(maxX), maxX, "maxX must be greater than minX.");
        if (maxZ <= minZ) throw new ArgumentOutOfRangeException(nameof(maxZ), maxZ, "maxZ must be greater than minZ.");
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
        if (stepHeight < 0f) throw new ArgumentOutOfRangeException(nameof(stepHeight), stepHeight, "Step height must be non-negative.");
        if (agentHeight < 0f) throw new ArgumentOutOfRangeException(nameof(agentHeight), agentHeight, "Agent height must be non-negative.");
        if (jumpHeight <= stepHeight)
            throw new ArgumentOutOfRangeException(nameof(jumpHeight), jumpHeight, "Jump height must be greater than step height.");
        if (maxSurfacesPerColumn < 1)
            throw new ArgumentOutOfRangeException(nameof(maxSurfacesPerColumn), maxSurfacesPerColumn, "Max surfaces per column must be at least 1.");
        if (maxHopCells < 2)
            throw new ArgumentOutOfRangeException(nameof(maxHopCells), maxHopCells, "Max hop cells must be at least 2.");

        int width = (int)MathF.Ceiling((maxX - minX) / cellSize);
        int height = (int)MathF.Ceiling((maxZ - minZ) / cellSize);
        int cellCount = width * height;

        // Flatten the provider's columns once: prefix offsets per cell into ascending surface arrays,
        // already filtered to standable entries with headroom for the agent.
        var columnStart = new int[cellCount + 1];
        var heightsList = new List<float>();
        var headroomsList = new List<float>();
        Span<NavSurfaceSample> buffer = stackalloc NavSurfaceSample[maxSurfacesPerColumn];

        for (int cz = 0; cz < height; cz++)
        {
            for (int cx = 0; cx < width; cx++)
            {
                int ci = cz * width + cx;
                columnStart[ci] = heightsList.Count;

                float wx = minX + (cx + 0.5f) * cellSize;
                float wz = minZ + (cz + 0.5f) * cellSize;
                if (extraBlocked is not null && extraBlocked(wx, wz)) continue;

                int count = columns.SampleColumn(wx, wz, buffer);
                if (count < 0 || count > buffer.Length)
                    throw new InvalidOperationException(
                        $"INavColumnProvider returned {count} surfaces for a buffer of {buffer.Length} at ({wx}, {wz}).");

                float previous = float.NegativeInfinity;
                for (int s = 0; s < count; s++)
                {
                    NavSurfaceSample sample = buffer[s];
                    if (!sample.Standable) continue;
                    if (sample.Height <= previous)
                        throw new InvalidOperationException(
                            $"INavColumnProvider wrote standable surfaces out of ascending height order at ({wx}, {wz}).");
                    previous = sample.Height;

                    if (sample.Headroom < agentHeight) continue;
                    heightsList.Add(sample.Height);
                    headroomsList.Add(sample.Headroom);
                }
            }
        }
        columnStart[cellCount] = heightsList.Count;

        List<NavLayerExtractor.Layer> extracted = NavLayerExtractor.Extract(
            width, height, columnStart, heightsList.ToArray(), headroomsList.ToArray(), stepHeight);

        if (extracted.Count == 0)
        {
            NavGrid empty = NavGrid.FromSurfaces(
                width, height, cellSize, minX, minZ,
                (_, _) => new NavSurfaceSample(false, 0f, 0f),
                stepHeight, agentHeight);
            return NavSpace.Single(empty);
        }

        var grids = new NavGrid[extracted.Count];
        for (int l = 0; l < extracted.Count; l++)
        {
            NavLayerExtractor.Layer layer = extracted[l];
            grids[l] = NavGrid.FromSurfaces(
                width, height, cellSize, minX, minZ,
                (cx, cz) =>
                {
                    int i = cz * width + cx;
                    return new NavSurfaceSample(layer.Standable[i], layer.Height[i], layer.Headroom[i]);
                },
                stepHeight, agentHeight, layer.YMin, layer.YMax);
        }

        var links = new List<NavLink>();
        for (int l = 0; l < grids.Length; l++)
            links.AddRange(NavHopLinks.Generate(grids[l], stepHeight, jumpHeight, maxHopCells, layer: l));
        if (grids.Length > 1)
            links.AddRange(NavLayerLinks.Generate(grids, stepHeight, jumpHeight));

        return new NavSpace(grids, links);
    }
}
