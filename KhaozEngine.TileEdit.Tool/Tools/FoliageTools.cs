using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>Authored cosmetic foliage layers and density painting.</summary>
[McpServerToolType]
public sealed class FoliageTools(QueryService query, MutationService mutate)
{
    [McpServerTool(Name = "foliage_layer_set"), Description("Adds or replaces one complete cosmetic foliage layer as one undo step. Density is base64 row-major bytes. X advances within each row and row index advances along positive world Z from originZ. The layer creates no gameplay objects, collision or picking targets.")]
    public MutationResult FoliageLayerSet(
        [Description("Complete validated layer object. Density length must equal width times height.")] FoliageLayerInfo layer)
        => ToolGuard.Guard(() => mutate.FoliageLayerSet(layer));

    [McpServerTool(Name = "foliage_get"), Description("Reads one cosmetic foliage layer as a detached object, or lists every layer when id is null. Density is base64 row-major bytes. X advances within each row and row index advances along positive world Z from originZ.")]
    public object FoliageGet(
        [Description("Layer id. Null lists every layer in authoring order.")] string? id = null)
        => ToolGuard.Guard<object>(() => id is null ? query.FoliageGet() : query.FoliageGet(id));

    [McpServerTool(Name = "foliage_density_set"), Description("Replaces one layer's complete density raster as one undo step. Rows are numeric 0 to 255. Row 0 starts at originZ, columns advance along positive world X, and later rows advance along positive world Z.")]
    public MutationResult FoliageDensitySet(
        [Description("Layer id.")] string id,
        [Description("Raster width. Must equal the configured layer width.")] int width,
        [Description("Raster height. Must equal the configured layer height.")] int height,
        [Description("Exactly height rows of width density values from 0 to 255. Row 0 is at originZ.")] int[][] rows)
        => ToolGuard.Guard(() => mutate.FoliageDensitySet(id, width, height, rows));

    [McpServerTool(Name = "foliage_paint"), Description("Paints a circular density brush in world metres as one undo step. Samples inside hardness times radius receive the target density. The remaining rim blends linearly to the unchanged raster.")]
    public MutationResult FoliagePaint(
        [Description("Layer id.")] string id,
        [Description("Brush centre world X in metres.")] float worldX,
        [Description("Brush centre world Z in metres. Tile north maps to negative world Z.")] float worldZ,
        [Description("Brush radius in world metres, greater than zero.")] float radius,
        [Description("Target density from 0 to 255.")] int density,
        [Description("Solid inner fraction from 0 to 1. Zero is fully soft and one is a hard circle.")] float hardness)
        => ToolGuard.Guard(() => mutate.FoliagePaint(id, worldX, worldZ, radius, density, hardness));

    [McpServerTool(Name = "foliage_remove"), Description("Removes one cosmetic foliage layer as one undo step.")]
    public MutationResult FoliageRemove([Description("Layer id.")] string id)
        => ToolGuard.Guard(() => mutate.FoliageRemove(id));
}
