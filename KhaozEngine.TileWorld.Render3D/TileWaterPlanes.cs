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
/// else.</para>
/// <para>The body rule itself (which tiles are water, the body search, the rectangle cut and the rim) lives in
/// <see cref="TileWaterBodies"/>. This type turns its bodies into plane requests.</para></summary>
public static class TileWaterPlanes
{
    /// <summary>How far under the body's rim the surface sits, in metres. Forwards to
    /// <see cref="TileWaterBodies.SurfaceDropMetres"/>, which owns it.</summary>
    public const float SurfaceDropMetres = TileWaterBodies.SurfaceDropMetres;

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

        IReadOnlyList<TileWaterBody> bodies = TileWaterBodies.Collect(doc, catalogs, region, plane);
        if (bodies.Count == 0) return Array.Empty<WaterPlane>();

        var rects = new List<TileRect>();
        var planes = new List<WaterPlane>();
        foreach (TileWaterBody body in bodies)
            foreach (TileRect world in body.Rects)
            {
                rects.Add(world);
                planes.Add(ToPlane(world, body.SurfaceY, doc.TileSize, look));
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

    /// <summary>Forwards to <see cref="TileWaterBodies.Components"/>, which owns the body search.</summary>
    /// <param name="mask">Cells indexed <c>[x, z]</c>, true where the cell belongs to a body.</param>
    /// <returns>One rectangle list per component, in discovery order.</returns>
    public static IReadOnlyList<IReadOnlyList<TileRect>> Components(bool[,] mask) => TileWaterBodies.Components(mask);

    /// <summary>Forwards to <see cref="TileWaterBodies.Rectangles"/>, which owns the rectangle cut.</summary>
    /// <param name="mask">Cells indexed <c>[x, z]</c>, true where the cell is covered.</param>
    /// <returns>The rectangles, in the order the row scan opens them, together covering exactly the set cells.</returns>
    public static IReadOnlyList<TileRect> Rectangles(bool[,] mask) => TileWaterBodies.Rectangles(mask);

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
    /// hit here is a bug in <see cref="TileWaterBodies"/> rather than anything an author did.</summary>
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
}
