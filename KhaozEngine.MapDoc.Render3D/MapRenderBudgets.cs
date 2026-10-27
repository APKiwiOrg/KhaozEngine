using System;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Residency ceilings. Byte counts describe live owned storage or a per-update transfer interval.</summary>
/// <param name="MaxWorkers">MapRenderBuildQueue.Workers. Simultaneous preparation jobs.</param>
/// <param name="MaxQueuedPlans">MapRenderBuildQueue.Plans. Plans held by queued and running jobs.</param>
/// <param name="MaxQueuedBytes">MapRenderBuildQueue.Bytes and shared preparation ledger. Bytes held by queued and running jobs.</param>
/// <param name="MaxAppliesPerUpdate">MapRenderResidency.Applies. Accepted plans in one Update.</param>
/// <param name="MaxUploadBytesPerUpdate">MapRenderResidency.UploadBytes. Transfer bytes in one Update.</param>
/// <param name="MaxDisposalsPerUpdate">MapRenderResidency.Disposals. Retired handles released in one Update.</param>
/// <param name="MaxResidentCpuBytes">MapRenderAdmission, MapRenderUploadPreflight and, as an origin, the native decode views (GltfDecodeBudget, GltfDecodeAdmission, ImageRgba). Simultaneous resident and pending CPU bytes.</param>
/// <param name="MaxResidentGpuBytes">MapRenderAdmission and MapRenderUploadPreflight. Simultaneous owned and staging GPU bytes.</param>
/// <param name="MaxPlanBytes">Preparation ledger windows. Live preparation bytes attributed to one window, including reuse.</param>
/// <param name="MaxActiveWindows">MapRenderResidency.MaxActiveWindows. Windows in the proposed state.</param>
/// <param name="MaxInFlightUploadCpuBytes">MapRenderAdmission and MapRenderUploadPreflight. CPU bytes held by unfinished uploads.</param>
/// <param name="MaxInFlightUploadStagingGpuBytes">MapRenderAdmission and MapRenderUploadPreflight. Staging GPU bytes held by unfinished uploads.</param>
/// <remarks>See docs/CAPACITY-DEMAND.md for the complete evaluated-site mapping.</remarks>
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
