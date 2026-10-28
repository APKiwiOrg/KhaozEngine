using System;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Translates decoder reservations without losing their original allowance or caller bound.</summary>
internal static class MapRenderDecodeCapacity
{
    internal static MapSceneCapacity Convert(GltfDecodeCapacity capacity) => new(capacity.LimitName, capacity.Requested, capacity.Allowance);
    internal static MapSceneCapacity? From(Exception error) => MapSceneCapacity.From(error) ?? (error switch
    {
        GltfDecodeRefusal { Capacity: { } capacity } => Convert(capacity),
        _ => error.InnerException is { } inner ? From(inner) : null,
    });
    internal static MapRenderAssetRefusal Refuse(string name, decimal requested, long allowance) => Refuse(new MapSceneCapacity(name, requested, allowance));
    internal static MapRenderAssetRefusal Refuse(MapSceneCapacity capacity) => new(MapRenderAssetStatus.CapacityExceeded,
        capacity.Message, new MapSceneCapacityException(capacity.LimitName, capacity.Requested, capacity.Allowance));
    internal static void Reserve(GltfDecodeBudget budget, long owned, long temporary)
    {
        if (!budget.TryReserve(owned, temporary, out var capacity)) throw Refuse(Convert(capacity!));
    }
}

internal sealed class MapRenderDecodeAllowance
{
    readonly MapSceneCapacity _bound;
    internal GltfDecodeBudget Budget { get; }
    internal MapRenderDecodeAllowance(MapSceneAdmission scene, MapRenderAdmission residency, MapRenderBudgets limits, GltfDecodeLimits? declared = null)
    {
        long cpu = residency.AvailableCpuBytes;
        long bytes = Math.Min(scene.RemainingPreparedBytes, cpu);
        _bound = cpu < scene.RemainingPreparedBytes
            ? new(nameof(MapRenderBudgets.MaxResidentCpuBytes), limits.MaxResidentCpuBytes - cpu, limits.MaxResidentCpuBytes)
            : scene.PreparedByteCapacity(0);
        if (declared is not null && declared.MaxPeakBytes <= bytes)
            _bound = new(nameof(GltfDecodeLimits.MaxPeakBytes), 0, declared.MaxPeakBytes);
        var budget = (declared ?? new()) with { MaxPeakBytes = Math.Min(bytes, declared?.MaxPeakBytes ?? long.MaxValue) };
        if (KhaozEngine.Diagnostics.CapacityDemandDiagnostics.IsObserved) Derive(budget, scene, residency, limits, declared, cpu);
        Budget = new(budget);
    }

    // The decode allowance is the least of the scene's remaining preparation, residency CPU and the declared decode peak.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void Derive(GltfDecodeLimits budget, MapSceneAdmission scene, MapRenderAdmission residency, MapRenderBudgets limits,
        GltfDecodeLimits? declared, long cpu) =>
        MapDemandOrigins.Derive(budget, nameof(GltfDecodeLimits.MaxPeakBytes), scene.PreparedByteOrigins(),
            [new(nameof(MapRenderBudgets.MaxResidentCpuBytes), limits.MaxResidentCpuBytes - cpu, limits.MaxResidentCpuBytes, residency)],
            declared is null ? [] : MapDemandOrigins.Of(declared, nameof(GltfDecodeLimits.MaxPeakBytes), declared.MaxPeakBytes));
    internal MapSceneCapacity Resolve(MapSceneCapacity capacity) => capacity.LimitName == nameof(GltfDecodeLimits.MaxPeakBytes)
        ? new(_bound.LimitName, _bound.Requested + capacity.Requested, _bound.Allowance) : capacity;
}
