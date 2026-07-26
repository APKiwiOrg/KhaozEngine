using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Unloads idle cells without losing their state: the persist-then-evict driver. A <see cref="ShardHost"/> creates
/// cells on demand and, on its own, never removes one, so a long-running world where players roam keeps every cell
/// it has ever touched alive. This drives the other half. Each scan it asks an
/// <see cref="ICellEvictionPolicy"/> which live cells are disposable, snapshots each candidate through
/// <see cref="CellPersistence"/>, and removes it from the host only once that write has landed. A failed write, a
/// cell that changed while the write was in flight, or a host that refuses all leave the cell exactly where it was,
/// to be retried on a later scan.
/// </summary>
/// <remarks>
/// <para><b>Recreation always restores first.</b> An unloaded coordinate comes back the moment anything routes to
/// it (a spawn, a handoff destination, an explicit <see cref="ICellPersistenceHost.EnsureCell"/>). The driver keeps
/// the evicted snapshot in memory (see <see cref="CellEvictionConfig.MaxCachedSnapshots"/>) and restores it
/// synchronously from the <see cref="ICellPersistenceHost.CellCreated"/> hook, which the host raises inside the
/// create call itself. So a cell recreated as a handoff destination is fully populated before it adopts the
/// crossing entity and before it ticks once. Beyond the cache the coordinate falls back to
/// <see cref="CellPersistence"/>'s ordinary asynchronous load, exactly as a cold cell does after a restart. Exactly
/// one of the two paths is armed per evicted coordinate, so nothing is ever restored twice.</para>
///
/// <para><b>What eviction persists is what a restart persists</b>: the cell snapshot's
/// <see cref="KhaozEngine.Replication.ReplicationChannels.Persist"/> channel. A component that did not declare
/// Persist does not survive an unload, the same way it does not survive a shutdown. A player entity is excluded
/// from cell snapshots entirely (it persists on its own record), which is why a cell holding one is never
/// evictable.</para>
///
/// <para>Call <see cref="Update"/> once per server frame on the server thread, alongside
/// <see cref="CellPersistence.Update"/>.</para>
/// </remarks>
public sealed class CellEvictor
{
    // A cell whose eviction snapshot has been handed to the store and is not durable yet. The cell keeps ticking
    // meanwhile, so the finalize pass re-checks that it still matches what was written before removing it.
    private readonly struct PendingEviction
    {
        public PendingEviction(CellCoord coord, byte[] snapshot, int ownedCount, Task<bool> save)
        {
            Coord = coord;
            Snapshot = snapshot;
            OwnedCount = ownedCount;
            Save = save;
        }

        public CellCoord Coord { get; }
        public byte[] Snapshot { get; }
        public int OwnedCount { get; }
        public Task<bool> Save { get; }
    }

    private readonly ICellEvictionHost host;
    private readonly CellPersistence persistence;
    private readonly CellEvictionConfig config;
    private readonly ICellEvictionPolicy policy;

    private readonly Dictionary<CellCoord, float> idleSeconds = new();
    // Evicted coords whose snapshot is held for a synchronous restore on recreation, plus their eviction order so
    // the oldest is dropped first when the cache is full.
    private readonly Dictionary<CellCoord, byte[]> cachedSnapshots = new();
    private readonly List<CellCoord> cacheOrder = new();
    private readonly List<PendingEviction> pending = new();
    private readonly List<CellCoord> scanScratch = new();
    private readonly List<CellCoord> pruneScratch = new();
    private float sinceScan;

    public CellEvictor(ICellEvictionHost host, CellPersistence persistence, CellEvictionConfig? config = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        this.config = config ?? new CellEvictionConfig();
        policy = this.config.Policy ?? new IdleCellEvictionPolicy();
        host.CellCreated += OnCellCreated;
    }

    /// <summary>Raised on the server thread once a cell has been persisted and removed from the host.</summary>
    public event Action<CellCoord>? CellEvicted;

    /// <summary>Raised on the server thread when an evicted coordinate was recreated and restored from the in-memory
    /// snapshot cache, inside the create call. The seam a game hooks to re-arm anything it keyed per cell.</summary>
    public event Action<CellCoord>? CellRestoredFromCache;

    /// <summary>Cells unloaded so far.</summary>
    public int EvictedCount { get; private set; }

    /// <summary>Recreated coordinates served synchronously from the in-memory snapshot cache.</summary>
    public int RestoredFromCacheCount { get; private set; }

    /// <summary>Evicted coordinates currently holding a cached snapshot for an instant restore.</summary>
    public int CachedSnapshotCount => cachedSnapshots.Count;

    /// <summary>Cells whose eviction snapshot is written but not yet confirmed durable.</summary>
    public int PendingEvictionCount => pending.Count;

    /// <summary>
    /// Call once per server frame on the server thread. Finalizes evictions whose store write has landed, then runs
    /// the policy scan on its own (much coarser) interval.
    /// </summary>
    public void Update(float dt)
    {
        FinishPending();
        sinceScan += dt;
        if (sinceScan < config.ScanIntervalSeconds) return;
        float elapsed = sinceScan;
        sinceScan = 0f;
        Scan(elapsed);
    }

    /// <summary>
    /// Starts unloading one cell now, bypassing the policy but not the safety gates: it still refuses while a load
    /// or save for the coordinate is in flight, while the host pins or cannot remove the cell, and while an earlier
    /// eviction of it is already under way. The cell is removed only once its snapshot is durably stored, on a
    /// later <see cref="Update"/>. Returns true when the eviction was started, not when it completed.
    /// </summary>
    public bool RequestEvict(CellCoord coord)
    {
        for (int i = 0; i < pending.Count; i++)
            if (pending[i].Coord == coord) return false;
        if (persistence.IsBusy(coord)) return false;
        if (!host.CanEvictCell(coord)) return false;
        if (!host.TryReadEvictionSignals(coord, out CellEvictionSignals signals)) return false;
        byte[]? snapshot = host.SnapshotCell(coord);
        if (snapshot is null) return false;
        pending.Add(new PendingEviction(coord, snapshot, signals.OwnedEntityCount,
            persistence.SaveCellAsync(coord, snapshot)));
        return true;
    }

    // Restores an evicted coordinate the instant it is recreated. The host raises CellCreated synchronously from
    // inside the create call, so this lands before the new cell can tick or adopt a migrated entity. A coord with no
    // cached snapshot is not ours to handle: CellPersistence loads it from the store on the same event.
    private void OnCellCreated(CellCoord coord)
    {
        if (!cachedSnapshots.Remove(coord, out byte[]? snapshot)) return;
        cacheOrder.Remove(coord);

        CellRestoreResult result = host.TryRestoreCell(coord, snapshot);
        if (!result.Ok)
        {
            // These are bytes this process wrote moments ago, so a decode failure means something is badly wrong.
            // Hand the coordinate back to the store-backed path rather than leaving the cell silently empty.
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

    private void FinishPending()
    {
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            PendingEviction p = pending[i];
            if (!p.Save.IsCompleted) continue;
            pending.RemoveAt(i);
            // A faulted or canceled save (surfaced by the persistence driver through OnStoreError) leaves the cell
            // exactly as it was, dirty, for a later scan to retry. Nothing is removed on a write that did not land.
            if (p.Save.IsCompletedSuccessfully && p.Save.Result) Complete(p);
        }
    }

    private void Complete(in PendingEviction p)
    {
        // The cell kept simulating while the write was in flight. Unloading it now would discard anything that
        // changed since, so re-verify both the persistable bytes and the owned population (an entity carrying no
        // Persist components changes the latter without changing the former) and back off if either moved.
        if (!host.TryReadEvictionSignals(p.Coord, out CellEvictionSignals signals)) return;
        if (signals.OwnedEntityCount != p.OwnedCount) return;
        byte[]? now = host.SnapshotCell(p.Coord);
        if (now is null || !now.AsSpan().SequenceEqual(p.Snapshot)) return;
        if (!host.CanEvictCell(p.Coord) || !host.EvictCell(p.Coord)) return;

        Cache(p.Coord, p.Snapshot);
        idleSeconds.Remove(p.Coord);
        EvictedCount++;
        CellEvicted?.Invoke(p.Coord);
    }

    // Arms the synchronous restore for an evicted coord. Arming it means CellPersistence must NOT also load that
    // coord from the store when it is recreated, so the driver's per-cell load bookkeeping is deliberately left in
    // place. Dropping a coord from the cache is therefore always paired with ForgetCell, which re-arms the
    // store-backed path. Exactly one of the two is live per evicted coordinate.
    private void Cache(CellCoord coord, byte[] snapshot)
    {
        if (config.MaxCachedSnapshots <= 0)
        {
            persistence.ForgetCell(coord);
            return;
        }
        cachedSnapshots[coord] = snapshot;
        cacheOrder.Add(coord);
        while (cacheOrder.Count > config.MaxCachedSnapshots)
        {
            CellCoord oldest = cacheOrder[0];
            cacheOrder.RemoveAt(0);
            if (cachedSnapshots.Remove(oldest)) persistence.ForgetCell(oldest);
        }
    }

    private void Scan(float elapsed)
    {
        scanScratch.Clear();
        scanScratch.AddRange(host.LiveCellCoords);
        int budget = config.MaxEvictionsPerScan;

        for (int i = 0; i < scanScratch.Count; i++)
        {
            CellCoord coord = scanScratch[i];
            if (!host.TryReadEvictionSignals(coord, out CellEvictionSignals signals)) continue;

            float idle = signals.BoundPlayerCount > 0
                ? 0f
                : (idleSeconds.TryGetValue(coord, out float prev) ? prev : 0f) + elapsed;
            idleSeconds[coord] = idle;

            if (budget <= 0) continue;
            if (!policy.ShouldEvict(signals.WithIdleSeconds(idle))) continue;
            if (RequestEvict(coord)) budget--;
        }

        PruneIdleEntries();
    }

    // Drops idle counters for coordinates that are no longer live, so the map tracks the live grid rather than
    // every cell the server has ever instantiated. scanScratch still holds this scan's live coordinates.
    private void PruneIdleEntries()
    {
        if (idleSeconds.Count == scanScratch.Count) return;
        pruneScratch.Clear();
        foreach (KeyValuePair<CellCoord, float> kv in idleSeconds)
            if (!scanScratch.Contains(kv.Key)) pruneScratch.Add(kv.Key);
        for (int i = 0; i < pruneScratch.Count; i++) idleSeconds.Remove(pruneScratch[i]);
        pruneScratch.Clear();
    }
}
