using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;

namespace KhaozEngine.Sharding;

/// <summary>
/// Cell eviction: the mechanical removal half. Persisting the cell first is the caller's job (see
/// <c>KhaozEngine.NetWorld.CellEvictor</c>, which snapshots and waits for the store write to land before calling
/// <see cref="ShardHost.RemoveCell"/>). Sharding stays storage-agnostic, so nothing here reads or writes bytes.
/// </summary>
public sealed partial class ShardHost
{
    // Reused by RemoveCell to collect the ownership-index keys to drop, so the sweep never mutates a dictionary it
    // is enumerating. Eviction is a cold path, so this only avoids a per-eviction allocation.
    private readonly List<long> evictScratch = new();

    /// <summary>
    /// Raised after a cell has been removed from the host, with the removed <see cref="CellSim"/>. Its world is
    /// still readable inside the handler (for a last diagnostic) but it is no longer part of the grid and will not
    /// tick again. The mirror of <see cref="CellCreated"/>: a subscriber that cached anything per cell (a wired
    /// system set, a per-coord bookkeeping entry) drops it here, because a later <see cref="CellCreated"/> for the
    /// same coordinate hands out a genuinely fresh cell.
    /// </summary>
    /// <remarks>
    /// <b>Do not recreate this coordinate from inside the handler</b> (an <see cref="EnsureCell"/> call, or
    /// anything that routes to one). Any synchronous restore for the removal is armed only after this event
    /// returns - a persist-then-evict caller like <c>KhaozEngine.NetWorld.CellEvictor</c> caches the eviction
    /// snapshot after <see cref="RemoveCell"/> comes back, so an <see cref="EnsureCell"/> called from here always
    /// gets a fresh, empty <see cref="CellSim"/> instead. Defer the recreation to a later call.
    /// </remarks>
    public event Action<CellSim>? CellRemoved;

    /// <summary>
    /// Whether <see cref="RemoveCell"/> would succeed right now. False when the coordinate is not instantiated, or
    /// when removing it would lose state that is not the caller's to persist:
    /// <list type="bullet">
    /// <item>an entity is <see cref="Migrating"/> out of it, so the handoff handshake is still open,</item>
    /// <item>inter-cell traffic is queued for it on the <see cref="CellLink"/> (an inbound migrate would be
    /// stranded),</item>
    /// <item>it owns the player entity of a client bound through <see cref="BindClient"/>.</item>
    /// </list>
    /// </summary>
    public bool CanRemoveCell(CellCoord coord)
    {
        if (!cells.TryGetValue(coord, out CellSim? cell)) return false;
        if (cell.HasMigratingEntities) return false;
        if (link.HasPending(coord)) return false;
        // Index-only membership check: a scanning CellSim.TryGetOwned per bound client would cost
        // players x cellPopulation here, paid on every eviction candidate. ownerCell already answers "does this
        // netId belong to this coord" in O(1).
        foreach (long playerNetId in clientPlayerNetId.Values)
            if (ownerCell.TryGetValue(playerNetId, out CellCoord owner) && owner == coord) return false;
        return true;
    }

    /// <summary>
    /// Removes an instantiated cell from the grid: it stops ticking, stops being ghosted into and out of, and every
    /// entity it owned ceases to exist. <b>Call this only once the cell's state is durably saved</b> - the host has
    /// no persistence of its own, so an unsaved cell removed here is simply gone.
    /// </summary>
    /// <returns>False (changing nothing) when <see cref="CanRemoveCell"/> refuses.</returns>
    /// <remarks>
    /// Removal drops every trace of the cell that would otherwise outlive it: its entries in the netId -&gt; cell
    /// ownership index, the ghosts each neighbour mirrored from it (and the neighbour's view keyed on it, which no
    /// live source would ever refresh again), the cached per-world snapshot index the serve pass keeps, its inbox on
    /// the <see cref="CellLink"/>, and the register/unregister hooks tying it to this host. A later
    /// <see cref="EnsureCell"/> for the same coordinate builds a genuinely fresh cell and raises
    /// <see cref="CellCreated"/> again, which is the hook a persistence layer restores through.
    /// </remarks>
    public bool RemoveCell(CellCoord coord)
    {
        if (!CanRemoveCell(coord)) return false;
        CellSim cell = cells[coord];

        // The ownership index can only point here for netIds this cell's owned index also holds (both are written
        // by the same register hook, and the unregister hook clears the host entry only while it still points
        // here), so the cell's own index is the complete key set to sweep.
        evictScratch.Clear();
        foreach (KeyValuePair<long, Entity> kv in cell.OwnedIndexEntries)
            if (ownerCell.TryGetValue(kv.Key, out CellCoord owner) && owner == coord) evictScratch.Add(kv.Key);
        for (int i = 0; i < evictScratch.Count; i++) ownerCell.Remove(evictScratch[i]);
        evictScratch.Clear();

        // Neighbours drop the ghosts this cell mirrored into them, and forget the view: with the source gone no
        // ghost sync will ever refresh or clear it.
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            if (dx == 0 && dy == 0) continue;
            if (cells.TryGetValue(new CellCoord(coord.X + dx, coord.Y + dy), out CellSim? neighbor))
                neighbor.RemoveGhostView(coord);
        }

        cells.Remove(coord);
        ordered.Remove(cell);
        cellsVersion++;                    // invalidates Tick's reused fan-out buffer
        clientIndexByWorld.Remove(cell.World);   // else the serve-pass index cache pins the dead world alive
        link.Forget(coord);
        cell.OwnedRegisteredHook = null;   // a late register on the detached cell must not write back into the index
        cell.OwnedUnregisteredHook = null;

        CellRemoved?.Invoke(cell);
        cell.Retire();
        return true;
    }

    /// <summary>
    /// Appends the home cell of every bound client (the cell that owns its player) to <paramref name="into"/>. The
    /// raw signal an eviction policy needs for "is anyone near this cell". A client bound to a player no cell owns
    /// contributes nothing. Duplicates are kept, so the count of a coordinate is the number of clients homed there.
    /// </summary>
    public void CollectBoundPlayerCells(List<CellCoord> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        foreach (long playerNetId in clientPlayerNetId.Values)
            if (TryGetOwner(playerNetId, out CellSim cell, out _)) into.Add(cell.Coord);
    }
}
