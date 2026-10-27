using System;

namespace KhaozEngine.MapDoc.Render3D;

public sealed record MapRenderBudgets(int MaxWorkers = 2, int MaxQueuedPlans = 32,
    long MaxQueuedBytes = 67108864, int MaxAppliesPerUpdate = 2, long MaxUploadBytesPerUpdate = 4194304,
    int MaxDisposalsPerUpdate = 8, long MaxResidentCpuBytes = 268435456, long MaxResidentGpuBytes = 536870912,
    long MaxPlanBytes = 16777216, int MaxActiveWindows = 64,
    long MaxInFlightUploadCpuBytes = 268435456, long MaxInFlightUploadStagingGpuBytes = 134217728)
{
    internal void Validate()
    {
        if (MaxWorkers < 1 || MaxQueuedPlans < 1 || MaxQueuedBytes < 0 || MaxAppliesPerUpdate < 1 ||
            MaxUploadBytesPerUpdate < 0 || MaxDisposalsPerUpdate < 1 || MaxResidentCpuBytes < 0 ||
            MaxResidentGpuBytes < 0 || MaxPlanBytes < 0 || MaxActiveWindows < 0 ||
            MaxInFlightUploadCpuBytes < 0 || MaxInFlightUploadStagingGpuBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(MapRenderBudgets));
    }
}
