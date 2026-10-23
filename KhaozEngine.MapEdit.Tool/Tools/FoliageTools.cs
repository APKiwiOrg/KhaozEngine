using System.ComponentModel;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using ModelContextProtocol.Server;

namespace KhaozEngine.MapEdit.Tools;

/// <summary>Native foliage wire adapters over the shared foliage command and the detached foliage read. Raster samples
/// are columns X and rows Z, rows advance positive world Z, brush positions and radii are world metres and density bytes
/// are 0 to 255.</summary>
[McpServerToolType]
public sealed class FoliageTools(MutationService mutation, QueryService query)
{
    [McpServerTool(Name = "foliage_layer_set"), Description("Adds one complete native foliage layer, or replaces the layer with the same ID in place, as one undo step. " +
        "layerJson is one closed payload 1 layer, the same object a layer takes in the document foliage section, with base64 density chunks. " +
        "A policy definition change needs expectedDifferentialDigest from a dryRun of the same proposal. dryRun applies nothing and returns the differential.")]
    public MapFoliageMutationResult FoliageLayerSet(string layerJson, string? expectedDifferentialDigest = null, bool dryRun = false) =>
        ToolGuard.Guard(() => mutation.FoliageApply(new MapSetFoliageLayer(FoliageWire.ReadLayer(layerJson), expectedDifferentialDigest), dryRun));

    [McpServerTool(Name = "foliage_get"), Description("Reads foliage without changing the map. With no layerId, lists every layer's metadata and chunk keys in authoring order, with no density. " +
        "With layerId, returns that layer's metadata, the stored base64 chunks the sample rectangle x, z, width, depth reaches when all four are given, " +
        "and a CPU preview of previewChunkJson {x,z} with complete sample poses. Absent chunks are omitted and read zero.")]
    public FoliageReadWireResult FoliageGet(string? layerId = null, int? x = null, int? z = null, int? width = null, int? depth = null,
        string? previewChunkJson = null) => ToolGuard.Guard(() => FoliageWire.ToWire(
            query.FoliageGet(layerId, x, z, width, depth, previewChunkJson is null ? null : FoliageWire.ReadChunkKey(previewChunkJson))));

    [McpServerTool(Name = "foliage_density_set"), Description("Replaces the samples [x, x + width) by [z, z + depth) of one layer as one undo step. " +
        "density is row-major with width * depth values from 0 to 255, and later rows advance positive world Z.")]
    public MapFoliageMutationResult FoliageDensitySet(string layerId, int x, int z, int width, int depth, int[] density) =>
        ToolGuard.Guard(() => mutation.FoliageApply(new MapSetFoliageDensity(layerId, x, z, width, depth, density)));

    [McpServerTool(Name = "foliage_paint"), Description("Paints one circular density brush in world metres as one undo step. target is 0 to 255 and hardness 0 to 1. " +
        "Samples within hardness times radius take the target, and the rest of the rim blends linearly to the unchanged raster.")]
    public MapFoliageMutationResult FoliagePaint(string layerId, float worldX, float worldZ, float radiusMetres, int target, float hardness) =>
        ToolGuard.Guard(() =>
        {
            if (target is < 0 or > 255) throw new MapDocumentException($"Foliage paint target {target} must be from 0 to 255.");
            return mutation.FoliageApply(new MapPaintFoliage(layerId, worldX, worldZ, radiusMetres, (byte)target, hardness));
        });

    [McpServerTool(Name = "foliage_remove"), Description("Removes one foliage layer as one undo step. Removing the last layer leaves the map without foliage.")]
    public MapFoliageMutationResult FoliageRemove(string layerId) => ToolGuard.Guard(() => mutation.FoliageApply(new MapRemoveFoliage(layerId)));
}
