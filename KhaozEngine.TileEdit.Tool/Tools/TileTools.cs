using System.ComponentModel;
using KhaozEngine.TileWorld;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>The authored tile layers: read one tile in full, read an ASCII map of a rect, and paint a rect or a
/// single tile. Every method delegates to <see cref="QueryService"/> or <see cref="MutationService"/> through
/// <see cref="ToolGuard.Guard{T}"/>, with the shape, settings, rotation and material arguments checked by
/// <see cref="ToolArgs"/> first.
///
/// <para>Tile space throughout: x east, z north, plane a storey index from 0, rects given as (x, z, width,
/// height) with the far edges EXCLUSIVE. Every map this tool returns runs NORTH FIRST, so row 0 is the highest z
/// of the rect and each row runs west to east, which is the way round a top-down render reads.</para></summary>
[McpServerToolType]
public sealed class TileTools(QueryService query, MutationService mutate)
{
    /// <summary>Everything authored and derived at one tile.</summary>
    [McpServerTool(Name = "tile_get"), Description("Reads one tile in full: both material ids with their catalog names, the overlay shape and rotation, the authored settings flags, the derived collision flags and blocked state, the four corner heights in centimetres, and which region holds it. This is what to call when an ASCII map raised a question about a specific tile.")]
    public TileInfo TileGet(
        [Description("Tile x (east).")] int x,
        [Description("Tile z (north).")] int z,
        [Description("Plane index, 0 is the ground storey.")] int plane)
        => ToolGuard.Guard(() => query.TileGet(x, z, plane));

    /// <summary>Paints one tile, which is a one-by-one fill.</summary>
    [McpServerTool(Name = "tile_set"), Description("Paints ONE tile's authored layers, which is exactly a 1x1 tiles_fill. Any layer left null is not touched, so this can repaint the ground without disturbing what was built on it. One undo step. Returns the undo label, the dirty flag, the undo depth, the new world hash and the rects touched.")]
    public MutationResult TileSet(
        [Description("Tile x (east).")] int x,
        [Description("Tile z (north).")] int z,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("Underlay (ground) material id from the catalog, 0 to 65535, or 0 for void. Null leaves it alone.")] int? underlay = null,
        [Description("Overlay material id from the catalog, 0 to 65535, or 0 for none. Null leaves it alone.")] int? overlay = null,
        [Description("How the overlay cuts the tile, by NAME: Full, DiagonalHalf, CornerQuarter or CornerThreeQuarter (case-insensitive). A number is refused. Null leaves it alone.")] string? shape = null,
        [Description("Overlay rotation in quarter turns clockwise, 0 to 3 (0 west, 1 north, 2 east, 3 south). Outside that range is refused. Null leaves it alone.")] int? rotation = null,
        [Description("Authored flags as a comma list of NAMES: None, Blocked, Indoors, Bridge, NoDraw (case-insensitive). 'none' or an empty string clears every flag. A number is refused. Null leaves them alone.")] string? settings = null)
        => ToolGuard.Guard(() => mutate.TilesFill(new TileRect(x, z, 1, 1), plane,
            ToolArgs.Material(underlay, nameof(underlay)), ToolArgs.Material(overlay, nameof(overlay)),
            ToolArgs.Shape(shape), ToolArgs.Rotation(rotation), ToolArgs.Settings(settings)));

    /// <summary>Paints every tile of a rect.</summary>
    [McpServerTool(Name = "tiles_fill"), Description("Paints the authored layers of every tile in a rect. Any layer left null is not touched, so a fill can repaint the ground without disturbing what was built on it. Passing underlay 0, overlay 0, shape Full, rotation 0 and settings none CLEARS the rect back to void ground (objects and markers are not tile layers and are left alone). One undo step for the whole rect. Returns the undo label, the dirty flag, the undo depth, the new world hash and the rects touched.")]
    public MutationResult TilesFill(
        [Description("Rect's west edge, tile x, inclusive.")] int x,
        [Description("Rect's south edge, tile z, inclusive.")] int z,
        [Description("Rect width in tiles, so the east edge x + width is exclusive.")] int width,
        [Description("Rect height in tiles, so the north edge z + height is exclusive.")] int height,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("Underlay (ground) material id from the catalog, 0 to 65535, or 0 for void. Null leaves it alone.")] int? underlay = null,
        [Description("Overlay material id from the catalog, 0 to 65535, or 0 for none. Null leaves it alone.")] int? overlay = null,
        [Description("How the overlay cuts each tile, by NAME: Full, DiagonalHalf, CornerQuarter or CornerThreeQuarter (case-insensitive). A number is refused. Null leaves it alone.")] string? shape = null,
        [Description("Overlay rotation in quarter turns clockwise, 0 to 3 (0 west, 1 north, 2 east, 3 south). Outside that range is refused. Null leaves it alone.")] int? rotation = null,
        [Description("Authored flags as a comma list of NAMES: None, Blocked, Indoors, Bridge, NoDraw (case-insensitive). 'none' or an empty string clears every flag. A number is refused. Null leaves them alone.")] string? settings = null)
        => ToolGuard.Guard(() => mutate.TilesFill(new TileRect(x, z, width, height), plane,
            ToolArgs.Material(underlay, nameof(underlay)), ToolArgs.Material(overlay, nameof(overlay)),
            ToolArgs.Shape(shape), ToolArgs.Rotation(rotation), ToolArgs.Settings(settings)));

    /// <summary>An ASCII map of one layer over a rect.</summary>
    [McpServerTool(Name = "tiles_get_rect"), Description("Reads one layer of a rect as an ASCII map, one character per tile, NORTH FIRST (row 0 is the highest z) and west to east within a row, with the legend that decodes it. This is the cheap way to see a whole area's shape before drilling into tile_get.")]
    public TileMapResult TilesGetRect(
        [Description("Rect's west edge, tile x, inclusive.")] int x,
        [Description("Rect's south edge, tile z, inclusive.")] int z,
        [Description("Rect width in tiles, so the east edge x + width is exclusive.")] int width,
        [Description("Rect height in tiles, so the north edge z + height is exclusive.")] int height,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("Which layer to map: underlay, overlay, shape, settings, or collision. The first two map material ids, collision maps the DERIVED walls and blocks.")] string layer)
        => ToolGuard.Guard(() => query.TilesGetRect(new TileRect(x, z, width, height), plane, layer));
}
