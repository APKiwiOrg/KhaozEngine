using System.ComponentModel;
using KhaozEngine.TileWorld;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>The corner-height lattice: read it, write it outright, and the three brushes (raise, flatten,
/// smooth) plus a heightmap import. Every method delegates to <see cref="QueryService"/> or
/// <see cref="MutationService"/> through <see cref="ToolGuard.Guard{T}"/>.
///
/// <para>Heights live on tile CORNERS, not on tiles, so every rect here is a rect of corners: the corners of the
/// tiles (0, 0) to (3, 3) are the rect (0, 0, 5, 5), one wider and one deeper than the tiles they carry. Values
/// are centimetres. Rows read and write NORTH FIRST, row 0 being the highest z, so a read from height_get_rect
/// hands straight back to height_set without flipping the terrain.</para></summary>
[McpServerToolType]
public sealed class HeightTools(QueryService query, MutationService mutate)
{
    /// <summary>Writes an explicit corner-height lattice.</summary>
    [McpServerTool(Name = "height_set"), Description("Writes explicit corner heights over a rect of CORNERS. The rows are given NORTH FIRST: row 0 is the highest z of the rect, each row runs west to east and is width long, and there are height rows. That is exactly the shape height_get_rect returns, so reading a patch, editing it and writing it back round trips. One undo step. Returns the undo label, the dirty flag, the undo depth, the new world hash, the rects touched, and how many corners the rect covered against how many actually landed (they differ where the rect reached space no region holds).")]
    public HeightResult HeightSet(
        [Description("Corner rect's west edge, corner x, inclusive.")] int x,
        [Description("Corner rect's south edge, corner z, inclusive.")] int z,
        [Description("Corner rect width, so x + width is exclusive. Covering the tiles x..x+n-1 needs width n+1.")] int width,
        [Description("Corner rect height, so z + height is exclusive. Covering the tiles z..z+n-1 needs height n+1.")] int height,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("The heights in centimetres, NORTH FIRST: rows[0] is the highest z, each row west to east and width long, with height rows in total.")] short[][] rows)
        => ToolGuard.Guard(() => mutate.HeightsSet(new TileRect(x, z, width, height), plane, rows));

    /// <summary>Raises or lowers a corner rect.</summary>
    [McpServerTool(Name = "height_raise"), Description("Raises the corners of a rect by a delta in centimetres, or lowers them with a negative delta, optionally fading the delta out toward the rect's edge ring so the patch blends into the terrain around it. One undo step. Returns the mutation fields plus the corner counts.")]
    public HeightResult HeightRaise(
        [Description("Corner rect's west edge, corner x, inclusive.")] int x,
        [Description("Corner rect's south edge, corner z, inclusive.")] int z,
        [Description("Corner rect width, x + width exclusive.")] int width,
        [Description("Corner rect height, z + height exclusive.")] int height,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("How far to move each corner, in centimetres. Negative lowers.")] int deltaCm,
        [Description("How much the delta fades toward the rect's edge, 0 to 1. 0 (the default) moves every corner by the full delta, 1 fades to nothing at the edge ring.")] float falloff = 0f)
        => ToolGuard.Guard(() => mutate.HeightsRaise(new TileRect(x, z, width, height), plane, deltaCm, falloff));

    /// <summary>Levels a corner rect.</summary>
    [McpServerTool(Name = "height_flatten"), Description("Levels every corner of a rect to one height, either the height given or the rect's own rounded average when none is given. Use this to seat a building on flat ground. One undo step. Returns the mutation fields plus the corner counts.")]
    public HeightResult HeightFlatten(
        [Description("Corner rect's west edge, corner x, inclusive.")] int x,
        [Description("Corner rect's south edge, corner z, inclusive.")] int z,
        [Description("Corner rect width, x + width exclusive.")] int width,
        [Description("Corner rect height, z + height exclusive.")] int height,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("The height to level to, in centimetres. Null levels to the rect's own rounded average.")] short? toCm = null)
        => ToolGuard.Guard(() => mutate.HeightsFlatten(new TileRect(x, z, width, height), plane, toCm));

    /// <summary>Blurs a corner rect.</summary>
    [McpServerTool(Name = "height_smooth"), Description("Runs an iterated box blur over the corners of a rect, blending it into the terrain around it. More iterations means a softer result. One undo step. Returns the mutation fields plus the corner counts.")]
    public HeightResult HeightSmooth(
        [Description("Corner rect's west edge, corner x, inclusive.")] int x,
        [Description("Corner rect's south edge, corner z, inclusive.")] int z,
        [Description("Corner rect width, x + width exclusive.")] int width,
        [Description("Corner rect height, z + height exclusive.")] int height,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("How many blur passes to run. Defaults to 1.")] int iterations = 1)
        => ToolGuard.Guard(() => mutate.HeightsSmooth(new TileRect(x, z, width, height), plane, iterations));

    /// <summary>Reads the corner-height lattice.</summary>
    [McpServerTool(Name = "height_get_rect"), Description("Reads the corner heights of a rect in centimetres, NORTH FIRST: row 0 is the highest z, each row west to east. Hand the rows straight to height_set to write them back unchanged.")]
    public HeightMapResult HeightGetRect(
        [Description("Corner rect's west edge, corner x, inclusive.")] int x,
        [Description("Corner rect's south edge, corner z, inclusive.")] int z,
        [Description("Corner rect width, x + width exclusive.")] int width,
        [Description("Corner rect height, z + height exclusive.")] int height,
        [Description("Plane index, 0 is the ground storey.")] int plane)
        => ToolGuard.Guard(() => query.HeightGetRect(new TileRect(x, z, width, height), plane));

    /// <summary>Resamples a PGM heightmap onto a corner rect.</summary>
    [McpServerTool(Name = "height_import"), Description("Resamples a binary PGM (P5, 8 or 16 bit) heightmap onto a rect of corners, mapping its greyscale linearly onto the given centimetre range, black to minCm and white to maxCm. The image's own row 0 is treated as the NORTH edge. PNG is not accepted because the engine ships no PNG decoder, so convert first. One undo step. Returns the mutation fields plus the corner counts.")]
    public HeightResult HeightImport(
        [Description("Path to the binary PGM (P5) file. A relative path resolves against the OPEN WORLD's directory, not the process working directory.")] string pgmPath,
        [Description("Corner rect's west edge, corner x, inclusive.")] int x,
        [Description("Corner rect's south edge, corner z, inclusive.")] int z,
        [Description("Corner rect width, x + width exclusive.")] int width,
        [Description("Corner rect height, z + height exclusive.")] int height,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("The height in centimetres that pure black maps to.")] short minCm,
        [Description("The height in centimetres that pure white maps to.")] short maxCm)
        => ToolGuard.Guard(() => mutate.HeightsImport(pgmPath, new TileRect(x, z, width, height), plane, minCm, maxCm));
}
