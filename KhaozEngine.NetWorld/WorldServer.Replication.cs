using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

public sealed partial class WorldServer
{
    private readonly ReplicationCadence? replicationCadence;
    private readonly ReplicationEpochAllocator replicationEpochs = new();
    private readonly Dictionary<int, RebuildServerStream> rebuildStreams = new();
    private readonly List<(int Slot, string Token)> failedReplicationSlots = new();

    // The replication tick negotiation deadlines count in. Zero without format 2, where no deadline runs.
    private long ReplicationTick => replicationCadence?.Tick ?? 0;

    /// <summary>The replication mode this server selected for a joined slot. A slot whose client never asked for
    /// format 2 reads legacy reliable and unnegotiated. False for a slot with no joined player.</summary>
    /// <param name="slot">The session slot.</param>
    /// <param name="selection">The slot's selection, or default when false.</param>
    public bool TryGetReplicationSelection(int slot, out ReplicationSelection selection)
    {
        selection = default;
        if (!netIdBySlot.ContainsKey(slot)) return false;
        if (rebuildStreams.TryGetValue(slot, out RebuildServerStream? stream)) selection = stream.Selection;
        return true;
    }

    /// <summary>Test seam: the shared delta writer, or null when delta replication is off.</summary>
    internal AoiDeltaReplicator? DeltaReplicatorForTest => deltaReplicator;

    /// <summary>Test seam: the last format 2 epoch this server issued.</summary>
    internal ulong ReplicationEpochHighWaterForTest => replicationEpochs.HighWater;

    /// <summary>Test seam: invoked once per served slot after the visibility filter and before either writer, with
    /// the slot, the served world, the filtered interest and the owner net id. Copy the set, never keep it.</summary>
    internal Action<int, World, IReadOnlySet<long>, long>? ServeObservedForTest { get; set; }

    /// <summary>Test seam: the slot's format 2 stream, when its client asked for one.</summary>
    internal bool TryGetRebuildStreamForTest(int slot, out RebuildServerStream stream) =>
        rebuildStreams.TryGetValue(slot, out stream!);

    // Refuses a format 2 opt-in without DeltaReplication and checks every format 2 budget before anything starts. A
    // reliable-only config is never checked and gets no cadence, so it cannot start throwing here.
    private static ReplicationCadence? CreateReplicationCadence(WorldServerConfig config)
    {
        if (!config.AllowUnreliableDeltaReplication) return null;
        ReplicationStreamOptions options = config.ReplicationStream
            ?? throw new ArgumentException("ReplicationStream is required with AllowUnreliableDeltaReplication.",
                nameof(config));
        options.ValidateConfig(config.DeltaReplication, config.AllowUnreliableDeltaReplication, config.TickSeconds);
        return new ReplicationCadence(config.TickSeconds);
    }

    // Runs right after the rate limiter. Length 18 is always a MOVE and is validated and stored here. Any other
    // length is offered to the format 2 control decode, which claims its own sub-markers before the permissive MOVE
    // fallback can see them. False leaves the frame to the legacy ack, control and game-message decodes.
    private bool RouteClientFrame(int slot, byte[] data, NetChannelReliability reliability)
    {
        if (data.Length == RebuildServerStream.MoveFrameBytes)
        {
            if (MoveProtocol.TryDecodeMove(data, out int seq, out MoveCommand cmd)) commands.Store(slot, seq, cmd);
            else Raise(slot, SuspiciousReason.MalformedPacket);
            return true;
        }
        switch (RebuildProtocol.DecodeClientControl(data, out RebuildClientControl control))
        {
            case ControlReadResult.Unclaimed:
                return false;
            case ControlReadResult.Valid when RebuildServerStream.IsRequiredChannel(control.Kind, reliability):
                if (rebuildStreams.TryGetValue(slot, out RebuildServerStream? stream))
                    stream.HandleControl(control, reliability, ReplicationTick);
                return true;
            default:
                Raise(slot, SuspiciousReason.MalformedPacket);
                return true;
        }
    }

    private void OnRebuildCapability(int slot)
    {
        if (!rebuildStreams.TryGetValue(slot, out RebuildServerStream? stream))
        {
            // Without format 2 the stream holds no writer and answers with the server policy fallback.
            stream = new RebuildServerStream(net, slot, replicationCadence is null ? null : deltaReplicator,
                config.ReplicationStream, replicationEpochs);
            rebuildStreams[slot] = stream;
        }
        stream.OnRebuildCapability(ReplicationTick);
    }

    // Once per Tick, from elapsed host time.
    private void AdvanceReplicationCadence(float dt) => replicationCadence?.Advance(dt);

    // True when format 2 owns the slot's state this tick, so the legacy writers must not serve it.
    private bool ServeReplicationSlot(int slot, World served, HashSet<long> filteredInterest, long ownerNetId)
    {
        ServeObservedForTest?.Invoke(slot, served, filteredInterest, ownerNetId);
        if (!rebuildStreams.TryGetValue(slot, out RebuildServerStream? stream) || stream.ServesLegacy) return false;
        stream.Serve(served, filteredInterest, ownerNetId, lastAckBySlot[slot], ReplicationTick);
        return true;
    }

    // Ends every session whose stream reported a typed failure, after the serve pass so no writer sees a slot vanish.
    private void EndReplicationServe()
    {
        if (rebuildStreams.Count == 0) return;
        failedReplicationSlots.Clear();
        foreach (KeyValuePair<int, RebuildServerStream> entry in rebuildStreams)
            if (entry.Value.Failure is ReplicationFailure failure) failedReplicationSlots.Add((entry.Key, failure.Token));
        foreach ((int slot, string token) in failedReplicationSlots) Disconnect(slot, token);
    }

    private void ForgetRebuildStream(int slot)
    {
        if (rebuildStreams.Remove(slot, out RebuildServerStream? stream)) stream.Forget();
    }
}
