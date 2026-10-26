using System;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Accounts simultaneous builder and frozen allocations, before allocation, without pooling.</summary>
internal sealed class MapFoliageColumnBudget(long allowance)
{
    internal long Live { get; private set; }
    internal long Peak { get; private set; }
    internal void Take(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes > allowance - Live) throw MapFoliageDistribution.Refuse(MapFoliageStatus.CapacityExceeded, "native foliage columns");
        Live = checked(Live + bytes); Peak = Math.Max(Peak, Live);
    }
    internal void Release(long bytes)
    {
        if (bytes < 0 || bytes > Live) throw new ArgumentOutOfRangeException(nameof(bytes));
        Live -= bytes;
    }
}
