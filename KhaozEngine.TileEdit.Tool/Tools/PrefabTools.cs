using System.Collections.Generic;
using System.ComponentModel;
using KhaozEngine.TileWorld;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>Prefabs: cut a patch of world out to a file, stamp one back down, and list what a directory holds.
/// Every method delegates to <see cref="QueryService"/> or <see cref="MutationService"/> through
/// <see cref="ToolGuard.Guard{T}"/>.
///
/// <para>A prefab is a json file holding a rect of tile layers, corner heights, objects and markers across a
/// span of planes. It is how a house authored once gets stamped down a street. Every path here resolves against
/// the OPEN WORLD's directory when it is relative.</para></summary>
[McpServerToolType]
public sealed class PrefabTools(QueryService query, MutationService mutate)
{
    /// <summary>Writes a rect of the world out as a prefab file.</summary>
    [McpServerTool(Name = "prefab_save"), Description("Extracts a rect of the open world into a prefab FILE: tile layers, corner heights, and optionally the objects and markers anchored inside it. This is the one write verb here that changes NOTHING about the world, so there is no undo step (deleting the file is the undo). Returns where it landed and what it carries.")]
    public PrefabSaveResult PrefabSave(
        [Description("Rect's west edge, tile x, inclusive.")] int x,
        [Description("Rect's south edge, tile z, inclusive.")] int z,
        [Description("Rect width in tiles, x + width exclusive.")] int width,
        [Description("Rect height in tiles, z + height exclusive.")] int height,
        [Description("Lowest plane to capture.")] int planeFrom,
        [Description("How many planes to capture upward from planeFrom.")] int planeCount,
        [Description("Path to write the prefab json to. A relative path resolves against the OPEN WORLD's directory. The file name without its extension becomes the prefab's name.")] string savePath,
        [Description("When true (the default), objects anchored inside the rect are captured too.")] bool includeObjects = true,
        [Description("When true (the default), markers inside the rect are captured too.")] bool includeMarkers = true)
        => ToolGuard.Guard(() => mutate.PrefabExtract(new TileRect(x, z, width, height), planeFrom, planeCount,
            savePath, includeObjects, includeMarkers));

    /// <summary>Stamps a prefab file into the world.</summary>
    [McpServerTool(Name = "prefab_place"), Description("Stamps a prefab file into the open world with its south-west corner at the given tile, turned by the given quarter turns. Everything the prefab carries (layers, heights, objects, markers) lands as a SINGLE undo step. Caveat on redo: this stamp re-runs on redo rather than restoring what it made, so the objects come back with FRESH ids and the world hash after a place, undo and redo differs from the hash after the place alone. The world is the same world, the object ids are not. Returns the mutation fields.")]
    public MutationResult PrefabPlace(
        [Description("Path to the prefab json. A relative path resolves against the OPEN WORLD's directory.")] string prefabPath,
        [Description("Tile x the prefab's west edge lands on.")] int x,
        [Description("Tile z the prefab's south edge lands on.")] int z,
        [Description("Plane the prefab's lowest plane lands on.")] int plane,
        [Description("Quarter turns clockwise to turn the whole prefab by, 0 to 3. Outside that range is refused. Defaults to 0.")] int rotation = 0)
        => ToolGuard.Guard(() => mutate.PrefabPlace(prefabPath, x, z, plane, ToolArgs.Rotation(rotation)));

    /// <summary>Lists the prefab files in a directory.</summary>
    [McpServerTool(Name = "prefab_list"), Description("Lists the prefab json files in a directory, by name, each with its full path and size in bytes. A relative directory resolves against the OPEN WORLD's directory. Fails when the directory does not exist.")]
    public IReadOnlyList<PrefabFileInfo> PrefabList(
        [Description("Directory to list. A relative path resolves against the OPEN WORLD's directory.")] string directory)
        => ToolGuard.Guard(() => query.PrefabList(directory));
}
