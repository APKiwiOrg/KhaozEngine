using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

public sealed partial class ShardedWorldServer
{
    // Format 2 negotiation and stream state, shared in shape with WorldServer.
    private readonly RebuildServerStreams replication;

    /// <summary>The replication mode this server selected for a joined slot. A slot whose client never asked for
    /// format 2 reads legacy reliable and unnegotiated. False for a slot with no joined player.</summary>
    /// <param name="slot">The session slot.</param>
    /// <param name="selection">The slot's selection, or default when false.</param>
    public bool TryGetReplicationSelection(int slot, out ReplicationSelection selection)
    {
        selection = default;
        if (!netIdBySlot.ContainsKey(slot)) return false;
        selection = replication.SelectionOf(slot);
        return true;
    }

    /// <summary>Test seam: the shared delta writer, or null when delta replication is off.</summary>
    internal AoiDeltaReplicator? DeltaReplicatorForTest => deltaReplicator;

    /// <summary>Test seam: the last format 2 epoch this server issued.</summary>
    internal ulong ReplicationEpochHighWaterForTest => replication.EpochHighWater;

    /// <summary>Test seam: records <paramref name="lastIssued"/> as the last format 2 epoch this server
    /// issued.</summary>
    internal void SeedReplicationEpochForTest(ulong lastIssued) => replication.SeedEpochsForTest(lastIssued);

    /// <summary>Test seam: sessions ended for a writer restart whose leave has not run yet.</summary>
    internal int ReplicationRestartPendingForTest => replication.PendingRestartCount;

    /// <summary>Test seam: invoked once per served slot after the visibility filter and before either writer, with
    /// the slot, the served world, the filtered interest and the owner net id. Copy the set, never keep it.</summary>
    internal Action<int, World, IReadOnlySet<long>, long>? ServeObservedForTest
    {
        get => replication.ServeObserved;
        set => replication.ServeObserved = value;
    }

    /// <summary>Test seam: the slot's format 2 stream, when its client asked for one.</summary>
    internal bool TryGetRebuildStreamForTest(int slot, out RebuildServerStream stream) =>
        replication.TryGetStream(slot, out stream);
}
