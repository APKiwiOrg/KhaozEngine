using System;
using System.ComponentModel;
using KhaozEngine.TileWorld;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>The authored tile layers: read one tile in full, read an ASCII map of a rect, and paint a rect or a
/// single tile. Every method delegates to <see cref="QueryService"/> or <see cref="MutationService"/> through
/// <see cref="ToolGuard.Guard{T}"/>.
///
/// <para>Tile space throughout: x east, z north, plane a storey index from 0, rects given as (x, z, width,
/// height) with the far edges EXCLUSIVE. Every map this tool returns runs NORTH FIRST, so row 0 is the highest z
/// of the rect and each row runs west to east, which is the way round a top-down render reads.</para></summary>
[McpServerToolType]
public sealed class TileTools(QueryService query, MutationService mutate)
{
    /// <summary>The overlay shape names <c>shape</c> accepts, case-insensitively.</summary>
    public const string ShapeNames = "Full, DiagonalHalf, CornerQuarter, CornerThreeQuarter";

    /// <summary>The tile setting flag names <c>settings</c> accepts, case-insensitively, comma separated.</summary>
    public const string SettingNames = "None, Blocked, Indoors, Bridge, NoDraw";

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
        [Description("Underlay (ground) material id from the catalog, or 0 for void. Null leaves it alone.")] int? underlay = null,
        [Description("Overlay material id from the catalog, or 0 for none. Null leaves it alone.")] int? overlay = null,
        [Description("How the overlay cuts the tile, one of: Full, DiagonalHalf, CornerQuarter, CornerThreeQuarter (case-insensitive). Null leaves it alone.")] string? shape = null,
        [Description("Overlay rotation in quarter turns clockwise, 0 to 3 (0 west, 1 north, 2 east, 3 south). Null leaves it alone.")] int? rotation = null,
        [Description("Authored flags as a comma list of: None, Blocked, Indoors, Bridge, NoDraw (case-insensitive). 'none' or an empty string clears every flag. Null leaves them alone.")] string? settings = null)
        => ToolGuard.Guard(() => mutate.TilesFill(new TileRect(x, z, 1, 1), plane, Material(underlay),
            Material(overlay), ParseShape(shape), rotation, ParseSettings(settings)));

    /// <summary>Paints every tile of a rect.</summary>
    [McpServerTool(Name = "tiles_fill"), Description("Paints the authored layers of every tile in a rect. Any layer left null is not touched, so a fill can repaint the ground without disturbing what was built on it. Passing underlay 0, overlay 0, shape Full, rotation 0 and settings none CLEARS the rect back to void ground (objects and markers are not tile layers and are left alone). One undo step for the whole rect. Returns the undo label, the dirty flag, the undo depth, the new world hash and the rects touched.")]
    public MutationResult TilesFill(
        [Description("Rect's west edge, tile x, inclusive.")] int x,
        [Description("Rect's south edge, tile z, inclusive.")] int z,
        [Description("Rect width in tiles, so the east edge x + width is exclusive.")] int width,
        [Description("Rect height in tiles, so the north edge z + height is exclusive.")] int height,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("Underlay (ground) material id from the catalog, or 0 for void. Null leaves it alone.")] int? underlay = null,
        [Description("Overlay material id from the catalog, or 0 for none. Null leaves it alone.")] int? overlay = null,
        [Description("How the overlay cuts each tile, one of: Full, DiagonalHalf, CornerQuarter, CornerThreeQuarter (case-insensitive). Null leaves it alone.")] string? shape = null,
        [Description("Overlay rotation in quarter turns clockwise, 0 to 3 (0 west, 1 north, 2 east, 3 south). Null leaves it alone.")] int? rotation = null,
        [Description("Authored flags as a comma list of: None, Blocked, Indoors, Bridge, NoDraw (case-insensitive). 'none' or an empty string clears every flag. Null leaves them alone.")] string? settings = null)
        => ToolGuard.Guard(() => mutate.TilesFill(new TileRect(x, z, width, height), plane, Material(underlay),
            Material(overlay), ParseShape(shape), rotation, ParseSettings(settings)));

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

    // Material ids are ushort on the document but int on the wire, because JSON has one number type and a client
    // should not have to know the storage width. Out of range fails here rather than wrapping silently.
    static ushort? Material(int? id)
    {
        if (id is not { } value) return null;
        if ((uint)value > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(id), value,
                $"a material id must be 0..{ushort.MaxValue}.");
        return (ushort)value;
    }

    // One shape name, case-insensitive. Enum.TryParse would also accept a bare number, which would let "7" land
    // as a shape the renderer has no case for, so the parse is name-only.
    static TileOverlayShape? ParseShape(string? shape)
    {
        if (shape is null) return null;
        string name = shape.Trim();
        if (name.Length == 0) return null;
        if (!Enum.TryParse(name, ignoreCase: true, out TileOverlayShape parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException($"'{shape}' is not an overlay shape. The shapes are {ShapeNames}.",
                nameof(shape));
        return parsed;
    }

    // A comma list of flag names, OR-ed together. Parsed one name at a time rather than handing the whole string
    // to Enum.TryParse, so a typo names ITSELF in the error instead of failing the whole list anonymously.
    static TileSettings? ParseSettings(string? settings)
    {
        if (settings is null) return null;
        string list = settings.Trim();
        if (list.Length == 0) return TileSettings.None;
        TileSettings flags = TileSettings.None;
        foreach (string part in list.Split(','))
        {
            string name = part.Trim();
            if (name.Length == 0) continue;
            if (!Enum.TryParse(name, ignoreCase: true, out TileSettings parsed) || !Enum.IsDefined(parsed))
                throw new ArgumentException($"'{name}' is not a tile setting. The settings are {SettingNames}.",
                    nameof(settings));
            flags |= parsed;
        }
        return flags;
    }
}
