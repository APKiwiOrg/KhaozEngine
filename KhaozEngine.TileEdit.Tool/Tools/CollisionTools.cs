using System.ComponentModel;
using KhaozEngine.TileWorld;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>The derived collision layer: what one tile blocks, whether an agent fits, whether two tiles connect,
/// and a walkability map of a rect. Every method delegates to <see cref="QueryService"/> through
/// <see cref="ToolGuard.Guard{T}"/>, and nothing here mutates.
///
/// <para>Collision is DERIVED, never authored: it is baked from the tiles' own Blocked setting plus the
/// collision kind of every object standing on them, and the document rebakes it after each edit. So the way to
/// change what these verbs report is to paint a setting or place an object, and then ask again.</para></summary>
[McpServerToolType]
public sealed class CollisionTools(QueryService query)
{
    /// <summary>The derived collision at one tile.</summary>
    [McpServerTool(Name = "collision_at"), Description("Reads the derived collision at one tile: the flag names, whether it is blocked outright, and whether a one-tile agent standing there could step north, east, south or west. Use this to work out why a path refuses to go somewhere.")]
    public CollisionInfo CollisionAt(
        [Description("Tile x (east).")] int x,
        [Description("Tile z (north).")] int z,
        [Description("Plane index, 0 is the ground storey.")] int plane)
        => ToolGuard.Guard(() => query.CollisionAt(x, z, plane));

    /// <summary>Whether an agent of a given size stands clear.</summary>
    [McpServerTool(Name = "is_walkable"), Description("Reports whether an agent that many tiles square, anchored at this tile and extending north and east, stands clear: every tile of that footprint must be unblocked. This is the same footprint rule the pathfinder walks with. Returns the flag, the agent size and the anchor tile's collision flags.")]
    public WalkableInfo IsWalkable(
        [Description("Anchor tile x (east).")] int x,
        [Description("Anchor tile z (north).")] int z,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("Agent footprint edge in tiles, at least 1. Defaults to 1.")] int agentSize = 1)
        => ToolGuard.Guard(() => query.IsWalkable(x, z, plane, agentSize));

    /// <summary>The walk between two tiles on one plane.</summary>
    [McpServerTool(Name = "path"), Description("Finds the walk from one tile to another on one plane and returns the tiles it steps through. An unreachable goal still returns the steps to the NEAREST reachable tile with reached false, and so does a goal that is simply further away than the search radius, which is a search-window limit rather than a verdict on the world. Widen maxRadius before concluding a place is cut off.")]
    public PathResult Path(
        [Description("Start tile x (east).")] int fromX,
        [Description("Start tile z (north).")] int fromZ,
        [Description("Goal tile x.")] int toX,
        [Description("Goal tile z.")] int toZ,
        [Description("Plane index, 0 is the ground storey. The search never changes plane.")] int plane,
        [Description("Agent footprint edge in tiles, at least 1. Defaults to 1.")] int agentSize = 1,
        [Description("How far from the start, in tiles, the search may look. Defaults to 64. A goal outside it reports reached false with a partial walk.")] int maxRadius = TilePathfinder.DefaultMaxRadius)
        => ToolGuard.Guard(() => query.Path(fromX, fromZ, toX, toZ, plane, agentSize, maxRadius));

    /// <summary>An ASCII walkability map of a rect.</summary>
    [McpServerTool(Name = "walkable_rect"), Description("Maps what a one-tile agent could stand on over a rect, one character per tile, NORTH FIRST (row 0 is the highest z) and west to east within a row, with the legend that decodes it. The quick way to see the shape of a room or a road before pathing through it.")]
    public TileMapResult WalkableRect(
        [Description("Rect's west edge, tile x, inclusive.")] int x,
        [Description("Rect's south edge, tile z, inclusive.")] int z,
        [Description("Rect width in tiles, x + width exclusive.")] int width,
        [Description("Rect height in tiles, z + height exclusive.")] int height,
        [Description("Plane index, 0 is the ground storey.")] int plane)
        => ToolGuard.Guard(() => query.WalkableRect(new TileRect(x, z, width, height), plane));
}
