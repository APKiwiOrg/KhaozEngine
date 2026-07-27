using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The extra server surface <see cref="CellEvictor"/> needs on top of <see cref="ICellPersistenceHost"/> to unload
/// a cell once its state is durably saved. Split out rather than folded into <see cref="ICellPersistenceHost"/>
/// because eviction is opt-in: a bounded world persists fine without ever unloading anything.
/// <c>ShardedWorldServer</c> implements both.
/// </summary>
/// <remarks>
/// Everything here is called on the server thread, from <see cref="CellEvictor.Update"/>. The eviction driver
/// re-checks <see cref="CanEvictCell"/> immediately before <see cref="EvictCell"/>, and treats a false from either
/// as "keep this cell and try again later", so an implementation is free to refuse for its own reasons at any
/// moment.
/// </remarks>
public interface ICellEvictionHost : ICellPersistenceHost
{
    /// <summary>
    /// Whether the cell exists and could be removed right now. False for a coordinate that is not instantiated, one
    /// mid-handoff, and one holding state a cell snapshot does not carry (a joined player's entity).
    /// </summary>
    bool CanEvictCell(CellCoord coord);

    /// <summary>
    /// Removes an instantiated cell: it stops ticking and every entity it owned ceases to exist. <b>Only ever call
    /// this once the cell's snapshot is durably stored</b>, which is what <see cref="CellEvictor"/> guarantees.
    /// Returns false (changing nothing) when the host refuses, on the same grounds as <see cref="CanEvictCell"/>.
    /// </summary>
    bool EvictCell(CellCoord coord);

    /// <summary>
    /// Reads the host-side eviction signals for a live cell, with <see cref="CellEvictionSignals.IdleSeconds"/>
    /// left at 0 (the driver tracks idle time, the host does not). False when the coordinate is not instantiated.
    /// </summary>
    bool TryReadEvictionSignals(CellCoord coord, out CellEvictionSignals signals);
}
