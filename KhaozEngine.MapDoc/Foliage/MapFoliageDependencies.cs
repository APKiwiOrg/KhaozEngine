using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Chunk dependencies over the exact scoped support and consulted clearance inputs. No root, closure or
/// whole-context identity participates, so unrelated edits do not invalidate a batch.</summary>
public static class MapFoliageDependencies
{
    public static string Compute(MapPreparedFoliageSupport support, MapPreparedFoliageClearance clearance,
        MapFoliageLayer layer, MapFoliageChunkKey chunk)
    {
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(clearance);
        ArgumentNullException.ThrowIfNull(layer);
        if (support.Status != MapFoliageStatus.Complete || clearance.Status != MapFoliageStatus.Complete) return "";
        return MapCanonical.HashHex(w =>
        {
            var resources = new HashSet<string>(StringComparer.Ordinal);
            w.WriteStartArray(); w.WriteStringValue("kemap/foliage-batch/1");
            w.WriteStringValue(layer.Id); w.WriteNumberValue(chunk.X); w.WriteNumberValue(chunk.Z);
            w.WriteNumberValue(1);
            // Clearance parts already hold every support part.
            foreach (string part in clearance.DependencyParts) w.WriteStringValue(part);
            w.WriteNumberValue(layer.Seed);
            foreach (float number in new[] { layer.Spacing, layer.ScaleMin, layer.ScaleMax, layer.RootOffset })
                w.WriteNumberValue(BitConverter.SingleToInt32Bits(number));
            foreach (MapFoliageModel model in layer.Models)
            {
                w.WriteStringValue(model.AssetId); w.WriteNumberValue(BitConverter.SingleToInt32Bits(model.Weight));
                if (support.Assets is not { } assets) continue;
                MapResolvedAsset asset = assets.GetAsset(model.AssetId);
                w.WriteNumberValue(BitConverter.SingleToInt32Bits(asset.SourceUnitsToMetres));
                Resource(asset.MeshResourceId);
                foreach (string id in asset.MaterialResourceIds) Resource(id);
            }
            if (support.Assets is not null)
                foreach (MapFoliageMaterial material in layer.Materials)
                {
                    Resource(material.ResourceId);
                }
            w.WriteEndArray();
            void Resource(string id)
            {
                w.WriteStringValue(id);
                var pending = new Stack<string>();
                pending.Push(id);
                while (pending.Count != 0)
                {
                    string next = pending.Pop();
                    if (!resources.Add(next)) continue;
                    MapResolvedResource resource = support.Assets!.GetResource(next);
                    w.WriteStartArray(); w.WriteStringValue(next);
                    w.WriteStringValue(resource.Reference.Sha256); w.WriteNumberValue((int)resource.Kind);
                    w.WriteStartArray();
                    foreach (string dependency in resource.Dependencies) w.WriteStringValue(dependency);
                    w.WriteEndArray(); w.WriteEndArray();
                    for (int i = resource.Dependencies.Count - 1; i >= 0; i--) pending.Push(resource.Dependencies[i]);
                }
            }
        });
    }

    /// <summary>The digest of a chunk that <see cref="MapFoliageDistribution.KnownOffRaster"/> proves off the raster. It
    /// prepared nothing, so it binds the scheme, layer, chunk, policy and the raster origin, cell size, width and depth
    /// that decide off-raster status. Growing or moving the raster changes it. Support, clearance and model inputs do not.</summary>
    internal static string OffRaster(MapFoliageLayer layer, MapFoliageChunkKey chunk) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray(); w.WriteStringValue("kemap/foliage-batch/1");
        w.WriteStringValue(layer.Id); w.WriteNumberValue(chunk.X); w.WriteNumberValue(chunk.Z);
        w.WriteNumberValue(1);
        w.WriteStringValue("off-raster");
        w.WriteStringValue(PolicyPart(layer));
        w.WriteStringValue(RasterPart(layer));
        w.WriteEndArray();
    });

    internal static string PolicyPart(MapFoliageLayer layer) =>
        $"policy|{layer.Policy.Id}|{layer.Policy.Version.ToString(CultureInfo.InvariantCulture)}|{Bits(layer.Policy.NormalBoundaryHeight)}";

    internal static string RasterPart(MapFoliageLayer layer) =>
        $"raster|{Bits(layer.OriginX)}|{Bits(layer.OriginZ)}|{Bits(layer.CellSize)}|{layer.Width.ToString(CultureInfo.InvariantCulture)}|{layer.Depth.ToString(CultureInfo.InvariantCulture)}";

    static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString(CultureInfo.InvariantCulture);
}
