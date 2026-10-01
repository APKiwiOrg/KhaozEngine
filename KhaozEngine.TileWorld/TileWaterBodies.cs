using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>One water body of one region-plane: its tiles as disjoint world tile rects and the height its still
/// surface sits at.</summary>
/// <param name="Rects">The body's tiles as disjoint world tile rects, far edges exclusive, in the order the row scan
/// opens them.</param>
/// <param name="SurfaceY">The still-water height in metres, <see cref="TileWaterBodies.SurfaceDropMetres"/> under the
/// body's rim.</param>
public readonly record struct TileWaterBody(IReadOnlyList<TileRect> Rects, float SurfaceY);

/// <summary>The GPU-free water body rule: which tiles are water, how a region-plane's water splits into 4-connected
/// bodies, how each body is cut into DISJOINT rectangles, and where its surface sits. The water render planes and
/// anything that needs to know where water is (wading, for one) read the same bodies.
/// <para>Water is authored as ground, not placed: a tile is water when its UNDERLAY material has
/// <see cref="GroundMaterialKind.Water"/>, and the author sinks the bed by lowering the corner heights. Only the
/// underlay counts. An overlay drawn in a water material is a puddle-shaped decoration on ordinary ground and gets no
/// surface, because an overlay cuts a fraction of a tile and a surface over a fraction of a tile has no rim to take
/// its height from.</para>
/// <para>Bodies are clipped to the region being collected. Each clipped piece takes its own rim, read from the
/// corners of its own rects, so a body crossing a region border can sit at a different height either side.</para></summary>
public static class TileWaterBodies
{
    /// <summary>How far under the body's rim the surface sits, in metres. The rim is the highest corner the body
    /// touches, which is where it meets its bank, so the surface has to sit just under it or the water would
    /// spill over the lip it is contained by.</summary>
    public const float SurfaceDropMetres = 0.02f;

    /// <summary>True when a tile is water: it draws at all (<see cref="TileGroundTriangles.IsDrawable"/>) and its
    /// underlay material is a water material. A NoDraw tile (a hole the ground skips) is never water, because water
    /// over a hole has no bed under it.</summary>
    /// <param name="document">The world the tile is read from.</param>
    /// <param name="catalogs">The catalogs the underlay id is resolved against.</param>
    /// <param name="worldX">The tile's world x.</param>
    /// <param name="worldZ">The tile's world z.</param>
    /// <param name="plane">The plane.</param>
    /// <returns>True for a water tile.</returns>
    public static bool IsWater(TileWorldDocument document, TileWorldCatalogs catalogs, int worldX, int worldZ, int plane)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalogs);
        return TileGroundTriangles.IsDrawable(document, worldX, worldZ, plane)
            && catalogs.Material(document.GetUnderlay(worldX, worldZ, plane))?.Kind == GroundMaterialKind.Water;
    }

    /// <summary>Every water body one region-plane holds, in a deterministic order: bodies in discovery order from the
    /// region's south-west corner, and each body's rectangles in the order the row scan opens them.</summary>
    /// <param name="document">The world the tiles and corner heights are read from.</param>
    /// <param name="catalogs">The catalogs the underlay ids are resolved against.</param>
    /// <param name="region">The region whose 64x64 tiles are scanned. Bodies are clipped to it.</param>
    /// <param name="plane">The plane within that region.</param>
    /// <returns>The bodies with world tile rects, empty when the region-plane holds no water.</returns>
    public static IReadOnlyList<TileWaterBody> Collect(TileWorldDocument document, TileWorldCatalogs catalogs,
                                                       RegionCoord region, int plane)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalogs);

        int size = TileRegion.Size;
        var mask = new bool[size, size];
        bool any = false;
        for (int lz = 0; lz < size; lz++)
            for (int lx = 0; lx < size; lx++)
                if (IsWater(document, catalogs, region.OriginX + lx, region.OriginZ + lz, plane))
                    any = mask[lx, lz] = true;
        if (!any) return Array.Empty<TileWaterBody>();

        var bodies = new List<TileWaterBody>();
        foreach (IReadOnlyList<TileRect> body in Components(mask))
        {
            float surfaceY = RimHeight(document, region, plane, body) - SurfaceDropMetres;
            var rects = new TileRect[body.Count];
            for (int i = 0; i < rects.Length; i++)
            {
                TileRect local = body[i];
                rects[i] = new TileRect(local.X + region.OriginX, local.Z + region.OriginZ, local.Width, local.Height);
            }
            bodies.Add(new TileWaterBody(rects, surfaceY));
        }
        return bodies;
    }

    /// <summary>The 4-connected components of a mask, each already cut into disjoint rectangles by
    /// <see cref="Rectangles"/>. Components are discovered row by row from index (0, 0), so the order is a pure
    /// function of the mask.</summary>
    /// <param name="mask">Cells indexed <c>[x, z]</c>, true where the cell belongs to a body.</param>
    /// <returns>One rectangle list per component, in discovery order.</returns>
    public static IReadOnlyList<IReadOnlyList<TileRect>> Components(bool[,] mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        int w = mask.GetLength(0), h = mask.GetLength(1);
        var seen = new bool[w, h];
        // One scratch mask reused by every component, wiped through the component's own cells afterwards, so a
        // region full of small ponds does not allocate a full-size mask each.
        var scratch = new bool[w, h];
        var cells = new List<(int X, int Z)>();
        var frontier = new Stack<(int X, int Z)>();
        var result = new List<IReadOnlyList<TileRect>>();

        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
            {
                if (!mask[x, z] || seen[x, z]) continue;

                cells.Clear();
                frontier.Clear();
                seen[x, z] = true;
                frontier.Push((x, z));
                while (frontier.Count > 0)
                {
                    (int cx, int cz) = frontier.Pop();
                    cells.Add((cx, cz));
                    scratch[cx, cz] = true;
                    Visit(mask, seen, frontier, cx - 1, cz, w, h);
                    Visit(mask, seen, frontier, cx + 1, cz, w, h);
                    Visit(mask, seen, frontier, cx, cz - 1, w, h);
                    Visit(mask, seen, frontier, cx, cz + 1, w, h);
                }

                result.Add(Rectangles(scratch));
                foreach ((int cx, int cz) in cells) scratch[cx, cz] = false;
            }

        return result;
    }

    /// <summary>Cuts a mask into disjoint rectangles by the greedy row-run merge: each row's maximal runs of set
    /// cells either EXTEND the rectangle directly below them when the x span is identical, or open a new one.
    /// Deterministic, and pinned by tests rather than left to the implementation, because the plane count a body
    /// costs the water pass is exactly the count this returns.
    /// <para>Connectivity is not consulted, so this is the per-body primitive: hand it one body's cells
    /// (<see cref="Components"/> does) rather than a mask holding several, or two bodies that happen to share a
    /// row span merge into one rectangle spanning the gap between them.</para></summary>
    /// <param name="mask">Cells indexed <c>[x, z]</c>, true where the cell is covered.</param>
    /// <returns>The rectangles, in the order the row scan opens them, together covering exactly the set cells.</returns>
    public static IReadOnlyList<TileRect> Rectangles(bool[,] mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        int w = mask.GetLength(0), h = mask.GetLength(1);
        var rects = new List<TileRect>();
        // Rectangles whose top edge is the row just below the one being scanned, so they are the only ones a run
        // in this row can extend. Swapped rather than rebuilt, so the scan allocates nothing per row.
        var open = new List<int>();
        var next = new List<int>();

        for (int z = 0; z < h; z++)
        {
            next.Clear();
            int x = 0;
            while (x < w)
            {
                if (!mask[x, z]) { x++; continue; }
                int start = x;
                while (x < w && mask[x, z]) x++;
                int width = x - start;

                int match = -1;
                for (int i = 0; i < open.Count; i++)
                {
                    TileRect candidate = rects[open[i]];
                    if (candidate.X == start && candidate.Width == width) { match = i; break; }
                }

                if (match >= 0)
                {
                    int index = open[match];
                    rects[index] = rects[index] with { Height = rects[index].Height + 1 };
                    open.RemoveAt(match);
                    next.Add(index);
                }
                else
                {
                    rects.Add(new TileRect(start, z, width, 1));
                    next.Add(rects.Count - 1);
                }
            }
            (open, next) = (next, open);
        }

        return rects;
    }

    // The highest corner the body touches, in metres, read over each region-local rect's corners with the far edges
    // included, so a piece clipped at a region border reads the shared border corners. One height for the whole
    // body, so a river that descends is authored as separate bodies with a weir between them rather than as one
    // sloped surface.
    static float RimHeight(TileWorldDocument document, RegionCoord region, int plane, IReadOnlyList<TileRect> body)
    {
        int rim = int.MinValue;
        foreach (TileRect local in body)
            for (int z = local.Z; z <= local.Z1; z++)
                for (int x = local.X; x <= local.X1; x++)
                    rim = Math.Max(rim, document.CornerHeightCm(region.OriginX + x, region.OriginZ + z, plane));
        return rim * 0.01f;
    }

    static void Visit(bool[,] mask, bool[,] seen, Stack<(int X, int Z)> frontier, int x, int z, int w, int h)
    {
        if (x < 0 || x >= w || z < 0 || z >= h || !mask[x, z] || seen[x, z]) return;
        seen[x, z] = true;
        frontier.Push((x, z));
    }
}
