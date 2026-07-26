using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables for <see cref="CellEvictor"/>.</summary>
public sealed class CellEvictionConfig
{
    /// <summary>
    /// How often the policy is asked which live cells to unload, seconds. The scan is O(live cells x online
    /// players), so it is deliberately far coarser than the tick. Idle time advances in scan-sized steps, so a
    /// threshold finer than this interval rounds up to it.
    /// </summary>
    public float ScanIntervalSeconds { get; init; } = 10f;

    /// <summary>
    /// Most cells one scan may start unloading, so a world that goes quiet all at once spreads its store writes
    /// over several scans instead of one burst. Evictions already in flight do not count against it.
    /// </summary>
    public int MaxEvictionsPerScan { get; init; } = 8;

    /// <summary>
    /// How many evicted cells' snapshots are kept in memory so a coordinate that is re-entered restores
    /// synchronously, inside the create call, instead of waiting on a store round trip. This is what makes a
    /// handoff into an unloaded coordinate seamless: the destination is fully populated before it adopts the
    /// crossing entity or ticks once. The bytes are already durable, so an entry dropped here costs nothing but
    /// that immediacy, and its coordinate falls back to the driver's normal asynchronous load. Oldest entries are
    /// dropped first. 0 disables the cache entirely (every recreation loads from the store).
    /// </summary>
    public int MaxCachedSnapshots { get; init; } = 1024;

    /// <summary>The policy deciding which cells to unload. Null (the default) uses <see cref="IdleCellEvictionPolicy"/>.</summary>
    public ICellEvictionPolicy? Policy { get; init; }
}
