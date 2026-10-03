using System;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Friend-test diagnostics of a client's format 2 ingest. Reading them never writes prediction state.</summary>
/// <param name="PendingPredictionCommands">Predicted commands not yet acknowledged by an ingested frame.</param>
/// <param name="IngestCount">Authoritative ingests on every path, legacy included.</param>
/// <param name="AcceptedCount">Accepted format 2 projections on the current connection.</param>
/// <param name="LastMovementAck">The movement ack of the last ingest.</param>
/// <param name="MaxTransportPayloadBytes">The largest format 2 control payload sent on the current connection.</param>
internal readonly record struct WorldClientRebuildDiagnostics(
    int PendingPredictionCommands,
    int IngestCount,
    int AcceptedCount,
    int LastMovementAck,
    int MaxTransportPayloadBytes);

// Format 2 integration: one RebuildClientStream per connection, built wherever a connection and view are built, its
// frames routed ahead of the legacy demux, its scheduled controls run after each receive drain, and its typed failures
// ending the session locally. Only an accepted projection reaches IngestServerState, the sole reconcile path.
public sealed partial class WorldClient
{
    private ReplicationStreamOptions? rebuildOptions;
    private RebuildClientStream? rebuild;
    private int rebuildIngestCount;
    private int rebuildLastMovementAck;

    /// <summary>The replication mode this connection runs and why. Legacy reliable and unnegotiated unless
    /// <see cref="WorldClientConfig.RequestUnreliableDeltaReplication"/> is on and the server answered: a mode 0 offer
    /// names its fallback reason, an accepted mode 1 offer reads acknowledged unreliable with the current epoch. Reset
    /// with every new connection. An older server's silence leaves it unnegotiated.</summary>
    public ReplicationSelection ReplicationSelection => rebuild?.Selection ?? default;

    internal int PendingPredictionCommands => prediction.PendingCommandCount;

    internal WorldClientRebuildDiagnostics RebuildDiagnosticsForTest => new(PendingPredictionCommands,
        rebuildIngestCount, rebuild?.AcceptedCount ?? 0, rebuildLastMovementAck, rebuild?.MaxTransportPayloadBytesSent ?? 0);

    internal ClientDeltaRebuild? DeltaRebuildForTest => rebuild?.Rebuild;

    internal RebuildClientStream? RebuildStreamForTest => rebuild;

    internal World WorldForTest => world;

    internal ClientReplicationView ViewForTest => view;

    private bool RebuildOwnsLiveness => rebuild?.OwnsLiveness ?? false;

    private bool RebuildRecoveryActive => rebuild?.RecoveryActive ?? false;

    private bool RebuildEnded => rebuild?.Failure is not null;

    // Constructor step: validates the format 2 config (D2.8 refusal, D2.9 retention rule, D2.15 payload cap) and builds
    // the first connection's stream. Null when the client did not opt in.
    private RebuildClientStream? StartRebuildStream(WorldClientConfig config)
    {
        if (config.RequestUnreliableDeltaReplication)
        {
            ReplicationStreamOptions options = config.ReplicationStream ?? throw new ArgumentException(
                "ReplicationStream is required with RequestUnreliableDeltaReplication.", nameof(config));
            options.ValidateConfig(config.RequestDeltaReplication, optIn: true, config.TickSeconds);
            rebuildOptions = options;
        }
        return NewRebuildStream();
    }

    private RebuildClientStream? NewRebuildStream() =>
        rebuildOptions is null ? null : new RebuildClientStream(net, registry, view, rebuildOptions, tickSeconds);

    // A Joined edge: the stream starts clean, then capability 3 follows capability 2 reliably.
    private void OnRebuildJoined()
    {
        if (rebuild is null) return;
        rebuild.Reset();
        net.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.RebuildDeltaCapable),
            NetChannelReliability.ReliableOrdered);
    }

    // True when the frame belongs to the format 2 stream. A live stream also swallows legacy state frames, so the
    // world it built is never overlaid by the legacy reader. Without a stream every kind takes the legacy demux.
    private bool RouteRebuildFrame(MoveProtocol.ServerFrameKind kind, byte[] payload, NetChannelReliability reliability)
    {
        if (rebuild is null) return false;
        bool owned = kind is MoveProtocol.ServerFrameKind.ReplicationMode or MoveProtocol.ServerFrameKind.RebuildDelta
            or MoveProtocol.ServerFrameKind.RebuildKeyframeChunk;
        if (!owned) return rebuild.OwnsLiveness && (kind is MoveProtocol.ServerFrameKind.Snapshot
            or MoveProtocol.ServerFrameKind.Delta);
        if (state == WorldConnectionState.Disconnected) return true;
        string? error;
        switch (kind)
        {
            case MoveProtocol.ServerFrameKind.ReplicationMode:
                if (!rebuild.ReceiveOffer(payload, reliability, out error)) EndRebuild(error);
                break;
            case MoveProtocol.ServerFrameKind.RebuildDelta:
                OnRebuildResult(rebuild.ReceiveDelta(world, payload, reliability, out long deltaNetId, out int deltaAck,
                    out error), deltaNetId, deltaAck, error);
                break;
            default:
                OnRebuildResult(rebuild.ReceiveChunk(world, payload, reliability, out long keyNetId, out int keyAck,
                    out error), keyNetId, keyAck, error);
                break;
        }
        return true;
    }

    // Accepted alone ingests, once, with the movement ack bundled in that projection, and alone refreshes valid-state
    // liveness. Stale, ignored, partial and missing-baseline results touch neither.
    private void OnRebuildResult(DeltaRebuildResult result, long localNetId, int movementAck, string? error)
    {
        if (result == DeltaRebuildResult.Accepted)
        {
            secondsSinceServerFrame = 0f;
            IngestServerState(localNetId, movementAck);
        }
        else if (result == DeltaRebuildResult.Invalid)
        {
            EndRebuild(error);
        }
    }

    private void EndRebuild(string? error)
    {
        if (rebuild!.Failure is ReplicationFailure failure) EndOnReplicationFailure(failure);
        else OnSnapshotDecodeFailed(error ?? "format 2 replication decode failed");
    }

    // Scheduled control work from elapsed Poll time, after the receive drain. Zero dt drains spend no allowance.
    private void PumpRebuildStream(float dt)
    {
        if (rebuild is null || state != WorldConnectionState.Connected) return;
        rebuild.Advance(dt);
        rebuild.AfterReceiveDrain();
        if (rebuild.Failure is ReplicationFailure failure) EndOnReplicationFailure(failure);
    }

    // A typed failure ends the session locally at once. The client never waits for a Disconnected event, and the guard
    // on that event keeps a later one from reading as a drop that schedules a reconnect. A client-detected failure is
    // always one of the three terminal reasons.
    private void EndOnReplicationFailure(ReplicationFailure failure)
    {
        if (state == WorldConnectionState.Disconnected) return;
        disconnectReason = failure.Reason;
        disconnectReasonDetail = failure.Detail;
        net.Disconnect();
        FailAttempt(allowReconnect: !failure.IsTerminal);
    }

    private void RecordRebuildIngest(int movementAck)
    {
        rebuildIngestCount++;
        rebuildLastMovementAck = movementAck;
    }
}
