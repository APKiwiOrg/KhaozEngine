using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>Named markers: set, remove, list. Every method delegates to <see cref="QueryService"/> or
/// <see cref="MutationService"/> through <see cref="ToolGuard.Guard{T}"/>.
///
/// <para>A marker is a uniquely named point on a tile, carrying tags and nothing else. It is how a world names
/// the places a game needs to find later (a spawn, a shop door, a quest step) without inventing an object for
/// them, so markers draw nothing and block nothing.</para></summary>
[McpServerToolType]
public sealed class MarkerTools(QueryService query, MutationService mutate)
{
    /// <summary>Places or re-homes a named marker.</summary>
    [McpServerTool(Name = "marker_set"), Description("Places the named marker, or moves it when the name is already taken (names are unique world-wide). Markers draw nothing and block nothing, they name a place for the game to look up. One undo step. Returns the undo label, the dirty flag, the undo depth, the new world hash and the rects touched.")]
    public MutationResult MarkerSet(
        [Description("Unique marker name, the key a game looks it up by.")] string name,
        [Description("Tile x (east).")] int x,
        [Description("Tile z (north).")] int z,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("Authoring tags to attach. Null means none.")] string[]? tags = null)
        => ToolGuard.Guard(() => mutate.MarkerSet(name, x, z, plane, tags));

    /// <summary>Deletes a named marker.</summary>
    [McpServerTool(Name = "marker_remove"), Description("Deletes the named marker. One undo step, and the undo puts it back with its tags. Returns the mutation fields.")]
    public MutationResult MarkerRemove(
        [Description("The marker name.")] string name)
        => ToolGuard.Guard(() => mutate.MarkerRemove(name));

    /// <summary>Every marker in the world.</summary>
    [McpServerTool(Name = "marker_list"), Description("Lists every marker in the whole world in name order, each with its tile, plane and tags.")]
    public IReadOnlyList<MarkerInfo> MarkerList()
        => ToolGuard.Guard(query.MarkerList);
}
