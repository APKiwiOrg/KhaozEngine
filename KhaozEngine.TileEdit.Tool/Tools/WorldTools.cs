using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>World lifecycle, catalog, region and history verbs over the one open tile world. Every method is a
/// thin wrapper that delegates to <see cref="TileEditSession"/>, <see cref="QueryService"/> or
/// <see cref="MutationService"/> through <see cref="ToolGuard.Guard{T}"/>, holding no logic of its own.
///
/// <para>Coordinates across the whole tool surface are TILE space: x runs east, z runs north, and a plane is a
/// storey index from 0. A rect is given as four ints (x, z, width, height) whose far edges are EXCLUSIVE, so
/// (0, 0, 64, 64) is the tiles 0..63 on both axes.</para>
///
/// <para>Paths split in two, and the split is here rather than anywhere else in the tool. The two verbs that
/// OPEN a world (<c>world_open</c> and <c>world_create</c>) name a directory before there is a world to be
/// relative to, so a relative path there resolves against the PROCESS working directory, which belongs to
/// whichever client launched the server: prefer an absolute path for both. Every other path-taking verb in the
/// tool (the heightmap import, the three prefab verbs, both renders' save paths) resolves a relative path
/// against the OPEN WORLD's own directory instead.</para></summary>
[McpServerToolType]
public sealed class WorldTools(TileEditSession session, QueryService query, MutationService mutate)
{
    /// <summary>Opens a world directory, replacing whatever was open.</summary>
    [McpServerTool(Name = "world_open"), Description("Opens the tile world in a directory (its world.json plus the regions it names), replacing any open world. Catalog paths inside the manifest resolve relative to that directory. Returns the path, the world identity and a full summary. Fails if the world or one of its catalogs cannot be loaded.")]
    public OpenResult WorldOpen(
        [Description("Absolute or working-directory-relative path to the world DIRECTORY (the one holding world.json).")] string path)
        => ToolGuard.Guard(() => session.Open(path));

    /// <summary>Creates an empty world, saves it, and keeps it open.</summary>
    [McpServerTool(Name = "world_create"), Description("Creates an empty world with one region at (0, 0), validates and saves it, then keeps it open. Catalog paths are stored in the manifest exactly as given, so a relative entry keeps the world portable. Returns the path, identity and a full summary. Refuses a directory that already holds a world.")]
    public OpenResult WorldCreate(
        [Description("Absolute or working-directory-relative path to the world DIRECTORY to create.")] string path,
        [Description("Stable machine id for the world, unique per world.")] string id,
        [Description("Human-readable display name for the world.")] string displayName,
        [Description("Catalog json paths (materials and object archetypes), relative to the world directory or absolute. Empty means no catalogs, which leaves every material and archetype id unresolvable.")] string[] catalogPaths,
        [Description("Number of vertical planes (storeys), at least 1. Defaults to 4, the OSRS-style ground plus three.")] int planeCount = 4,
        [Description("Tile edge length in world metres. Defaults to 1.")] float tileSize = 1f)
        => ToolGuard.Guard(() => session.Create(path, id, displayName, planeCount, tileSize, catalogPaths));

    /// <summary>Validates then writes the open world back to its own directory.</summary>
    [McpServerTool(Name = "world_save"), Description("Validates the open world and, when it passes, writes it back to its own directory and clears the dirty flag. An invalid world throws and nothing is written. Returns the directory and the world hash of what landed there.")]
    public SaveResult WorldSave()
        => ToolGuard.Guard(session.Save);

    /// <summary>A flat summary of the open world.</summary>
    [McpServerTool(Name = "world_summary"), Description("Returns a flat summary of the open world: id, display name, directory, plane count, tile size, region/object/marker counts, the world hash, the dirty flag, the undo and redo depths with their labels, and the manifest's catalog paths. Read this after any batch of edits to see where the history stands.")]
    public WorldSummary Summary()
        => ToolGuard.Guard(session.Summary);

    /// <summary>Validates the open world without throwing.</summary>
    [McpServerTool(Name = "world_validate"), Description("Validates the open world against its catalogs and reports every issue as '[code] message' rather than failing, so a client can ask what is wrong without a failed call. Returns a valid flag and the issue list.")]
    public ValidateResult WorldValidate()
        => ToolGuard.Guard(session.Validate);

    /// <summary>Lists the loaded catalogs' materials or object archetypes.</summary>
    [McpServerTool(Name = "catalog_list"), Description("Lists what the loaded catalogs define, so a client knows the legal material ids and archetype ids before it paints or places. Returns one record carrying both lists, with the kind that was not asked for empty.")]
    public CatalogListResult CatalogList(
        [Description("Which catalog to list: materials or archetypes.")] string kind)
        => ToolGuard.Guard(() => query.CatalogList(kind));

    /// <summary>Materialises an empty region.</summary>
    [McpServerTool(Name = "region_create"), Description("Materialises an empty 64x64 region at the given region coordinate, which is void ground until something paints it. Region (rx, rz) covers tiles x rx*64..rx*64+63 and z rz*64..rz*64+63. One undo step. Returns the undo label, the dirty flag, the undo depth, the new world hash and the rects touched.")]
    public MutationResult RegionCreate(
        [Description("Region x index (each region is 64 tiles wide, so region 1 starts at tile x 64).")] int rx,
        [Description("Region z index (each region is 64 tiles deep, so region 1 starts at tile z 64).")] int rz)
        => ToolGuard.Guard(() => mutate.RegionCreate(rx, rz));

    /// <summary>Deletes a whole region.</summary>
    [McpServerTool(Name = "region_delete"), Description("Deletes a whole region, its tile layers, objects and markers included. One undo step, and the undo puts all of it back. Returns the undo label, the dirty flag, the undo depth, the new world hash and the rects touched.")]
    public MutationResult RegionDelete(
        [Description("Region x index.")] int rx,
        [Description("Region z index.")] int rz)
        => ToolGuard.Guard(() => mutate.RegionDelete(rx, rz));

    /// <summary>Lists every region the world holds.</summary>
    [McpServerTool(Name = "region_list"), Description("Lists every region the world holds, south row first and west to east within a row, each with its tile rect and how many objects and markers are anchored in it. This is the map of where the world actually exists.")]
    public IReadOnlyList<RegionInfo> RegionList()
        => ToolGuard.Guard(query.RegionList);

    /// <summary>Steps the history backwards.</summary>
    [McpServerTool(Name = "undo"), Description("Undoes up to the given number of edits, stopping early when the stack runs out. Every verb that mutates is exactly one undo step. Returns how many steps actually moved, the dirty flag, the undo and redo depths with their labels, and the world hash after the move.")]
    public UndoResult Undo(
        [Description("How many edits to step back. Defaults to 1.")] int steps = 1)
        => ToolGuard.Guard(() => mutate.Undo(steps));

    /// <summary>Steps the history forwards.</summary>
    [McpServerTool(Name = "redo"), Description("Redoes up to the given number of undone edits, stopping early when the stack runs out. Returns how many steps actually moved, the dirty flag, the undo and redo depths with their labels, and the world hash after the move.")]
    public UndoResult Redo(
        [Description("How many edits to step forward. Defaults to 1.")] int steps = 1)
        => ToolGuard.Guard(() => mutate.Redo(steps));
}
