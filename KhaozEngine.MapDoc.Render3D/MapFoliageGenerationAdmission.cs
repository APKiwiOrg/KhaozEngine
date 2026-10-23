using System;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Admits a conservative complete candidate envelope before generation enters its retained cache.</summary>
internal static class MapFoliageGenerationAdmission
{
    internal static long RetentionDemand(MapWindowFoliageContext context, MapFoliageLayer layer, MapFoliageChunkKey chunk,
        MapFoliageLimits limits, long retainedBytes = 0, long? aggregateAllowance = null)
    {
        var rectangle = MapFoliageDistribution.Rectangle(layer, chunk);
        var range = MapFoliageLatticeRange.Of(rectangle.MinX, rectangle.MinZ, rectangle.MaxX, rectangle.MaxZ, layer.Spacing);
        if (range.Count > limits.MaxCandidates) throw new MapSceneCapacityException(nameof(MapFoliageLimits.MaxCandidates), (decimal)range.Count, limits.MaxCandidates);
        long demand = 256;
        if (!MapFoliageDistribution.KnownOffRaster(layer, rectangle))
        {
            var inputs = (IMapFoliageInputs)context;
            long sample = 0;
            foreach (var model in layer.Models)
            {
                var asset = inputs.Assets.GetAsset(model.AssetId);
                foreach (var surface in inputs.DeclaredSurfaces)
                    sample = Math.Max(sample, MapFoliageRetentionBudget.ChargeSample(layer.Id, surface.Id, asset.Id, layer.Policy.Id, asset.MaterialResourceIds));
            }
            demand = checked(demand + Math.Min(checked((long)range.Count), Math.Min(limits.MaxSamples, limits.MaxQuerySamples)) * sample);
        }
        if (aggregateAllowance is { } allowance && demand > allowance - retainedBytes)
            throw new MapSceneCapacityException(nameof(MapFoliageLimits.MaxQueryRetainedBytes), (decimal)retainedBytes + demand, allowance);
        if (demand > limits.MaxQueryRetainedBytes) throw new MapSceneCapacityException(nameof(MapFoliageLimits.MaxQueryRetainedBytes), demand, limits.MaxQueryRetainedBytes);
        return demand;
    }
    internal static void Charge(MapSceneAdmission admission, long bytes)
    { admission.ReserveBytes(bytes); }
}
