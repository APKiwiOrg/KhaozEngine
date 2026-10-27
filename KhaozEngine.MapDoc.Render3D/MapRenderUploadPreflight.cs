using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

internal sealed class MapRenderUploadPreflight
{
    readonly MapRenderBudgets _budgets;
    readonly decimal _cpu, _gpu;
    MapRenderUploadPreflight(MapRenderBudgets budgets, decimal cpu, decimal gpu)
    { _budgets = budgets; _cpu = cpu; _gpu = gpu; }

    internal static MapRenderUploadPreflight Measure(IEnumerable<SceneUploadCapacity> capacities, MapRenderBudgets budgets,
        int references, int recordingCapacity)
    {
        decimal retained = 0, owned = 0, cpu = 0, staging = 0;
        foreach (var capacity in capacities)
        {
            if (capacity.PeakCpuBytes < 0 || capacity.RetainedCpuBytes < 0 || capacity.RetainedCpuBytes > capacity.PeakCpuBytes ||
                capacity.OwnedGpuBytes < 0 || capacity.PeakStagingGpuBytes < 0)
                throw new ArgumentException("Invalid renderer upload capacity.");
            RequireCapacity(nameof(MapRenderBudgets.MaxInFlightUploadCpuBytes), capacity.PeakCpuBytes, budgets.MaxInFlightUploadCpuBytes);
            RequireCapacity(nameof(MapRenderBudgets.MaxInFlightUploadStagingGpuBytes), capacity.PeakStagingGpuBytes, budgets.MaxInFlightUploadStagingGpuBytes);
            retained += capacity.RetainedCpuBytes; owned += capacity.OwnedGpuBytes;
            cpu += capacity.PeakCpuBytes; staging += capacity.PeakStagingGpuBytes;
        }
        // Only the attainable envelope is required for a small batch. Active uploads use their full peak,
        // so all completed survivors plus one shared in-flight envelope conservatively bound the interval.
        long recording = references == recordingCapacity || references == 0 ||
            (references < recordingCapacity && references * 2L >= recordingCapacity) ? 0 : checked(512L + references * 512L);
        decimal gpu = owned + Math.Min(staging, budgets.MaxInFlightUploadStagingGpuBytes);
        decimal uploadCpu = retained + Math.Min(cpu, budgets.MaxInFlightUploadCpuBytes) + recording;
        RequireCapacity(nameof(MapRenderBudgets.MaxResidentGpuBytes), gpu, budgets.MaxResidentGpuBytes);
        RequireCapacity(nameof(MapRenderBudgets.MaxResidentCpuBytes), uploadCpu, budgets.MaxResidentCpuBytes);
        return new(budgets, uploadCpu, gpu);
    }

    internal MapSceneCapacity? Validate(MapRenderLifetime lifetime, bool mayWait) =>
        ResidentCapacity(nameof(MapRenderBudgets.MaxResidentGpuBytes), lifetime.GpuBytes + _gpu, _budgets.MaxResidentGpuBytes, mayWait) ??
        ResidentCapacity(nameof(MapRenderBudgets.MaxResidentCpuBytes), lifetime.CpuBytes + _cpu, _budgets.MaxResidentCpuBytes, mayWait);

    static MapSceneCapacity? ResidentCapacity(string name, decimal requested, long allowance, bool mayWait)
    {
        if (requested <= allowance) return null;
        var capacity = new MapSceneCapacity(name, requested, allowance);
        if (!mayWait) throw new MapSceneCapacityException(name, requested, allowance);
        return capacity;
    }

    static void RequireCapacity(string name, decimal requested, long allowance)
    {
        if (requested > allowance) throw new MapSceneCapacityException(name, requested, allowance);
    }
}
