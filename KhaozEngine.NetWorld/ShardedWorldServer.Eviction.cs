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
    // Reused by the signal read, which runs once per live cell per eviction scan (seconds apart), never per tick.
    private readonly List<CellCoord> playerCellScratch = new();

    /// <inheritdoc />
    public bool CanEvictCell(CellCoord coord) => !IsCellPinned(coord) && host.CanRemoveCell(coord);

    /// <inheritdoc />
    public bool EvictCell(CellCoord coord) => !IsCellPinned(coord) && host.RemoveCell(coord);

    /// <inheritdoc />
    public bool TryReadEvictionSignals(CellCoord coord, out CellEvictionSignals signals)
    {
        signals = default;
        if (!host.TryGetCell(coord, out CellSim cell)) return false;

        playerCellScratch.Clear();
        host.CollectBoundPlayerCells(playerCellScratch);
        int here = 0;
        int nearest = int.MaxValue;
        for (int i = 0; i < playerCellScratch.Count; i++)
        {
            CellCoord p = playerCellScratch[i];
            if (p == coord) here++;
            int distance = Math.Max(Math.Abs(p.X - coord.X), Math.Abs(p.Y - coord.Y));
            if (distance < nearest) nearest = distance;
        }

        signals = new CellEvictionSignals(coord, cell.OwnedCount, here, nearest, IsCellPinned(coord));
        return true;
    }

    // A cell owning a joined player's entity is never evictable. Player state persists on the player record, not in
    // the cell snapshot (SnapshotCell excludes player NetIds), so unloading such a cell would destroy it outright.
    // ShardHost.CanRemoveCell refuses the same cells via the client bindings. This is the server's own check, on
    // the slot table that is the authority on who is joined.
    private bool IsCellPinned(CellCoord coord)
    {
        foreach (long netId in netIdBySlot.Values)
            if (host.TryGetOwner(netId, out CellSim cell, out _) && cell.Coord == coord) return true;
        return false;
    }
}
