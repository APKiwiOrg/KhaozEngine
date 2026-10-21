using System;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Admits a conservative complete candidate envelope before generation enters its retained cache.</summary>
internal static class MapFoliageGenerationAdmission
{
    internal static long RetentionDemand(MapWindowFoliageContext context, MapFoliageLayer layer, MapFoliageChunkKey chunk,
        MapFoliageLimits limits)
    {
        var rectangle = MapFoliageDistribution.Rectangle(layer, chunk);
        var range = MapFoliageLatticeRange.Of(rectangle.MinX, rectangle.MinZ, rectangle.MaxX, rectangle.MaxZ, layer.Spacing);
        if (range.Count > limits.MaxCandidates) throw Capacity();
        if (MapFoliageDistribution.KnownOffRaster(layer, rectangle)) return 256;
        var inputs = (IMapFoliageInputs)context;
        long sample = 0;
        foreach (var model in layer.Models)
        {
            var asset = inputs.Assets.GetAsset(model.AssetId);
            foreach (var surface in inputs.DeclaredSurfaces)
                sample = Math.Max(sample, MapFoliageRetentionBudget.ChargeSample(layer.Id, surface.Id, asset.Id, layer.Policy.Id, asset.MaterialResourceIds));
        }
        long demand = checked(256 + Math.Min(checked((long)range.Count), Math.Min(limits.MaxSamples, limits.MaxQuerySamples)) * sample);
        if (demand > limits.MaxQueryRetainedBytes) throw Capacity();
        return demand;
    }
    internal static void Charge(MapSceneAdmission admission, long bytes)
    { if (!admission.TryReservePreparation(bytes, 0)) throw Capacity(); }
    static MapRenderAssetRefusal Capacity() => new(MapRenderAssetStatus.CapacityExceeded, "Foliage generation exceeds its shared preparation allowance.");
}
