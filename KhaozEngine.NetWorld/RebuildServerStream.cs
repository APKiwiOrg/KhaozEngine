using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>
/// One viewer's format 2 negotiation and stream state on a server: the mode it selected for that viewer, the offered
/// epoch and limits, and whether the viewer accepted them. It owns no movement or visibility policy. The host keeps
/// both and passes the already filtered interest set in.
/// </summary>
/// <remarks>
/// The stream answers a capability once. A mode 0 offer keeps the viewer on legacy reliable delivery. A mode 1 offer
/// ends legacy serving for the viewer the moment it is handed to reliable transport, because legacy state queued
/// before it already precedes it on that channel. Legacy serving never resumes on that connection. Negotiation must
/// finish within <see cref="ReplicationStreamOptions.RecoveryDeadlineTicks"/> replication ticks of the offer, or the
/// stream reports <see cref="DisconnectReason.ReplicationRecoveryFailed"/> for the host to act on.
/// </remarks>
internal sealed class RebuildServerStream
{
    /// <summary>The MOVE frame length. Every client payload of this length takes the MOVE decode first.</summary>
    internal const int MoveFrameBytes = 18;

    private readonly NetServer net;
    private readonly int slot;
    private readonly AoiDeltaReplicator? writer;
    private readonly ReplicationStreamOptions? options;
    private readonly DeltaRebuildOptions? limits;
    private readonly ReplicationEpochAllocator epochs;
    private Phase phase;
    private long offeredTick;

    /// <param name="net">The session server the stream sends through.</param>
    /// <param name="slot">The viewer's session slot.</param>
    /// <param name="writer">The host's shared delta writer, or null when server policy does not allow format 2. A
    /// null writer answers every capability with a mode 0 offer naming
    /// <see cref="ReplicationSelectionReason.DisabledServerPolicy"/>.</param>
    /// <param name="options">Validated stream options. Required with a writer, ignored without one.</param>
    /// <param name="limits">The host's validated stream limits, from
    /// <see cref="ReplicationStreamOptions.StreamLimits"/>. Required with a writer, ignored without one.</param>
    /// <param name="epochs">The host's server-lifetime epoch allocator.</param>
    /// <exception cref="ArgumentNullException"><paramref name="net"/> or <paramref name="epochs"/> is null, or a
    /// writer was given without options or limits.</exception>
    internal RebuildServerStream(NetServer net, int slot, AoiDeltaReplicator? writer, ReplicationStreamOptions? options,
        DeltaRebuildOptions? limits, ReplicationEpochAllocator epochs)
    {
        this.net = net ?? throw new ArgumentNullException(nameof(net));
        this.epochs = epochs ?? throw new ArgumentNullException(nameof(epochs));
        this.slot = slot;
        this.writer = writer;
        if (writer is not null)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        }
    }

    private enum Phase
    {
        Unnegotiated,
        Fallback,
        Offered,
        Accepted,
    }

    /// <summary>The mode this server selected for the viewer. Legacy reliable and unnegotiated until a capability is
    /// answered. Mode 1 with its epoch from the moment the offer is handed to transport.</summary>
    internal ReplicationSelection Selection { get; private set; }

    /// <summary>True while the host should keep serving legacy state to the viewer: before a capability is answered and
    /// after a mode 0 offer.</summary>
    internal bool ServesLegacy => phase is Phase.Unnegotiated or Phase.Fallback;

    /// <summary>True once the viewer accepted the exact offered epoch.</summary>
    internal bool Accepted => phase == Phase.Accepted;

    /// <summary>The packet cap selected for the current epoch, 0 before a mode 1 offer.</summary>
    internal int PacketCap { get; private set; }

    /// <summary>A typed failure the host must end the session with, or null.</summary>
    internal ReplicationFailure? Failure { get; private set; }

    /// <summary>True when <paramref name="kind"/> arrived on the channel it is specified for. Acceptance and repair
    /// travel reliably. An acknowledgement may use either channel.</summary>
    internal static bool IsRequiredChannel(RebuildControlKind kind, NetChannelReliability reliability) =>
        kind == RebuildControlKind.Acknowledge || reliability == NetChannelReliability.ReliableOrdered;

    /// <summary>Answers the viewer's capability once. Without a writer the answer is mode 0 for server policy. With an
    /// unknown or infeasible transport limit it is mode 0 for the transport limit. Otherwise a fresh epoch and the
    /// selected limits go out as a reliable mode 1 offer. A repeated capability is ignored. The selection changes
    /// only when the offer was handed to transport.</summary>
    /// <param name="replicationTick">The host's current replication tick, which starts the negotiation deadline.</param>
    internal void OnRebuildCapability(long replicationTick)
    {
        if (phase != Phase.Unnegotiated || Failure is not null) return;
        if (writer is null)
        {
            SendFallback(ReplicationSelectionReason.DisabledServerPolicy);
            return;
        }
        // Past the null-writer return: the constructor required options and limits with a writer.
        int unreliableLimit = net.MaxUnfragmentedPayloadBytes(slot, NetChannelReliability.UnreliableSequenced);
        int reliableLimit = net.MaxUnfragmentedPayloadBytes(slot, NetChannelReliability.ReliableOrdered);
        if (!options!.TrySelectPacketCap(unreliableLimit, reliableLimit, out int packetCap))
        {
            SendFallback(ReplicationSelectionReason.UnavailableTransportLimit);
            return;
        }
        // The epoch is spent before the send, so a refused send burns it. Epochs are never reused either way.
        if (!epochs.TryNext(out ulong epoch))
        {
            // No epoch is left for this server lifetime. The session restarts and admission stays closed.
            Failure = new ReplicationFailure(DisconnectReason.ReplicationRestart, "epoch allocator exhausted");
            return;
        }
        ReplicationModeOffer offer = RebuildProtocol.SelectedOffer(limits!, packetCap, options.MaxChunksPerTick, epoch);
        if (!net.TrySendTo(slot, RebuildProtocol.EncodeModeOffer(offer), NetChannelReliability.ReliableOrdered))
            return;
        phase = Phase.Offered;
        PacketCap = packetCap;
        offeredTick = replicationTick;
        Selection = RebuildProtocol.SelectionOf(offer);
    }

    /// <summary>Applies one valid client control that arrived on its required channel. An acceptance counts only for
    /// the exact offered epoch while the offer is open, and starts the writer's format 2 slot for that epoch. An
    /// acknowledgement of the accepted epoch goes to the writer, which ignores any id it never committed. Anything
    /// else changes nothing.</summary>
    /// <param name="control">The decoded control.</param>
    /// <param name="reliability">The channel it arrived on.</param>
    /// <param name="replicationTick">The host's current replication tick.</param>
    internal void HandleControl(in RebuildClientControl control, NetChannelReliability reliability,
        long replicationTick)
    {
        if (!IsRequiredChannel(control.Kind, reliability)) return;
        switch (control.Kind)
        {
            // Offered or later is reachable only through a mode 1 offer, which requires a writer, options and limits.
            case RebuildControlKind.Accept when phase == Phase.Offered && control.Epoch == Selection.Epoch:
                writer!.StartRebuild(slot, control.Epoch, limits!);
                phase = Phase.Accepted;
                break;
            case RebuildControlKind.Acknowledge when phase == Phase.Accepted && control.Epoch == Selection.Epoch:
                writer!.AcknowledgeRebuild(slot, new ReplicationPacketId(control.Epoch, control.SnapshotSequence));
                break;
        }
    }

    /// <summary>The per-tick serve step for a viewer whose legacy serving has stopped. Sends no state until the
    /// keyframe barrier runs. Records <see cref="DisconnectReason.ReplicationRecoveryFailed"/> once the negotiation
    /// has been open for <see cref="ReplicationStreamOptions.RecoveryDeadlineTicks"/> replication ticks.</summary>
    /// <param name="world">The world the viewer is served from this tick.</param>
    /// <param name="interest">The viewer's interest, already filtered for visibility.</param>
    /// <param name="ownerNetId">The viewer's own player net id.</param>
    /// <param name="movementAck">The viewer's latest movement acknowledgement.</param>
    /// <param name="replicationTick">The host's current replication tick.</param>
    internal void Serve(World world, IReadOnlySet<long> interest, long ownerNetId, int movementAck,
        long replicationTick)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(interest);
        if (ServesLegacy || Failure is not null) return;
        // Not serving legacy means a mode 1 offer went out, which requires a writer and options.
        if (replicationTick - offeredTick >= options!.RecoveryDeadlineTicks)
            Failure = new ReplicationFailure(DisconnectReason.ReplicationRecoveryFailed, "negotiation deadline");
    }

    /// <summary>Drops the stream's negotiation state. The host forgets the writer slot itself.</summary>
    internal void Forget()
    {
        phase = Phase.Unnegotiated;
        Selection = default;
        PacketCap = 0;
        offeredTick = 0;
        Failure = null;
    }

    private void SendFallback(ReplicationSelectionReason reason)
    {
        ReplicationModeOffer offer = RebuildProtocol.FallbackOffer(reason);
        if (!net.TrySendTo(slot, RebuildProtocol.EncodeModeOffer(offer), NetChannelReliability.ReliableOrdered))
            return;
        phase = Phase.Fallback;
        Selection = RebuildProtocol.SelectionOf(offer);
    }
}
