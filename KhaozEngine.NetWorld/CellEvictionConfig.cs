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
    /// How many evicted cells' freezes are kept in memory so a coordinate that is re-entered restores
    /// synchronously, inside the create call, instead of waiting on a store round trip. This is what makes a
    /// handoff into an unloaded coordinate seamless: the destination is fully populated before it adopts the
    /// crossing entity or ticks once. Oldest entries are dropped first. 0 disables the cache entirely (every
    /// recreation loads from the store).
    /// <para><b>What a dropped entry costs changed in 17.39.0.</b> The freeze is no longer the bytes that were
    /// saved: it is a faithful capture of the cell as it stopped, so it also holds every
    /// <see cref="TransientScope.DurableOnly"/> entity, which the store never held. A dropped coordinate falls back
    /// to the driver's normal asynchronous load, so its ordinary entities lose only the immediacy and come back
    /// exactly as a cold cell does. Its <see cref="TransientScope.DurableOnly"/> entities are gone for good, as
    /// <see cref="TransientScope.Always"/> would have left them, and the drop is silent, because from the store's
    /// side that coordinate is simply cold.</para>
    /// <para><b>Sizing it.</b> With no <see cref="TransientScope.DurableOnly"/> entity anywhere in the world this is
    /// a pure latency knob and any value is safe. With one, it is the bound on how far that entity's life reaches,
    /// so size it to the number of COORDINATES that can hold such an entity and be unloaded and re-entered within
    /// one process lifetime, rather than to a roaming player's working set: for an authored region, its whole cell
    /// count. Each entry holds one cell's snapshot bytes plus its marks, so what it costs is the world's cell size
    /// rather than this number, and a real cell is worth measuring before scaling it up. Whatever tracks a
    /// <see cref="TransientScope.DurableOnly"/> entity (a spawner keyed by net id) must stay able to notice a net id
    /// it no longer owns, since past the cache that entity is gone with no restart involved.</para>
    /// </summary>
    public int MaxCachedSnapshots { get; init; } = 1024;

    /// <summary>The policy deciding which cells to unload. Null (the default) uses <see cref="IdleCellEvictionPolicy"/>.</summary>
    public ICellEvictionPolicy? Policy { get; init; }
}
