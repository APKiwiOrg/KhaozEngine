using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KhaozEngine.MapEdit.Tools;

/// <summary>Read-only world queries over the open document: ground sampling, walkability, rect scans over
/// placements, NPC spawns, and player spawns, a scatter layer preview, a brute-force flat-area search, and the
/// full procedural setup (terrain scalars, biome bands, scatter layers, companion layers). Every method is a
/// thin wrapper that delegates to <see cref="QueryService"/> through <see cref="ToolGuard.Guard{T}"/>. Coordinates
/// are the engine's world frame: X and Z span the ground plane and Y is up, all lengths in meters, angles in
/// degrees.</summary>
[McpServerToolType]
public sealed class QueryTools(QueryService query)
{
    [McpServerTool(Name = "ground_height"), Description("Samples ground height, slope, and water state at a single world point.")]
    public GroundInfo GroundHeight(
        [Description("World X in meters (ground plane).")] float x,
        [Description("World Z in meters (ground plane).")] float z)
        => ToolGuard.Guard(() => query.GroundHeight(x, z));

    [McpServerTool(Name = "is_walkable"), Description("Reports whether a world point is walkable, composing the engine's slope gate with a water gate. Submerged ground is never walkable regardless of slope.")]
    public WalkableInfo IsWalkable(
        [Description("World X in meters (ground plane).")] float x,
        [Description("World Z in meters (ground plane).")] float z,
        [Description("Maximum walkable slope in degrees, measured from vertical (a flat surface is 0). Defaults to 45.")] float maxSlopeDegrees = 45f)
        => ToolGuard.Guard(() => query.IsWalkable(x, z, maxSlopeDegrees));

    [McpServerTool(Name = "placements_in_rect"), Description("Lists placements, NPC spawns, and player spawns whose position falls inside the inclusive world rect. A null-Y placement resolves to sampled ground height.")]
    public PlacementsInRectResult PlacementsInRect(
        [Description("Minimum world X of the rect in meters (ground plane).")] float minX,
        [Description("Minimum world Z of the rect in meters (ground plane).")] float minZ,
        [Description("Maximum world X of the rect in meters (ground plane).")] float maxX,
        [Description("Maximum world Z of the rect in meters (ground plane).")] float maxZ)
        => ToolGuard.Guard(() => query.PlacementsInRect(minX, minZ, maxX, maxZ));

    [McpServerTool(Name = "scatter_preview_in_rect"), Description("Previews a scatter layer's generated props over a world rect without baking them. Reports the full generated count and caps returned entries, flagging truncation.")]
    public ScatterPreviewResult ScatterPreviewInRect(
        [Description("Name of the scatter layer to preview.")] string layer,
        [Description("Minimum world X of the rect in meters (ground plane).")] float minX,
        [Description("Minimum world Z of the rect in meters (ground plane).")] float minZ,
        [Description("Maximum world X of the rect in meters (ground plane).")] float maxX,
        [Description("Maximum world Z of the rect in meters (ground plane).")] float maxZ,
        [Description("Maximum number of preview entries to return. Defaults to 500.")] int maxResults = 500)
        => ToolGuard.Guard(() => query.ScatterPreviewInRect(layer, minX, minZ, maxX, maxZ, maxResults));

    [McpServerTool(Name = "find_flat_area"), Description("Brute-force grid scan for flat, buildable spots of the given radius. Each candidate samples its center plus two rings and must clear the slope, water, and height-spread gates. Results sort by max slope, then height spread, then position.")]
    public FlatAreaResult FindFlatArea(
        [Description("Radius of the flat disc to find, in meters.")] float radius,
        [Description("Maximum slope in degrees, measured from vertical, allowed at any sampled point. Defaults to 30.")] float maxSlopeDegrees = 30f,
        [Description("Maximum allowed height spread across the sampled points, in meters. Defaults to 1.")] float maxHeightSpread = 1.0f,
        [Description("Minimum world X of the search rect in meters. Null uses the document bounds.")] float? minX = null,
        [Description("Minimum world Z of the search rect in meters. Null uses the document bounds.")] float? minZ = null,
        [Description("Maximum world X of the search rect in meters. Null uses the document bounds.")] float? maxX = null,
        [Description("Maximum world Z of the search rect in meters. Null uses the document bounds.")] float? maxZ = null,
        [Description("When true, every sampled point must be above water. Defaults to true.")] bool aboveWater = true,
        [Description("Maximum number of flat spots to return. Defaults to 5.")] int maxResults = 5)
        => ToolGuard.Guard(() => query.FindFlatArea(radius, maxSlopeDegrees, maxHeightSpread, minX, minZ, maxX, maxZ, aboveWater, maxResults));

    [McpServerTool(Name = "procedural_info"), Description("Reads the full procedural setup of the open document: terrain scalars, biome bands, scatter layers (with their rules and kinds), and companion layers, at full field fidelity. The read counterpart to terrain_edit, the biome band triad, and the scatter/companion layer triads.")]
    public ProceduralInfo ProceduralInfo()
        => ToolGuard.Guard(query.ProceduralInfo);
}
