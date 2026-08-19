using System;
using System.Collections.Generic;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// A minimal real-<see cref="ShardHost"/> <see cref="ICellPersistenceHost"/>, so a
/// <see cref="CellPersistence"/> boot test restores through the actual <c>CellSim</c> decode path (the live
/// registry, the rollback-on-failure contract, the retained-extension handling) rather than through a fake that
/// records whatever bytes it is handed. A migration that produces a body the real decoder rejects has to go red
/// somewhere, and this is where.
/// </summary>
internal sealed class ShardPersistenceHost : ICellPersistenceHost
{
    private readonly NetIdAllocator alloc = new();

    /// <param name="registry">The registry the shard decodes with.</param>
    /// <param name="exposeRegistry">Whether to also offer it as <see cref="ICellPersistenceHost.Registry"/>, the
    /// default a <see cref="CellPersistence"/> falls back on. Off by default so a test that wants the un-filtered
    /// inference keeps getting it.</param>
    internal ShardPersistenceHost(ReplicationRegistry registry, bool exposeRegistry = false)
    {
        Shard = new ShardHost(64f, 1f / 30f, registry);
        Shard.CellCreated += c => CellCreated?.Invoke(c.Coord);
        Registry = exposeRegistry ? registry : null;
    }

    internal ShardHost Shard { get; }

    public ReplicationRegistry? Registry { get; }

    public event Action<CellCoord>? CellCreated;

    public IReadOnlyCollection<CellCoord> LiveCellCoords
    {
        get
        {
            var coords = new List<CellCoord>();
            foreach (CellSim c in Shard.Cells) coords.Add(c.Coord);
            return coords;
        }
    }

    public byte[]? SnapshotCell(CellCoord c) =>
        Shard.TryGetCell(c, out CellSim cell) ? cell.SnapshotOwned(new HashSet<long>()) : null;

    public IReadOnlyList<long> RestoreCell(CellCoord c, byte[] snapshot) =>
        Shard.TryGetCell(c, out CellSim cell) ? cell.RestoreOwned(snapshot) : Array.Empty<long>();

    public CellRestoreResult TryRestoreCell(CellCoord c, byte[] snapshot) =>
        Shard.TryGetCell(c, out CellSim cell) ? cell.TryRestoreOwned(snapshot) : new CellRestoreResult(true, Array.Empty<long>(), 0, null);

    public void EnsureCell(CellCoord c) => Shard.EnsureCell(c);

    public long NextNetId => alloc.NextValue;

    public void EnsureNextNetIdAtLeast(long atLeast) => alloc.EnsureNextAtLeast(atLeast);
}
