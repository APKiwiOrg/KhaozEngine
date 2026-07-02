using System;
using System.Collections.Generic;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The server-side surface <see cref="CellPersistence"/> drives, so the same per-cell persistence wiring serves
/// any <see cref="ShardHost"/>-based server. Cell-keyed and player-agnostic: <see cref="SnapshotCell"/> returns a
/// cell's persistable (owned, non-player, non-ghost, non-migrating) entities, and <see cref="RestoreCell"/> puts
/// them back. The host owns the <see cref="KhaozEngine.Replication.NetId"/> allocator so restored entities can never collide with fresh
/// spawns after a restart (see <see cref="EnsureNextNetIdAtLeast"/>).
/// </summary>
public interface ICellPersistenceHost
{
    /// <summary>Raised when a cell is first instantiated: persistence loads that cell's saved state here.
    /// Implementations MUST raise this on the server/simulation thread - the same thread that calls
    /// <see cref="CellPersistence.Update"/> - not from a worker or IO thread. The persistence driver's
    /// per-cell load bookkeeping is single-threaded on that assumption.</summary>
    event Action<CellCoord>? CellCreated;

    /// <summary>The coordinates of all currently instantiated cells (for the periodic dirty pass + flush).</summary>
    IReadOnlyCollection<CellCoord> LiveCellCoords { get; }

    /// <summary>The durable snapshot of a cell's persistable entities, or null if the cell is not instantiated.</summary>
    byte[]? SnapshotCell(CellCoord coord);

    /// <summary>Restores entities into a cell (call on the server thread). Returns the restored NetId values.</summary>
    IReadOnlyList<int> RestoreCell(CellCoord coord, byte[] snapshot);

    /// <summary>Instantiates a cell by coordinate (firing <see cref="CellCreated"/> if new); used by preload.</summary>
    void EnsureCell(CellCoord coord);

    /// <summary>The next NetId the allocator will hand out.</summary>
    int NextNetId { get; }

    /// <summary>Raises the allocator so its next id is at least <paramref name="atLeast"/> (never lowers it).</summary>
    void EnsureNextNetIdAtLeast(int atLeast);
}
