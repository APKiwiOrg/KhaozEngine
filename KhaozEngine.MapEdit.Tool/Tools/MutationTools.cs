using System.ComponentModel;
using KhaozEngine.MapDoc;
using ModelContextProtocol.Server;

namespace KhaozEngine.MapEdit.Tools;

/// <summary>Mutation verbs: placements, spawns, regions, terrain globals, terrain features, scatter exclusions and
/// overrides, and region bake. Every method is a thin wrapper that delegates to <see cref="MutationService"/>
/// through <see cref="ToolGuard.Guard{T}"/>, holding no logic of its own. Terrain features and shapes cross the
/// MCP boundary as JSON strings because both are open or polymorphic unions, so the feature and shape parameters
/// take raw json parsed with the document's own serializer options. Region shapes are the two verbs that parse
/// here (via <see cref="DocJson.ParseShape"/>) before calling the shape-typed service methods, using the open
/// document's registry through <see cref="MapEditSession"/>. Coordinates are the engine's world frame: X and Z
/// span the ground plane and Y is up, all lengths in meters, yaw in radians.</summary>
[McpServerToolType]
public sealed class MutationTools(MutationService mutation, MapEditSession session)
{
    // ---- placements ---------------------------------------------------------------------------------------

    [McpServerTool(Name = "placement_add"), Description("Adds an authored placement. A null id auto-generates p-<kind>-N. A null y keeps the placement ground-snapped, and the result always reports the resolved ground height.")]
    public MutationResult PlacementAdd(
        [Description("Prop or building kind id to place.")] string kind,
        [Description("World X in meters (ground plane).")] float x,
        [Description("World Z in meters (ground plane).")] float z,
        [Description("Explicit world Y height in meters (Y is up). Null keeps the placement snapped to ground at load.")] float? y = null,
        [Description("Rotation about the vertical Y axis in radians. Defaults to 0.")] float yaw = 0f,
        [Description("Uniform scale multiplier. Defaults to 1.")] float scale = 1f,
        [Description("Explicit placement id. Null auto-generates p-<kind>-N.")] string? id = null,
        [Description("Freeform tags to attach to the placement. Null for none.")] string[]? tags = null)
        => ToolGuard.Guard(() => mutation.PlacementAdd(kind, x, z, y, yaw, scale, id, tags));

    [McpServerTool(Name = "placement_move"), Description("Moves a placement to a new world XZ. By default the placement re-snaps to ground. Pass y to set an explicit height, or keepExplicitY to preserve the placement's current height.")]
    public MutationResult PlacementMove(
        [Description("Id of the placement to move.")] string id,
        [Description("New world X in meters (ground plane).")] float x,
        [Description("New world Z in meters (ground plane).")] float z,
        [Description("Explicit world Y height in meters (Y is up). Null defers to keepExplicitY.")] float? y = null,
        [Description("When true and y is null, preserves the placement's current explicit Y instead of re-snapping to ground. Defaults to false.")] bool keepExplicitY = false)
        => ToolGuard.Guard(() => mutation.PlacementMove(id, x, z, y, keepExplicitY));

    [McpServerTool(Name = "placement_rotate"), Description("Sets a placement's yaw.")]
    public MutationResult PlacementRotate(
        [Description("Id of the placement to rotate.")] string id,
        [Description("New rotation about the vertical Y axis in radians.")] float yaw)
        => ToolGuard.Guard(() => mutation.PlacementRotate(id, yaw));

    [McpServerTool(Name = "placement_scale"), Description("Sets a placement's uniform scale.")]
    public MutationResult PlacementScale(
        [Description("Id of the placement to scale.")] string id,
        [Description("New uniform scale multiplier.")] float scale)
        => ToolGuard.Guard(() => mutation.PlacementScale(id, scale));

    [McpServerTool(Name = "placement_rename"), Description("Renames a placement. The new id must be unique in the document.")]
    public MutationResult PlacementRename(
        [Description("Current id of the placement.")] string oldId,
        [Description("New id for the placement, unique in the document.")] string newId)
        => ToolGuard.Guard(() => mutation.PlacementRename(oldId, newId));

    [McpServerTool(Name = "placement_remove"), Description("Removes a placement by id.")]
    public MutationResult PlacementRemove(
        [Description("Id of the placement to remove.")] string id)
        => ToolGuard.Guard(() => mutation.PlacementRemove(id));

    // ---- spawns -------------------------------------------------------------------------------------------

    [McpServerTool(Name = "spawn_add"), Description("Adds an NPC spawn marker. A null id auto-generates s-<archetypeId>-N.")]
    public MutationResult SpawnAdd(
        [Description("Archetype id of the NPC to spawn.")] string archetypeId,
        [Description("World X in meters (ground plane).")] float x,
        [Description("World Z in meters (ground plane).")] float z,
        [Description("Whether the spawn is enabled. Defaults to true.")] bool enabled = true,
        [Description("Explicit spawn id. Null auto-generates s-<archetypeId>-N.")] string? id = null,
        [Description("Freeform tags to attach to the spawn. Null for none.")] string[]? tags = null)
        => ToolGuard.Guard(() => mutation.SpawnAdd(archetypeId, x, z, enabled, id, tags));

    [McpServerTool(Name = "spawn_move"), Description("Moves a spawn to a new world XZ.")]
    public MutationResult SpawnMove(
        [Description("Id of the spawn to move.")] string id,
        [Description("New world X in meters (ground plane).")] float x,
        [Description("New world Z in meters (ground plane).")] float z)
        => ToolGuard.Guard(() => mutation.SpawnMove(id, x, z));

    [McpServerTool(Name = "spawn_set_enabled"), Description("Toggles a spawn's enabled flag.")]
    public MutationResult SpawnSetEnabled(
        [Description("Id of the spawn to toggle.")] string id,
        [Description("New enabled state for the spawn.")] bool enabled)
        => ToolGuard.Guard(() => mutation.SpawnSetEnabled(id, enabled));

    [McpServerTool(Name = "spawn_rename"), Description("Renames a spawn. The new id must be unique in the document.")]
    public MutationResult SpawnRename(
        [Description("Current id of the spawn.")] string oldId,
        [Description("New id for the spawn, unique in the document.")] string newId)
        => ToolGuard.Guard(() => mutation.SpawnRename(oldId, newId));

    [McpServerTool(Name = "spawn_remove"), Description("Removes a spawn by id.")]
    public MutationResult SpawnRemove(
        [Description("Id of the spawn to remove.")] string id)
        => ToolGuard.Guard(() => mutation.SpawnRemove(id));

    // ---- terrain globals + features -----------------------------------------------------------------------

    [McpServerTool(Name = "terrain_edit"), Description("Edits the terrain globals: water level and/or noise seed. At least one must be supplied. Both change together under one validated, world-affecting mutation.")]
    public MutationResult TerrainEdit(
        [Description("New world water level height in meters (Y is up). Null leaves it unchanged.")] float? waterLevel = null,
        [Description("New terrain noise seed. Null leaves it unchanged.")] int? seed = null)
        => ToolGuard.Guard(() => mutation.TerrainEdit(waterLevel, seed));

    [McpServerTool(Name = "feature_add"), Description("Appends a terrain feature parsed from json (a registry-open tagged union keyed by a type discriminator). An unknown discriminator fails with a precise message. The result reports the appended index.")]
    public MutationResult FeatureAdd(
        [Description("Terrain feature as a json object, including its type discriminator (for example lake or flatten).")] string featureJson)
        => ToolGuard.Guard(() => mutation.FeatureAdd(featureJson));

    [McpServerTool(Name = "feature_edit"), Description("Replaces the terrain feature at the given index with one parsed from json.")]
    public MutationResult FeatureEdit(
        [Description("Zero-based index of the feature to replace, in fold order.")] int index,
        [Description("Replacement terrain feature as a json object, including its type discriminator.")] string featureJson)
        => ToolGuard.Guard(() => mutation.FeatureEdit(index, featureJson));

    [McpServerTool(Name = "feature_remove"), Description("Removes the terrain feature at the given index.")]
    public MutationResult FeatureRemove(
        [Description("Zero-based index of the feature to remove, in fold order.")] int index)
        => ToolGuard.Guard(() => mutation.FeatureRemove(index));

    [McpServerTool(Name = "feature_reorder"), Description("Moves a terrain feature from one index to another. Features fold in list order, so this picks the winner between overlapping features.")]
    public MutationResult FeatureReorder(
        [Description("Zero-based current index of the feature.")] int fromIndex,
        [Description("Zero-based target index for the feature.")] int toIndex)
        => ToolGuard.Guard(() => mutation.FeatureReorder(fromIndex, toIndex));

    // ---- exclusions ---------------------------------------------------------------------------------------

    [McpServerTool(Name = "exclusion_add"), Description("Appends a scatter exclusion over a shape parsed from json, optionally filtered to specific layers. A null layer filter applies to every scatter layer.")]
    public MutationResult ExclusionAdd(
        [Description("Exclusion shape as a json object (disc, rect, or polygon, keyed by its type discriminator).")] string shapeJson,
        [Description("Scatter layer names the exclusion applies to. Null applies to every layer.")] string[]? layers = null)
        => ToolGuard.Guard(() => mutation.ExclusionAdd(shapeJson, layers));

    [McpServerTool(Name = "exclusion_edit"), Description("Replaces the shape of the exclusion at the given index with one parsed from json.")]
    public MutationResult ExclusionEdit(
        [Description("Zero-based index of the exclusion to edit.")] int index,
        [Description("Replacement exclusion shape as a json object (disc, rect, or polygon).")] string shapeJson)
        => ToolGuard.Guard(() => mutation.ExclusionEdit(index, shapeJson));

    [McpServerTool(Name = "exclusion_remove"), Description("Removes the exclusion at the given index.")]
    public MutationResult ExclusionRemove(
        [Description("Zero-based index of the exclusion to remove.")] int index)
        => ToolGuard.Guard(() => mutation.ExclusionRemove(index));

    // ---- scatter overrides --------------------------------------------------------------------------------

    [McpServerTool(Name = "scatter_override_add"), Description("Appends a scatter override (density multiplier and/or kind substitution) over a shape parsed from json, optionally filtered to specific layers.")]
    public MutationResult ScatterOverrideAdd(
        [Description("Override shape as a json object (disc, rect, or polygon).")] string shapeJson,
        [Description("Density multiplier applied inside the shape. Defaults to 1.")] float densityMultiplier = 1f,
        [Description("Substitute kinds, each 'id' (weight 1) or 'id:weight'. Null leaves kinds unchanged.")] string[]? kinds = null,
        [Description("Scatter layer names the override applies to. Null applies to every layer.")] string[]? layers = null)
        => ToolGuard.Guard(() => mutation.ScatterOverrideAdd(shapeJson, densityMultiplier, kinds, layers));

    [McpServerTool(Name = "scatter_override_edit"), Description("Edits the scatter override at the given index, replacing only the supplied fields. A null argument leaves that field unchanged.")]
    public MutationResult ScatterOverrideEdit(
        [Description("Zero-based index of the scatter override to edit.")] int index,
        [Description("Replacement shape as a json object (disc, rect, or polygon). Null leaves the shape unchanged.")] string? shapeJson = null,
        [Description("New density multiplier. Null leaves it unchanged.")] float? densityMultiplier = null,
        [Description("New substitute kinds, each 'id' (weight 1) or 'id:weight'. Null leaves kinds unchanged.")] string[]? kinds = null,
        [Description("New scatter layer filter. Null leaves the filter unchanged.")] string[]? layers = null)
        => ToolGuard.Guard(() => mutation.ScatterOverrideEdit(index, shapeJson, densityMultiplier, kinds, layers));

    [McpServerTool(Name = "scatter_override_remove"), Description("Removes the scatter override at the given index.")]
    public MutationResult ScatterOverrideRemove(
        [Description("Zero-based index of the scatter override to remove.")] int index)
        => ToolGuard.Guard(() => mutation.ScatterOverrideRemove(index));

    // ---- bake ---------------------------------------------------------------------------------------------

    [McpServerTool(Name = "bake_region"), Description("Freezes a scatter layer over a world rect into authored placements (each baked-<layer>-N with an explicit Y and a baked tag) and appends a covering exclusion limited to that layer so the frozen props are not re-scattered over themselves.")]
    public BakeResult BakeRegion(
        [Description("Name of the scatter layer to freeze.")] string layer,
        [Description("Minimum world X of the rect in meters (ground plane).")] float minX,
        [Description("Minimum world Z of the rect in meters (ground plane).")] float minZ,
        [Description("Maximum world X of the rect in meters (ground plane).")] float maxX,
        [Description("Maximum world Z of the rect in meters (ground plane).")] float maxZ)
        => ToolGuard.Guard(() => mutation.BakeRegion(layer, minX, minZ, maxX, maxZ));

    // ---- regions ------------------------------------------------------------------------------------------

    [McpServerTool(Name = "region_add"), Description("Adds a named region marker over a shape parsed from json.")]
    public MutationResult RegionAdd(
        [Description("Region name, unique in the document.")] string name,
        [Description("Region shape as a json object (disc, rect, or polygon, keyed by its type discriminator).")] string shapeJson,
        [Description("Freeform tags to attach to the region. Null for none.")] string[]? tags = null)
        => ToolGuard.Guard(() =>
        {
            MapShapeDoc shape = session.WithDocument((_, registry) => DocJson.ParseShape(shapeJson, registry));
            return mutation.RegionAdd(name, shape, tags);
        });

    [McpServerTool(Name = "region_edit_shape"), Description("Replaces a region's shape with one parsed from json.")]
    public MutationResult RegionEditShape(
        [Description("Name of the region to edit.")] string name,
        [Description("Replacement region shape as a json object (disc, rect, or polygon).")] string shapeJson)
        => ToolGuard.Guard(() =>
        {
            MapShapeDoc shape = session.WithDocument((_, registry) => DocJson.ParseShape(shapeJson, registry));
            return mutation.RegionEditShape(name, shape);
        });

    [McpServerTool(Name = "region_rename"), Description("Renames a region. The new name must be unique in the document.")]
    public MutationResult RegionRename(
        [Description("Current name of the region.")] string oldName,
        [Description("New name for the region, unique in the document.")] string newName)
        => ToolGuard.Guard(() => mutation.RegionRename(oldName, newName));

    [McpServerTool(Name = "region_remove"), Description("Removes a region by name.")]
    public MutationResult RegionRemove(
        [Description("Name of the region to remove.")] string name)
        => ToolGuard.Guard(() => mutation.RegionRemove(name));
}
