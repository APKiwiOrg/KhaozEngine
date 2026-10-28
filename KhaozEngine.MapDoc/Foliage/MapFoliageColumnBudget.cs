using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Accounts simultaneous builder and frozen allocations, before allocation, without pooling.</summary>
/// <remarks>The pinned control layout holds no provenance field. A derived allowance registers its origins on this budget.</remarks>
internal sealed class MapFoliageColumnBudget(long allowance, Action<long>? allocate = null, Action<long>? release = null)
{
    internal long Live { get; private set; }
    internal long Peak { get; private set; }
    internal void Take(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (DemandDiagnostics.Check(this, "MapFoliageColumnBudget.MaxQueryRetainedBytes.1", "MaxQueryRetainedBytes", Live, bytes, allowance, bytes > allowance - Live, CapacityDemandLifetime.Transient, allowanceSource: this)) throw MapFoliageDistribution.Refuse(MapFoliageStatus.CapacityExceeded, "native foliage columns");
        allocate?.Invoke(bytes);
        Live = checked(Live + bytes); Peak = Math.Max(Peak, Live);
    }
    internal void Release(long bytes)
    {
        if (bytes < 0 || bytes > Live) throw new ArgumentOutOfRangeException(nameof(bytes));
        release?.Invoke(bytes); Live -= bytes;
    }
}
