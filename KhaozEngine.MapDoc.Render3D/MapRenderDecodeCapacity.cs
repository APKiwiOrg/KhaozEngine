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
    internal MapRenderDecodeAllowance(MapSceneAdmission scene, MapRenderAdmission residency, MapRenderBudgets limits)
    {
        long cpu = residency.AvailableCpuBytes;
        long bytes = Math.Min(scene.RemainingPreparedBytes, cpu);
        _bound = cpu < scene.RemainingPreparedBytes
            ? new(nameof(MapRenderBudgets.MaxResidentCpuBytes), limits.MaxResidentCpuBytes - cpu, limits.MaxResidentCpuBytes)
            : scene.PreparedByteCapacity(0);
        Budget = new(new(MaxPeakBytes: bytes));
    }
    internal MapSceneCapacity Resolve(MapSceneCapacity capacity) => capacity.LimitName == nameof(GltfDecodeLimits.MaxPeakBytes)
        ? new(_bound.LimitName, _bound.Requested + capacity.Requested, _bound.Allowance) : capacity;
}
