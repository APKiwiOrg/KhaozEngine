using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The server-side surface <see cref="CellPersistence"/> drives, so the same per-cell persistence wiring serves
/// any <see cref="ShardHost"/>-based server. Cell-keyed and player-agnostic: <see cref="SnapshotCell(CellCoord)"/> returns a
/// cell's persistable (owned, non-player, non-ghost, non-migrating) entities, and <see cref="RestoreCell"/> puts
/// them back. The host owns the <see cref="KhaozEngine.Replication.NetId"/> allocator so restored entities can never collide with fresh
/// spawns after a restart (see <see cref="EnsureNextNetIdAtLeast"/>).
/// </summary>
public interface ICellPersistenceHost
{
    /// <summary>Raised each time a cell coordinate is instantiated, including a recreate after eviction (not only
    /// the first time): persistence loads that cell's saved state here. Implementations MUST raise this on the
    /// server/simulation thread - the same thread that calls <see cref="CellPersistence.Update"/> - not from a
    /// worker or IO thread. The persistence driver's per-cell load bookkeeping is single-threaded on that
    /// assumption.</summary>
    event Action<CellCoord>? CellCreated;

    /// <summary>The coordinates of all currently instantiated cells (for the periodic dirty pass + flush).</summary>
    IReadOnlyCollection<CellCoord> LiveCellCoords { get; }

    /// <summary>
    /// The live replication registry this host restores cells with, which <see cref="CellPersistence"/> takes as the
    /// default for <see cref="CellPersistenceConfig.Registry"/> so the inference that brings a pre-v4 blob forward is
    /// registry-aware without the consumer wiring the same object in twice. <c>ShardedWorldServer</c> already exposes
    /// it, so it satisfies this implicitly. The default is null (no registry-aware validation), which is what a host
    /// that has no registry to offer keeps, and the config knob overrides it either way.
    /// </summary>
    ReplicationRegistry? Registry => null;

    /// <summary>The durable snapshot of a cell's persistable entities, or null if the cell is not instantiated.</summary>
    byte[]? SnapshotCell(CellCoord coord);

    /// <summary>
    /// The snapshot of a cell's owned entities for <paramref name="purpose"/>, or null if the cell is not
    /// instantiated. The two purposes differ in exactly one thing: how far a
    /// <see cref="KhaozEngine.Sharding.Transient"/> mark reaches, so an
    /// <see cref="SnapshotPurpose.Eviction"/> capture keeps a
    /// <see cref="KhaozEngine.Sharding.TransientScope.DurableOnly"/> entity that the durable one leaves out (#668).
    /// <para>The default ignores the purpose and returns <see cref="SnapshotCell(CellCoord)"/>, which is the
    /// behaviour every host had before 17.39.0, so an existing implementer is unaffected and keeps destroying a
    /// marked entity on an unload. A <see cref="KhaozEngine.Sharding.CellSim"/>-backed host (like
    /// <c>ShardedWorldServer</c>) overrides it to honour the scope. Call on the server thread.</para>
    /// </summary>
    byte[]? SnapshotCell(CellCoord coord, SnapshotPurpose purpose) => SnapshotCell(coord);

    /// <summary>
    /// The <see cref="KhaozEngine.Sharding.Transient"/> marks a <paramref name="purpose"/> capture of this cell
    /// KEEPS the entity for but cannot encode, keyed by net id. The marker is in no
    /// <see cref="ReplicationRegistry"/> by design, so it reaches no bytes and a restored entity comes back
    /// unmarked: a caller holding a capture carries these beside it and hands them to
    /// <see cref="ApplyTransientMarks"/> on the far side, which is what stops an unloaded-then-restored
    /// <see cref="KhaozEngine.Sharding.TransientScope.DurableOnly"/> entity from becoming persistable on the way
    /// back (#668). Empty by default and for <see cref="SnapshotPurpose.Durable"/>, which excludes every marked
    /// entity anyway. Call on the server thread.
    /// </summary>
    IReadOnlyDictionary<long, TransientScope> ReadTransientMarks(CellCoord coord, SnapshotPurpose purpose) =>
        ReadOnlyDictionary<long, TransientScope>.Empty;

    /// <summary>
    /// Re-applies marks read by <see cref="ReadTransientMarks"/> to the entities a restore just put back into
    /// <paramref name="coord"/>, so the far side of a capture is transient at the same scope it left at. A net id
    /// the cell does not own is skipped. No-op by default, which is the pre-17.39.0 behaviour and consistent with a
    /// host whose <see cref="ReadTransientMarks"/> returns nothing to carry. Call on the server thread.
    /// </summary>
    void ApplyTransientMarks(CellCoord coord, IReadOnlyDictionary<long, TransientScope> marks) { }

    /// <summary>Restores entities into a cell (call on the server thread). Returns the restored NetId values.</summary>
    IReadOnlyList<long> RestoreCell(CellCoord coord, byte[] snapshot);

    /// <summary>
    /// Restores entities into a cell reporting decode success (so the driver can quarantine a corrupt blob instead of
    /// crash-looping) and how many unknown extension frames were retained. The default wraps <see cref="RestoreCell"/>
    /// as an always-Ok result for back-compat; <see cref="KhaozEngine.Sharding.CellSim.TryRestoreOwned"/>-backed hosts
    /// (like <c>ShardedWorldServer</c>) override it to surface a genuine decode failure and retention count. Call on
    /// the server thread.
    /// </summary>
    CellRestoreResult TryRestoreCell(CellCoord coord, byte[] snapshot)
        => new(true, RestoreCell(coord, snapshot), 0, null);

    /// <summary>Instantiates a cell by coordinate (firing <see cref="CellCreated"/> if new); used by preload.</summary>
    void EnsureCell(CellCoord coord);

    /// <summary>The next NetId the allocator will hand out (the packed 64-bit high-water for the local node).</summary>
    long NextNetId { get; }

    /// <summary>Raises the allocator so its next id is at least <paramref name="atLeast"/> (never lowers it).</summary>
    void EnsureNextNetIdAtLeast(long atLeast);
}
