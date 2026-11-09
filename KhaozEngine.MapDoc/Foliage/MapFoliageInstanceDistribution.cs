using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Foliage;

public static partial class MapFoliageDistribution
{
    internal static MapFoliageInstanceBatch GenerateInstances(MapFoliageGenerationContext context, string layerId,
        MapFoliageChunkKey chunk, MapFoliageLimits? limits = null) => GenerateInstances((IMapFoliageInputs)context, layerId, chunk, limits);
    internal static MapFoliageInstanceBatch GenerateInstances(MapWindowFoliageContext context, string layerId,
        MapFoliageChunkKey chunk, MapFoliageLimits? limits = null) => GenerateInstances((IMapFoliageInputs)context, layerId, chunk, limits);

    static MapFoliageInstanceBatch GenerateInstances(IMapFoliageInputs context, string layerId, MapFoliageChunkKey chunk, MapFoliageLimits? limits)
    {
        ArgumentNullException.ThrowIfNull(context);
        var admitted = Limits(limits);
        var meter = new Meter();
        MapFoliageInstanceBuilder? instances = null;
        try
        {
            var layer = Layer(context, layerId);
            var area = Rectangle(layer, chunk);
            context.RequireChunk(layer, chunk);
            var range = MapFoliageLatticeRange.Of(area.MinX, area.MinZ, area.MaxX, area.MaxZ, layer.Spacing);
            if (DemandDiagnostics.Check(context, "MapFoliageInstanceDistribution.MaxCandidates.1", "MaxCandidates", 0, (decimal)range.Count, admitted.MaxCandidates, range.Count > admitted.MaxCandidates, CapacityDemandLifetime.Cumulative, allowanceSource: admitted)) throw Refuse(MapFoliageStatus.CapacityExceeded, "chunk candidates");
            instances = new(layer.Id, layer.Policy, chunk, admitted);
            var evaluation = new Evaluation(context, layer, admitted, meter);
            Scan(evaluation, range, area, static _ => true, null, instances);
            string digest = KnownOffRaster(layer, area) ? MapFoliageDependencies.OffRaster(layer, chunk) : evaluation.Digest(chunk);
            return instances.Freeze(digest, meter.Work());
        }
        catch (MapFoliageRefusal refusal)
        { return new(layerId, chunk, refusal.Status, meter.Work(instances?.LiveBytes ?? 0)); }
        finally { instances?.Dispose(); }
    }

    // Both products visit exactly the same lattice and accepted-candidate evaluator. Only their retention sinks differ.
    static void Scan(Evaluation evaluation, MapFoliageLatticeRange range, RectArea area, Func<long, bool> admit,
        Action<MapFoliageDecision>? full, MapFoliageInstanceBuilder? instances = null)
    {
        for (long z = range.MinZ; z <= range.MaxZ; z++)
            for (long x = range.MinX; x <= range.MaxX; x++)
            {
                var decision = evaluation.Decide((int)x, (int)z, area, admit, instances);
                full?.Invoke(decision);
            }
    }
}
