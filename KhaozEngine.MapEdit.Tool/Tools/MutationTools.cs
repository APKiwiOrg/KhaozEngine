using System.ComponentModel;
using KhaozEngine.MapDoc;
using ModelContextProtocol.Server;

namespace KhaozEngine.MapEdit.Tools;

/// <summary>Mutation verbs: placements, spawns, regions, terrain globals, terrain features and biome bands,
/// scatter exclusions and overrides, scatter and companion layers, and region bake. Every method is a thin
/// wrapper that delegates to <see cref="MutationService"/> through <see cref="ToolGuard.Guard{T}"/>, holding no
/// logic of its own. Terrain features and shapes cross the MCP boundary as JSON strings because both are open or
/// polymorphic unions, so the feature and shape parameters take raw json parsed with the document's own
/// serializer options. Every other closed-shape type (biome bands, scatter/companion layers) crosses as typed
/// flat parameters instead, with kinds/hostKinds lists using the <c>"id"</c> / <c>"id:weight"</c> convention.
/// Region shapes are the two verbs that parse json here (via <see cref="DocJson.ParseShape"/>) before calling
/// the shape-typed service methods, using the open document's registry through <see cref="MapEditSession"/>.
/// Coordinates are the engine's world frame: X and Z span the ground plane and Y is up, all lengths in meters,
/// yaw in radians.</summary>
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

    [McpServerTool(Name = "terrain_edit"), Description("Edits the terrain globals: water level, noise seed, biome-edge blend distance, and the gentle/detail noise scalars. At least one must be supplied. All supplied fields change together under one validated, world-affecting mutation.")]
    public MutationResult TerrainEdit(
        [Description("New world water level height in meters (Y is up). Null leaves it unchanged.")] float? waterLevel = null,
        [Description("New terrain noise seed. Null leaves it unchanged.")] int? seed = null,
        [Description("New biome-edge blend distance in meters. Null leaves it unchanged.")] float? biomeBlend = null,
        [Description("New gentle-noise frequency. Null leaves it unchanged.")] float? gentleFrequency = null,
        [Description("New gentle-noise amplitude in meters. Null leaves it unchanged.")] float? gentleAmplitude = null,
        [Description("New detail-noise frequency. Null leaves it unchanged.")] float? detailFrequency = null,
        [Description("New detail-noise octave count. Null leaves it unchanged.")] int? detailOctaves = null)
        => ToolGuard.Guard(() => mutation.TerrainEdit(waterLevel, seed, biomeBlend, gentleFrequency, gentleAmplitude, detailFrequency, detailOctaves));

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

    [McpServerTool(Name = "feature_rename"), Description("Renames the terrain feature at the given index. Empty clears the name back to unnamed. The new name must be unique among named features.")]
    public MutationResult FeatureRename(
        [Description("Zero-based index of the feature to rename, in fold order.")] int index,
        [Description("New name for the feature. Empty clears the name back to unnamed.")] string name)
        => ToolGuard.Guard(() => mutation.FeatureRename(index, name));

    // ---- terrain biome bands --------------------------------------------------------------------------------

    [McpServerTool(Name = "biome_band_add"), Description("Appends a terrain biome band (an elevation-range biome slice that feeds biome selection and height shaping). The result reports the appended index.")]
    public MutationResult BiomeBandAdd(
        [Description("Lower elevation edge in meters. Null means an open (unbounded) lower edge.")] float? start = null,
        [Description("Upper elevation edge in meters. Null means an open (unbounded) upper edge.")] float? end = null,
        [Description("Biome id for the band: Meadow, Forest, Marsh, Mountains, Desert, or Snow.")] string biome = "Meadow",
        [Description("Base terrain height in meters for this band.")] float baseHeight = 0f,
        [Description("Hill amplitude in meters for this band.")] float hillAmplitude = 0f)
        => ToolGuard.Guard(() => mutation.BiomeBandAdd(start, end, biome, baseHeight, hillAmplitude));

    [McpServerTool(Name = "biome_band_edit"), Description("Replaces the terrain biome band at the given index with a new whole value. Every field must be supplied, unlike the scatter/companion layer edit verbs.")]
    public MutationResult BiomeBandEdit(
        [Description("Zero-based index of the biome band to replace.")] int index,
        [Description("Lower elevation edge in meters. Null means an open (unbounded) lower edge.")] float? start,
        [Description("Upper elevation edge in meters. Null means an open (unbounded) upper edge.")] float? end,
        [Description("Biome id for the band: Meadow, Forest, Marsh, Mountains, Desert, or Snow.")] string biome,
        [Description("Base terrain height in meters for this band.")] float baseHeight,
        [Description("Hill amplitude in meters for this band.")] float hillAmplitude)
        => ToolGuard.Guard(() => mutation.BiomeBandEdit(index, start, end, biome, baseHeight, hillAmplitude));

    [McpServerTool(Name = "biome_band_remove"), Description("Removes the terrain biome band at the given index.")]
    public MutationResult BiomeBandRemove(
        [Description("Zero-based index of the biome band to remove.")] int index)
        => ToolGuard.Guard(() => mutation.BiomeBandRemove(index));

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

    [McpServerTool(Name = "exclusion_rename"), Description("Renames the exclusion at the given index. Empty clears the name back to unnamed. The new name must be unique among named exclusions.")]
    public MutationResult ExclusionRename(
        [Description("Zero-based index of the exclusion to rename.")] int index,
        [Description("New name for the exclusion. Empty clears the name back to unnamed.")] string name)
        => ToolGuard.Guard(() => mutation.ExclusionRename(index, name));

    [McpServerTool(Name = "exclusion_set_layers"), Description("Replaces the layer filter of the exclusion at the given index. A null layers list applies the exclusion to every scatter layer including any added later. An empty list applies it to nothing.")]
    public MutationResult ExclusionSetLayers(
        [Description("Zero-based index of the exclusion to retarget.")] int index,
        [Description("Scatter layer names the exclusion applies to. Null applies to every layer.")] string[]? layers = null)
        => ToolGuard.Guard(() => mutation.ExclusionSetLayers(index, layers));

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

    // ---- scatter layers -------------------------------------------------------------------------------------

    [McpServerTool(Name = "scatter_layer_add"), Description("Appends a named procedural scatter layer with no rules. Add rules afterward with scatter_rule_add. The layer name must be unique in the document.")]
    public MutationResult ScatterLayerAdd(
        [Description("Scatter layer name, unique in the document.")] string name,
        [Description("Cell hashing seed. Defaults to 1337.")] int seed = 1337,
        [Description("Scatter grid cell size in meters. Must be positive. Defaults to 4.5.")] float cellSize = 4.5f,
        [Description("Per-cell position jitter in meters. Defaults to 1.6.")] float jitter = 1.6f,
        [Description("Maximum ground height in meters a candidate may scatter at. Null means no height cap.")] float? maxHeight = null,
        [Description("Minimum uniform scale multiplier for generated props. Defaults to 0.8.")] float scaleMin = 0.8f,
        [Description("Maximum uniform scale multiplier for generated props. Must be >= scaleMin. Defaults to 1.35.")] float scaleMax = 1.35f)
        => ToolGuard.Guard(() => mutation.ScatterLayerAdd(name, seed, cellSize, jitter, maxHeight, scaleMin, scaleMax));

    [McpServerTool(Name = "scatter_layer_edit"), Description("Edits a scatter layer's scalars by name, replacing only the supplied fields (a null argument leaves that field unchanged). Rules are preserved as-is: edit them with scatter_rule_add/scatter_rule_edit/scatter_rule_remove.")]
    public MutationResult ScatterLayerEdit(
        [Description("Name of the scatter layer to edit.")] string name,
        [Description("New cell hashing seed. Null leaves it unchanged.")] int? seed = null,
        [Description("New scatter grid cell size in meters. Null leaves it unchanged.")] float? cellSize = null,
        [Description("New per-cell position jitter in meters. Null leaves it unchanged.")] float? jitter = null,
        [Description("New maximum ground height in meters. Null leaves it unchanged unless clearMaxHeight is set.")] float? maxHeight = null,
        [Description("When true, clears MaxHeight back to unset (no height cap), overriding maxHeight. Defaults to false.")] bool clearMaxHeight = false,
        [Description("New minimum uniform scale multiplier. Null leaves it unchanged.")] float? scaleMin = null,
        [Description("New maximum uniform scale multiplier. Null leaves it unchanged.")] float? scaleMax = null)
        => ToolGuard.Guard(() => mutation.ScatterLayerEdit(name, seed, cellSize, jitter, maxHeight, clearMaxHeight, scaleMin, scaleMax));

    [McpServerTool(Name = "scatter_layer_remove"), Description("Removes the scatter layer with the given name. Rejected while a companion layer hosts it or an exclusion/scatter-override layer filter still names it.")]
    public MutationResult ScatterLayerRemove(
        [Description("Name of the scatter layer to remove.")] string name)
        => ToolGuard.Guard(() => mutation.ScatterLayerRemove(name));

    [McpServerTool(Name = "scatter_layer_rename"), Description("Renames a scatter layer, cascading the rename through every companion layer HostLayer and explicit exclusion/scatter-override layer filter that names it. The new name must be unique among scatter layers. The result detail reports how many references were cascaded.")]
    public MutationResult ScatterLayerRename(
        [Description("Current name of the scatter layer.")] string oldName,
        [Description("New name for the scatter layer, unique in the document.")] string newName)
        => ToolGuard.Guard(() => mutation.ScatterLayerRename(oldName, newName));

    // ---- scatter rules --------------------------------------------------------------------------------------

    [McpServerTool(Name = "scatter_rule_add"), Description("Appends a biome scatter rule (density and kinds) to a scatter layer. The result reports the appended rule's index.")]
    public MutationResult ScatterRuleAdd(
        [Description("Name of the scatter layer to add the rule to.")] string layerName,
        [Description("Biome id the rule applies to: Meadow, Forest, Marsh, Mountains, Desert, or Snow.")] string biome,
        [Description("Scatter density for this rule. Defaults to 0.55.")] float density = 0.55f,
        [Description("Prop kinds to scatter, each 'id' (weight 1) or 'id:weight'. Null for none.")] string[]? kinds = null)
        => ToolGuard.Guard(() => mutation.ScatterRuleAdd(layerName, biome, density, kinds));

    [McpServerTool(Name = "scatter_rule_edit"), Description("Edits the scatter rule at the given index on a scatter layer, replacing only the supplied fields (a null argument leaves that field unchanged). At least one field must be supplied.")]
    public MutationResult ScatterRuleEdit(
        [Description("Name of the scatter layer the rule belongs to.")] string layerName,
        [Description("Zero-based index of the rule to edit.")] int ruleIndex,
        [Description("New biome id: Meadow, Forest, Marsh, Mountains, Desert, or Snow. Null leaves it unchanged.")] string? biome = null,
        [Description("New scatter density. Null leaves it unchanged.")] float? density = null,
        [Description("New prop kinds, each 'id' (weight 1) or 'id:weight'. Null leaves them unchanged.")] string[]? kinds = null)
        => ToolGuard.Guard(() => mutation.ScatterRuleEdit(layerName, ruleIndex, biome, density, kinds));

    [McpServerTool(Name = "scatter_rule_remove"), Description("Removes the scatter rule at the given index from a scatter layer.")]
    public MutationResult ScatterRuleRemove(
        [Description("Name of the scatter layer the rule belongs to.")] string layerName,
        [Description("Zero-based index of the rule to remove.")] int ruleIndex)
        => ToolGuard.Guard(() => mutation.ScatterRuleRemove(layerName, ruleIndex));

    // ---- companion layers -----------------------------------------------------------------------------------

    [McpServerTool(Name = "companion_layer_add"), Description("Appends a named companion layer that rings host placements from a scatter layer. The host must name a real scatter layer for the document to validate. The layer name must be unique in the document.")]
    public MutationResult CompanionLayerAdd(
        [Description("Companion layer name, unique in the document.")] string name,
        [Description("Name of the scatter layer whose placements host these companions.")] string hostLayer,
        [Description("Cell hashing seed. Defaults to 1337.")] int seed = 1337,
        [Description("Host placement kind ids this companion rings (plain ids, no weights). Null for none.")] string[]? hostKinds = null,
        [Description("Companion kinds to scatter, each 'id' (weight 1) or 'id:weight'. Null for none.")] string[]? kinds = null,
        [Description("Minimum companion count per host. Defaults to 2.")] int countMin = 2,
        [Description("Maximum companion count per host. Must be >= countMin. Defaults to 4.")] int countMax = 4,
        [Description("Minimum ring radius in meters. Defaults to 0.6.")] float radiusMin = 0.6f,
        [Description("Maximum ring radius in meters. Defaults to 1.8.")] float radiusMax = 1.8f,
        [Description("Minimum uniform scale multiplier. Defaults to 0.7.")] float scaleMin = 0.7f,
        [Description("Maximum uniform scale multiplier. Defaults to 1.1.")] float scaleMax = 1.1f,
        [Description("Maximum ground height in meters a companion may scatter at. Null means no height cap.")] float? maxHeight = null)
        => ToolGuard.Guard(() => mutation.CompanionLayerAdd(name, hostLayer, seed, hostKinds, kinds, countMin, countMax, radiusMin, radiusMax, scaleMin, scaleMax, maxHeight));

    [McpServerTool(Name = "companion_layer_edit"), Description("Edits a companion layer by name, replacing only the supplied fields (a null argument leaves that field unchanged).")]
    public MutationResult CompanionLayerEdit(
        [Description("Name of the companion layer to edit.")] string name,
        [Description("New host scatter layer name. Null leaves it unchanged.")] string? hostLayer = null,
        [Description("New cell hashing seed. Null leaves it unchanged.")] int? seed = null,
        [Description("New host placement kind ids (plain ids, no weights). Null leaves them unchanged.")] string[]? hostKinds = null,
        [Description("New companion kinds, each 'id' (weight 1) or 'id:weight'. Null leaves them unchanged.")] string[]? kinds = null,
        [Description("New minimum companion count per host. Null leaves it unchanged.")] int? countMin = null,
        [Description("New maximum companion count per host. Null leaves it unchanged.")] int? countMax = null,
        [Description("New minimum ring radius in meters. Null leaves it unchanged.")] float? radiusMin = null,
        [Description("New maximum ring radius in meters. Null leaves it unchanged.")] float? radiusMax = null,
        [Description("New minimum uniform scale multiplier. Null leaves it unchanged.")] float? scaleMin = null,
        [Description("New maximum uniform scale multiplier. Null leaves it unchanged.")] float? scaleMax = null,
        [Description("New maximum ground height in meters. Null leaves it unchanged unless clearMaxHeight is set.")] float? maxHeight = null,
        [Description("When true, clears MaxHeight back to unset (no height cap), overriding maxHeight. Defaults to false.")] bool clearMaxHeight = false)
        => ToolGuard.Guard(() => mutation.CompanionLayerEdit(name, hostLayer, seed, hostKinds, kinds, countMin, countMax, radiusMin, radiusMax, scaleMin, scaleMax, maxHeight, clearMaxHeight));

    [McpServerTool(Name = "companion_layer_remove"), Description("Removes the companion layer with the given name.")]
    public MutationResult CompanionLayerRemove(
        [Description("Name of the companion layer to remove.")] string name)
        => ToolGuard.Guard(() => mutation.CompanionLayerRemove(name));

    [McpServerTool(Name = "companion_layer_rename"), Description("Renames a companion layer. The new name must be unique among companion layers.")]
    public MutationResult CompanionLayerRename(
        [Description("Current name of the companion layer.")] string oldName,
        [Description("New name for the companion layer, unique in the document.")] string newName)
        => ToolGuard.Guard(() => mutation.CompanionLayerRename(oldName, newName));

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
