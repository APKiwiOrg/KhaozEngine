using System;
using System.Collections.Generic;
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
            foreach (string part in support.DependencyParts) w.WriteStringValue(part);
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
}
