using System;
using System.Collections.Generic;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// <see cref="ICellEvictionHost"/>: the server side of unloading idle cells. Wire a <see cref="CellEvictor"/> over
/// this and the server's <see cref="CellPersistence"/> to bound the cell count on a long-running world.
/// </summary>
public sealed partial class ShardedWorldServer
{
    // The bound-player home-cell set (and its pinned-coord projection), cached across a scan instead of rebuilt per
    // cell: CellEvictor.Scan calls TryReadEvictionSignals once per live cell, then RequestEvict and Complete each
    // call it again per candidate, and CanEvictCell/EvictCell each did their own O(players) pin walk on top. That
    // used to be an O(players) CollectBoundPlayerCells walk PLUS an O(players) pin walk on every one of those calls.
    // boundPlayerCellsVersion is bumped only where a player's home cell can actually change - BindClient/
    // UnbindClient (a join/leave) and a handoff moving ownership - so a whole scan's worth of calls costs one
    // O(players) rebuild rather than one per call.
    private readonly List<CellCoord> boundPlayerCellsCache = new();
    private readonly HashSet<CellCoord> pinnedCellsCache = new();
    private int boundPlayerCellsVersion;
    private int boundPlayerCellsCachedVersion = -1;

    /// <inheritdoc />
    public bool CanEvictCell(CellCoord coord)
    {
        RefreshBoundPlayerCellsIfStale();
        return !pinnedCellsCache.Contains(coord) && host.CanRemoveCell(coord);
    }

    /// <inheritdoc />
    public bool EvictCell(CellCoord coord)
    {
        RefreshBoundPlayerCellsIfStale();
        return !pinnedCellsCache.Contains(coord) && host.RemoveCell(coord);
    }

    /// <inheritdoc />
    public bool TryReadEvictionSignals(CellCoord coord, out CellEvictionSignals signals)
    {
        signals = default;
        if (!host.TryGetCell(coord, out CellSim cell)) return false;

        RefreshBoundPlayerCellsIfStale();
        int here = 0;
        int nearest = int.MaxValue;
        for (int i = 0; i < boundPlayerCellsCache.Count; i++)
        {
            CellCoord p = boundPlayerCellsCache[i];
            if (p == coord) here++;
            int distance = Math.Max(Math.Abs(p.X - coord.X), Math.Abs(p.Y - coord.Y));
            if (distance < nearest) nearest = distance;
        }

        signals = new CellEvictionSignals(coord, cell.OwnedCount, here, nearest, pinnedCellsCache.Contains(coord));
        return true;
    }

    // A cell owning a joined player's entity is never evictable. Player state persists on the player record, not in
    // the cell snapshot (SnapshotCell excludes player NetIds), so unloading such a cell would destroy it outright.
    // ShardHost.CanRemoveCell refuses the same cells via the client bindings. pinnedCellsCache.Contains(coord)
    // above is the server's own check, on the slot table that is the authority on who is joined.

    // Rebuilds the bound-player-cells cache (and its pinned-coord projection) only when boundPlayerCellsVersion has
    // moved since the last build, so repeat reads across one scan cost a single O(players) walk.
    private void RefreshBoundPlayerCellsIfStale()
    {
        if (boundPlayerCellsCachedVersion == boundPlayerCellsVersion) return;
        boundPlayerCellsCache.Clear();
        host.CollectBoundPlayerCells(boundPlayerCellsCache);
        pinnedCellsCache.Clear();
        for (int i = 0; i < boundPlayerCellsCache.Count; i++) pinnedCellsCache.Add(boundPlayerCellsCache[i]);
        boundPlayerCellsCachedVersion = boundPlayerCellsVersion;
    }
}
