using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Turns a region-plane's water tiles into the <see cref="WaterPlane"/> requests the engine's water
/// pass draws: one 4-connected body at a time, each cut into a DISJOINT set of rectangles sitting 2 cm under the
/// rim the body shares with its bank.
/// <para>Water is authored as ground, not placed: a tile is water when its UNDERLAY material has
/// <see cref="GroundMaterialKind.Water"/>, and the author sinks the bed by lowering the corner heights. Only the
/// underlay counts in R5. An overlay drawn in a water material is a puddle-shaped decoration on ordinary ground
/// and gets no surface, because an overlay cuts a fraction of a tile and a surface over a fraction of a tile has
/// no rim to take its height from.</para>
/// <para>Rectangles rather than one plane per tile, because the pass draws a fixed grid per plane
/// (<c>WaterMath.GridResolution</c>, 97 by 97 vertices whatever the plane covers), so a straight river has to be one
/// plane and a bend a few. Rectangles rather than one bounding box per body, because a box over-covers at a bend
/// and the pass only discards where the ground is at or above the surface, so a ditch, a cave mouth or a sunk
/// road cut inside the box would render as water. The rectangles are the body's own tiles and nothing
/// else.</para></summary>
public static class TileWaterPlanes
{
    /// <summary>How far under the body's rim the surface sits, in metres. The rim is the highest corner the body
    /// touches, which is where it meets its bank, so the surface has to sit just under it or the water would
    /// spill over the lip it is contained by.</summary>
    public const float SurfaceDropMetres = 0.02f;

    /// <summary>Plane count above which one call logs a warning. Every plane costs the pass a full grid, so a
    /// region-plane emitting more than this is the signal that a river was drawn as a staircase of short runs
    /// where a few longer ones would read the same.</summary>
    public const int PlaneCountWarnThreshold = 16;

    // Cached per the facade's contract: an ambient logger holds its category and resolves the configured manager
    // per call, so one static field stays correct across a reconfigure.
    static readonly ILogger Logger = Log.Get(nameof(TileWaterPlanes));

    /// <summary>Every water plane one region-plane contributes, in a deterministic order: bodies in discovery
    /// order from the region's south-west corner, and each body's rectangles in the order the row scan opens
    /// them.</summary>
    /// <param name="doc">The world the tiles and corner heights are read from.</param>
    /// <param name="catalogs">The catalogs the underlay ids are resolved against.</param>
    /// <param name="region">The region whose 64x64 tiles are scanned. Bodies are clipped to it.</param>
    /// <param name="plane">The plane within that region.</param>
    /// <param name="look">The per-plane look every emitted plane carries, or null for the scene's own.</param>
    /// <returns>The planes, empty when the region-plane holds no water.</returns>
    /// <exception cref="InvalidOperationException">Two emitted planes overlap, which is a bug in the
    /// decomposition rather than a content error.</exception>
    public static IReadOnlyList<WaterPlane> Collect(TileWorldDocument doc, TileWorldCatalogs catalogs,
                                                    RegionCoord region, int plane, WaterLook? look = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);

        int size = TileRegion.Size;
        var mask = new bool[size, size];
        bool any = false;
        for (int lz = 0; lz < size; lz++)
            for (int lx = 0; lx < size; lx++)
                if (IsWater(doc, catalogs, region.OriginX + lx, region.OriginZ + lz, plane))
                    any = mask[lx, lz] = true;
        if (!any) return Array.Empty<WaterPlane>();

        var rects = new List<TileRect>();
        var planes = new List<WaterPlane>();
        foreach (IReadOnlyList<TileRect> body in Components(mask))
        {
            float surfaceY = RimHeight(doc, region, plane, body) - SurfaceDropMetres;
            foreach (TileRect local in body)
            {
                var world = new TileRect(local.X + region.OriginX, local.Z + region.OriginZ, local.Width, local.Height);
                rects.Add(world);
                planes.Add(ToPlane(world, surfaceY, doc.TileSize, look));
            }
        }

        RequireDisjoint(rects, region, plane);
        if (OverflowWarning(planes.Count, region, plane) is { } warning) Logger.Warn(warning);
        return planes;
    }

    /// <summary>One water plane covering a rect of tiles, converted through <see cref="TileWorldSpace"/>. World z
    /// is MINUS tile z, so a rect further north has a more negative centre.</summary>
    /// <param name="tiles">The world tile rect, far edges exclusive.</param>
    /// <param name="surfaceY">The still-water height in metres.</param>
    /// <param name="tileSize">Metres per tile, from the document.</param>
    /// <param name="look">The per-plane look, or null for the scene's own.</param>
    /// <returns>The plane request.</returns>
    public static WaterPlane ToPlane(TileRect tiles, float surfaceY, float tileSize, WaterLook? look = null) =>
        new(TileWorldSpace.WorldX(tiles.X + tiles.Width * 0.5f, tileSize),
            surfaceY,
            TileWorldSpace.WorldZ(tiles.Z + tiles.Height * 0.5f, tileSize),
            tiles.Width * tileSize * 0.5f,
            tiles.Height * tileSize * 0.5f,
            look);

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
    /// costs is exactly the count this returns.
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

    /// <summary>The line one call logs when it emitted too many planes, or null when it did not. Built here
    /// rather than formatted at the call site so the threshold and the wording can be pinned by a test that
    /// never touches the ambient logging facade. That matters in this repo: the render test assembly holds
    /// exactly ONE <c>Log.Configure</c> call, a module initializer that arms the GPU validation artifact, and a
    /// second configure anywhere in the process throws that artifact's sink away (KhaozEngine#617). So the
    /// message is testable and the facade is left alone.</summary>
    /// <param name="count">How many planes the call emitted.</param>
    /// <param name="region">The region, quoted in the message.</param>
    /// <param name="plane">The plane, quoted in the message.</param>
    /// <returns>The warning, or null at or below <see cref="PlaneCountWarnThreshold"/>.</returns>
    internal static string? OverflowWarning(int count, RegionCoord region, int plane) =>
        count <= PlaneCountWarnThreshold
            ? null
            : $"tile world: region {region} plane {plane} emitted {count} water planes, over the " +
              $"{PlaneCountWarnThreshold} one region-plane is expected to need. Each plane costs the water pass a " +
              "full grid, so the water here wants fewer and longer runs.";

    /// <summary>Throws when any two rectangles share a tile. Two overlapping planes double-darken, because the
    /// water pass blends with depth write off, and their boundary reads as a crisp step in brightness (the
    /// failure Ruinborne's inland lake cover was rebuilt to avoid). The decomposition cannot produce one, so a
    /// hit here is a bug in this file rather than anything an author did.</summary>
    /// <param name="rects">The rectangles one region-plane emitted.</param>
    /// <param name="region">The region, quoted in the message.</param>
    /// <param name="plane">The plane, quoted in the message.</param>
    internal static void RequireDisjoint(IReadOnlyList<TileRect> rects, RegionCoord region, int plane)
    {
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
                if (rects[i].Intersects(rects[j]))
                    throw new InvalidOperationException(
                        $"region {region} plane {plane}: water planes {i} and {j} overlap ({rects[i]} and {rects[j]}). " +
                        "Two planes over the same tiles double-darken, so the decomposition must never emit them.");
    }

    // A tile is water when it draws at all and its underlay material is a water material. The drawable test is
    // the mesher's own, so a NoDraw tile (a hole the ground mesh skips) gets no surface either: water over a hole
    // has no bed under it to darken against, and would read as a slab of blue over whatever lies beyond.
    static bool IsWater(TileWorldDocument doc, TileWorldCatalogs catalogs, int x, int z, int plane) =>
        TileGroundMesher.IsDrawable(doc, x, z, plane)
        && catalogs.Material(doc.GetUnderlay(x, z, plane))?.Kind == GroundMaterialKind.Water;

    // The highest corner the body touches, in metres. One height for the whole body, so a river that descends is
    // authored as separate bodies with a weir between them rather than as one sloped surface.
    static float RimHeight(TileWorldDocument doc, RegionCoord region, int plane, IReadOnlyList<TileRect> body)
    {
        int rim = int.MinValue;
        foreach (TileRect local in body)
            for (int z = local.Z; z <= local.Z1; z++)
                for (int x = local.X; x <= local.X1; x++)
                    rim = Math.Max(rim, doc.CornerHeightCm(region.OriginX + x, region.OriginZ + z, plane));
        return rim * 0.01f;
    }

    static void Visit(bool[,] mask, bool[,] seen, Stack<(int X, int Z)> frontier, int x, int z, int w, int h)
    {
        if (x < 0 || x >= w || z < 0 || z >= h || !mask[x, z] || seen[x, z]) return;
        seen[x, z] = true;
        frontier.Push((x, z));
    }
}
