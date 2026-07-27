using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KhaozEngine.MapEdit.Tools;

/// <summary>Document lifecycle verbs: open, create, save, validate, and summarize the one open map document. Every
/// method is a thin wrapper that delegates to <see cref="MapEditSession"/> through
/// <see cref="ToolGuard.Guard{T}"/>, holding no logic of its own. Coordinates throughout the tool surface are the
/// engine's world frame: X and Z span the ground plane and Y is up, all lengths in meters.</summary>
[McpServerToolType]
public sealed class DocumentTools(MapEditSession session)
{
    [McpServerTool(Name = "map_open"), Description("Opens a map document (.map.json), replacing any open document. Optional asset manifest paths enable kind-aware rendering.")]
    public OpenResult MapOpen(
        [Description("Absolute or working-directory-relative path to the .map.json file.")] string path,
        [Description("Asset manifest json paths (props and buildings) used to resolve kinds for renders. Null when none.")] string[]? manifestPaths = null)
        => ToolGuard.Guard(() => session.Open(path, manifestPaths));

    [McpServerTool(Name = "map_create"), Description("Creates a fresh map document with one default open Meadow biome band, validates and saves it, then keeps it open.")]
    public OpenResult MapCreate(
        [Description("Absolute or working-directory-relative path to write the new .map.json file.")] string path,
        [Description("Stable document id (machine key), unique per map.")] string id,
        [Description("Human-readable display name for the map.")] string displayName,
        [Description("Minimum world X bound in meters (ground plane).")] float minX,
        [Description("Minimum world Z bound in meters (ground plane).")] float minZ,
        [Description("Maximum world X bound in meters (ground plane).")] float maxX,
        [Description("Maximum world Z bound in meters (ground plane).")] float maxZ,
        [Description("Terrain noise seed. Defaults to 1.")] int seed = 1,
        [Description("World water level height in meters (Y is up). Defaults to 0.")] float waterLevel = 0f,
        [Description("When true, replaces an existing file at the path. Defaults to false, which fails if the file exists.")] bool overwrite = false,
        [Description("Asset manifest json paths (props and buildings) used to resolve kinds for renders. Null when none.")] string[]? manifestPaths = null)
        => ToolGuard.Guard(() => session.Create(path, id, displayName, minX, minZ, maxX, maxZ, seed, waterLevel, overwrite, manifestPaths));

    [McpServerTool(Name = "map_save"), Description("Saves the open document to its path (validates first, failing on an invalid document) and clears the dirty flag.")]
    public SaveResult MapSave()
        => ToolGuard.Guard(session.Save);

    [McpServerTool(Name = "map_validate"), Description("Validates the open document: structural (semantic) checks, then a JSON schema check when the structure passes. Reports both results with their errors.")]
    public ValidateResult MapValidate()
        => ToolGuard.Guard(session.Validate);

    [McpServerTool(Name = "map_summary"), Description("Returns a flat summary of the open document: identity, bounds, terrain seed and water level, feature types in fold order, layer and companion names, section counts, player spawn ids, region names, and the dirty flag.")]
    public MapSummary MapSummary()
        => ToolGuard.Guard(session.Summary);

    [McpServerTool(Name = "set_window"), Description("Moves the loaded window of the open tiled document: reloads the manifest plus only the tiles inside the given world rect, discarding whatever this session held before. Refuses with unsaved changes unless discard is passed.")]
    public WindowStatusResult SetWindow(
        [Description("Minimum world X of the window rect, meters.")] float minX,
        [Description("Minimum world Z of the window rect, meters.")] float minZ,
        [Description("Maximum world X of the window rect, meters.")] float maxX,
        [Description("Maximum world Z of the window rect, meters.")] float maxZ,
        [Description("When true, moves the window even with unsaved changes, losing them. Defaults to false.")] bool discard = false)
        => ToolGuard.Guard(() => session.SetWindow(minX, minZ, maxX, maxZ, discard));

    [McpServerTool(Name = "window_status"), Description("Reports the loaded window of the open document: whether it is tiled, whether a window is loaded (vs. the whole world), the window's tile and world rect, and the occupied/loaded tile counts.")]
    public WindowStatusResult WindowStatus()
        => ToolGuard.Guard(session.WindowStatus);

    [McpServerTool(Name = "convert_to_tiled"), Description("Converts the open document to the tiled form (a directory of map.json plus content-addressed tile files) at the given directory, explicitly, preserving tileSize and the world hash exactly.")]
    public ConvertResult ConvertToTiled(
        [Description("Absolute or working-directory-relative path to the tiled document DIRECTORY to write.")] string directory)
        => ToolGuard.Guard(() => session.ConvertToTiled(directory));

    [McpServerTool(Name = "convert_to_single"), Description("Converts the open document to the monolithic form (one .map.json file) at the given path, explicitly, preserving tileSize and the world hash exactly.")]
    public ConvertResult ConvertToSingle(
        [Description("Absolute or working-directory-relative path to the .map.json FILE to write.")] string path)
        => ToolGuard.Guard(() => session.ConvertToSingle(path));

    [McpServerTool(Name = "retile"), Description("Sets the document's tileSize and re-saves it at its own path. tileSize is part of world identity, so this changes the world hash: the result's Warning states the before/after digests, and a re-tile needs a coordinated client and server release.")]
    public RetileResult Retile(
        [Description("New document tile edge in meters. Must be positive and finite.")] float tileSize)
        => ToolGuard.Guard(() => session.Retile(tileSize));
}
