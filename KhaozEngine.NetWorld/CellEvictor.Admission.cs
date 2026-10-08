using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

public sealed partial class CellEvictor
{
    sealed record CachedAdmission(CachedFreeze Freeze, IDisposable SaveFence);
    readonly Dictionary<CellCoord, CachedAdmission> pendingCacheAdmissions = new();

    /// <summary>Cached freezes retained with their transient marks until the destination environment
    /// admits them. These cells cannot be saved empty or evicted while waiting.</summary>
    public int PendingCacheAdmissionCount => pendingCacheAdmissions.Count;

    void RetryCacheAdmissions()
    {
        foreach (var pair in pendingCacheAdmissions.ToArray()) TryRestoreCache(pair.Key, pair.Value);
    }

    void TryRestoreCache(CellCoord coord, CachedAdmission waiting)
    {
        CellRestoreResult result = host.TryRestoreCell(coord, waiting.Freeze.Bytes, waiting.Freeze.Marks);
        if (result.NeedsAdmission) return;
        pendingCacheAdmissions.Remove(coord);
        waiting.SaveFence.Dispose();
        if (!result.Ok)
        {
            // Only a decode failure abandons the cache for the stored durable snapshot. Environment
            // refusals retain the richer freeze, including entities intentionally absent from that save.
            persistence.ForgetCell(coord);
            persistence.RequestLoad(coord);
            return;
        }
        long max = 0;
        foreach (long id in result.NetIds) if (id > max) max = id;
        if (max > 0) host.EnsureNextNetIdAtLeast(max + 1);
        RestoredFromCacheCount++;
        CellRestoredFromCache?.Invoke(coord);
    }
}
