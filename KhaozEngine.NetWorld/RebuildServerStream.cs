using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>
/// One viewer's format 2 stream on a server: negotiation, the reliable keyframe barrier, steady unreliable serving,
/// limit changes, deadlines and send failures. It owns the selected limits, the frozen keyframe and its chunk cursor,
/// not movement or visibility policy. The host keeps both and passes the already filtered interest set in.
/// </summary>
/// <remarks>
/// <para>A mode 0 offer keeps the viewer on legacy reliable delivery. A mode 1 offer ends legacy serving the moment it
/// is handed to reliable transport, because legacy state queued before it already precedes it on that channel. Legacy
/// serving never resumes on that connection.</para>
/// <para>Every keyframe repair allocates a fresh epoch, starts the writer slot with it and freezes one complete
/// projection with its movement ack. The complete object is fragmented at the epoch's fixed width and sent reliably in
/// at most <see cref="ReplicationStreamOptions.MaxChunksPerTick"/> chunks per cadence tick. Routine deltas resume only
/// after the exact keyframe id was acknowledged on the reliable channel. A repair with unchanged limits establishes its
/// epoch through its chunks. A drop of the actual transport limit below the epoch's cap sends a new offer instead, and
/// its keyframe waits for that offer's acceptance.</para>
/// <para>Negotiation must complete, initial keyframe acknowledgement included, within
/// <see cref="ReplicationStreamOptions.RecoveryDeadlineTicks"/> replication ticks of the offer. A later repair gets
/// the same budget from its trigger. Expiry reports <see cref="DisconnectReason.ReplicationRecoveryFailed"/>.</para>
/// <para>A send is committed only after the transport took it. A refused send reports
/// <see cref="DisconnectReason.ReplicationRestart"/>. A thrown send is not caught: <see cref="Faulted"/> is set before
/// the exception leaves, the candidate stays uncommitted, and the next serve reports the restart.</para>
/// </remarks>
internal sealed class RebuildServerStream
{
    /// <summary>The MOVE frame length. Every client payload of this length takes the MOVE decode first.</summary>
    internal const int MoveFrameBytes = 18;

    /// <summary>The internal fragment stream every keyframe uses inside kind 5.</summary>
    internal const byte KeyframeStreamId = 0;

    // The session frame byte the transport payload carries ahead of every NetWorld frame.
    private const int SessionFrameBytes = 1;

    private readonly NetServer net;
    private readonly int slot;
    private readonly AoiDeltaReplicator? writer;
    private readonly ReplicationStreamOptions? options;
    private readonly DeltaRebuildOptions? limits;
    private readonly ReplicationEpochAllocator epochs;
    private Phase phase;
    private long? recoveryStartTick;
    // Only selects the recovery failure detail: the initial negotiation or a later repair.
    private bool negotiating;
    private bool repairRequested;
    private long? boundOwner;
    private ReplicationPacketId keyframeId;
    private uint keyframeTotalBytes;
    private byte[][]? keyframeChunks;
    private int chunkCursor;
    private bool keyframeCommitted;

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
        Barrier,
        Steady,
    }

    /// <summary>The mode this server selected for the viewer. Legacy reliable and unnegotiated until a capability is
    /// answered. Mode 1 with the current epoch from the moment the offer is handed to transport. A repair moves the
    /// epoch forward.</summary>
    internal ReplicationSelection Selection { get; private set; }

    /// <summary>True while the host should keep serving legacy state to the viewer: before a capability is answered and
    /// after a mode 0 offer.</summary>
    internal bool ServesLegacy => phase is Phase.Unnegotiated or Phase.Fallback;

    /// <summary>True once the viewer accepted the current offered epoch, through its keyframe barrier and steady
    /// serving. False again while a replacement offer waits for acceptance.</summary>
    internal bool Accepted => phase is Phase.Barrier or Phase.Steady;

    /// <summary>The packet cap selected for the current epoch, 0 before a mode 1 offer.</summary>
    internal int PacketCap { get; private set; }

    /// <summary>A typed failure the host must end the session with, or null.</summary>
    internal ReplicationFailure? Failure { get; private set; }

    /// <summary>Friend-test diagnostic: a send threw. The next serve reports a restart.</summary>
    internal bool Faulted { get; private set; }

    /// <summary>Friend-test diagnostic: keyframe chunks handed to transport in the current cadence tick.</summary>
    internal int ChunksSentThisCadence { get; private set; }

    /// <summary>Test seam: replaces <see cref="NetServer.TrySendTo"/>, so a test can refuse a send on a connection the
    /// session server still knows. Null sends through the session server.</summary>
    internal Func<byte[], NetChannelReliability, bool>? SendOverrideForTest { get; set; }

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
        if (phase != Phase.Unnegotiated || Failure is not null || Faulted) return;
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
        if (SendOffer(packetCap, replicationTick)) negotiating = true;
    }

    /// <summary>Applies one valid client control that arrived on its required channel. An acceptance counts only for
    /// the exact offered epoch while the offer is open, and starts the writer's format 2 slot and the keyframe barrier
    /// for that epoch. During the barrier only a reliable acknowledgement of the exact keyframe id, after every chunk
    /// was handed to transport, promotes it and resumes steady serving. In steady serving an acknowledgement of the
    /// current epoch goes to the writer, which ignores any id it never committed, and a repair request for the current
    /// epoch schedules one keyframe repair. Requests during a barrier are coalesced into it. Anything else changes
    /// nothing.</summary>
    /// <param name="control">The decoded control.</param>
    /// <param name="reliability">The channel it arrived on.</param>
    /// <param name="replicationTick">The host's current replication tick.</param>
    internal void HandleControl(in RebuildClientControl control, NetChannelReliability reliability,
        long replicationTick)
    {
        if (!IsRequiredChannel(control.Kind, reliability) || Failure is not null || Faulted) return;
        var id = new ReplicationPacketId(control.Epoch, control.SnapshotSequence);
        switch (control.Kind)
        {
            // Offered or later is reachable only through a mode 1 offer, which requires a writer, options and limits.
            case RebuildControlKind.Accept when phase == Phase.Offered && control.Epoch == Selection.Epoch:
                writer!.StartRebuild(slot, control.Epoch, limits!);
                EnterBarrier();
                break;
            case RebuildControlKind.Acknowledge when phase == Phase.Barrier:
                if (keyframeCommitted && id == keyframeId && reliability == NetChannelReliability.ReliableOrdered)
                    CompleteBarrier();
                break;
            case RebuildControlKind.Acknowledge when phase == Phase.Steady && control.Epoch == Selection.Epoch:
                writer!.AcknowledgeRebuild(slot, id);
                break;
            case RebuildControlKind.Repair when phase == Phase.Steady && control.Epoch == Selection.Epoch:
                repairRequested = true;
                break;
        }
    }

    /// <summary>The per-tick serve step for a viewer whose legacy serving has stopped. A faulted stream reports a
    /// restart and an expired negotiation or repair reports <see cref="DisconnectReason.ReplicationRecoveryFailed"/>
    /// on any call. State moves only on a call that carries a cadence allowance: the keyframe barrier sends its next
    /// chunks, and steady serving sends one routine delta of the current projection or starts a keyframe repair.</summary>
    /// <param name="world">The world the viewer is served from this tick.</param>
    /// <param name="interest">The viewer's interest, already filtered for visibility.</param>
    /// <param name="ownerNetId">The viewer's own player net id.</param>
    /// <param name="movementAck">The viewer's latest movement acknowledgement.</param>
    /// <param name="replicationTick">The host's current replication tick.</param>
    /// <param name="allowance">True when this host tick crossed a cadence boundary.</param>
    internal void Serve(World world, IReadOnlySet<long> interest, long ownerNetId, int movementAck,
        long replicationTick, bool allowance)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(interest);
        if (Faulted)
        {
            Failure ??= new ReplicationFailure(DisconnectReason.ReplicationRestart, "send fault");
            return;
        }
        if (ServesLegacy || Failure is not null) return;
        // Not serving legacy means a mode 1 offer went out, which requires a writer, options and limits.
        if (recoveryStartTick is long start && replicationTick - start >= options!.RecoveryDeadlineTicks)
        {
            Fail(DisconnectReason.ReplicationRecoveryFailed, negotiating ? "negotiation deadline" : "repair deadline");
            return;
        }
        if (!allowance) return;
        ChunksSentThisCadence = 0;
        if (phase == Phase.Barrier) ServeBarrier(world, interest, ownerNetId, movementAck, replicationTick);
        else if (phase == Phase.Steady) ServeSteady(world, interest, ownerNetId, movementAck, replicationTick);
    }

    /// <summary>Drops the stream's state. The host forgets the writer slot itself.</summary>
    internal void Forget()
    {
        phase = Phase.Unnegotiated;
        Selection = default;
        PacketCap = 0;
        recoveryStartTick = null;
        negotiating = false;
        Failure = null;
        Faulted = false;
        ChunksSentThisCadence = 0;
        boundOwner = null;
        EnterBarrierState();
    }

    private void ServeBarrier(World world, IReadOnlySet<long> interest, long ownerNetId, int movementAck, long tick)
    {
        // An owner change needs a fresh epoch, even for an authorized keyframe already in flight. Defensive: both
        // hosts bind one owner per session, and the steady branch below is the one a stream-level test drives.
        if (keyframeChunks is not null && ownerNetId != boundOwner) repairRequested = true;
        if (repairRequested && !BeginRepair(tick)) return;
        if (keyframeChunks is null && !Freeze(world, interest, ownerNetId, movementAck)) return;
        SendChunks(tick);
    }

    private void ServeSteady(World world, IReadOnlySet<long> interest, long ownerNetId, int movementAck, long tick)
    {
        // The limit rule first, so a repair and a feasible limit drop on one tick spend one epoch: the replacement
        // offer's keyframe serves both. Nothing below changes the limit before the routine send.
        if (!LimitHolds(tick)) return;
        if (ownerNetId != boundOwner || repairRequested || writer!.RebuildNeedsRepair(slot))
        {
            Repair(world, interest, ownerNetId, movementAck, tick);
            return;
        }
        ReplicationDeltaPacket packet;
        try
        {
            packet = writer.BuildRebuildFor(slot, world, interest, ownerNetId);
        }
        catch (DeltaRebuildException e) when (e.Failure == DeltaRebuildFailure.CapacityExceeded)
        {
            Fail(DisconnectReason.ReplicationCapacityExceeded, e.Message);
            return;
        }
        catch (DeltaRebuildException)
        {
            // Retention pressure or an ambiguous sequence: the writer slot is unchanged and a new epoch fixes both.
            Repair(world, interest, ownerNetId, movementAck, tick);
            return;
        }
        byte[] frame = RebuildProtocol.EncodeDelta(ownerNetId, movementAck, packet);
        if (SessionFrameBytes + frame.Length > PacketCap)
        {
            // Never fragment, truncate or trim an unreliable delta. The candidate is never recorded as sent.
            Repair(world, interest, ownerNetId, movementAck, tick);
            return;
        }
        if (!Send(frame, NetChannelReliability.UnreliableSequenced))
        {
            Fail(DisconnectReason.ReplicationRestart, "routine delta send refused");
            return;
        }
        writer.RecordRebuildSent(slot, packet.Id);
    }

    private void Repair(World world, IReadOnlySet<long> interest, long ownerNetId, int movementAck, long tick)
    {
        if (BeginRepair(tick) && Freeze(world, interest, ownerNetId, movementAck)) SendChunks(tick);
    }

    // A keyframe repair with unchanged limits: a fresh epoch, a restarted writer slot and a new barrier. The recovery
    // deadline starts at the first trigger and is not extended by a repair that restarts inside it.
    private bool BeginRepair(long tick)
    {
        if (!epochs.TryNext(out ulong epoch))
        {
            Fail(DisconnectReason.ReplicationRestart, "epoch allocator exhausted");
            return false;
        }
        recoveryStartTick ??= tick;
        writer!.StartRebuild(slot, epoch, limits!);
        Selection = Selection with { Epoch = epoch };
        EnterBarrier();
        return true;
    }

    // Freezes the current authorized projection and its movement ack as one complete keyframe object, fragmented at
    // the epoch's width with the low 16 sequence bits as the generic fragment sequence.
    private bool Freeze(World world, IReadOnlySet<long> interest, long ownerNetId, int movementAck)
    {
        ReplicationDeltaPacket packet;
        try
        {
            packet = writer!.BuildRebuildFor(slot, world, interest, ownerNetId, keyframe: true);
        }
        catch (DeltaRebuildException e) when (e.Failure == DeltaRebuildFailure.CapacityExceeded)
        {
            Fail(DisconnectReason.ReplicationCapacityExceeded, e.Message);
            return false;
        }
        catch (DeltaRebuildException)
        {
            // Retry with a fresh epoch at the next cadence tick, inside the running recovery deadline.
            repairRequested = true;
            return false;
        }
        ReadOnlySpan<byte> body = packet.Bytes.Span;
        var keyframe = new byte[RebuildProtocol.EnvelopeBytes + body.Length];
        BinaryPrimitives.WriteInt64LittleEndian(keyframe, ownerNetId);
        BinaryPrimitives.WriteInt32LittleEndian(keyframe.AsSpan(sizeof(long)), movementAck);
        body.CopyTo(keyframe.AsSpan(RebuildProtocol.EnvelopeBytes));
        keyframeChunks = MessageFragmenter.Fragment(KeyframeStreamId, unchecked((ushort)packet.Id.Sequence), keyframe,
            ReplicationStreamOptions.ChunkWidth(PacketCap));
        keyframeId = packet.Id;
        keyframeTotalBytes = (uint)keyframe.Length;
        boundOwner = ownerNetId;
        return true;
    }

    // Sends the next chunks within this cadence tick's budget. The keyframe is committed only after its last chunk was
    // handed to transport.
    private void SendChunks(long tick)
    {
        byte[][] chunks = keyframeChunks!;
        while (chunkCursor < chunks.Length && ChunksSentThisCadence < options!.MaxChunksPerTick)
        {
            if (!LimitHolds(tick)) return;
            byte[] frame = RebuildProtocol.EncodeKeyframeChunk(keyframeId, keyframeTotalBytes, chunks[chunkCursor]);
            if (!Send(frame, NetChannelReliability.ReliableOrdered))
            {
                Fail(DisconnectReason.ReplicationRestart, "keyframe chunk send refused");
                return;
            }
            chunkCursor++;
            ChunksSentThisCadence++;
        }
        if (chunkCursor == chunks.Length && !keyframeCommitted)
        {
            writer!.RecordRebuildSent(slot, keyframeId);
            keyframeCommitted = true;
        }
    }

    // The limit-change rule, applied before every state send. A limit at or above the epoch's cap keeps the epoch. A
    // lower feasible limit renegotiates through a new offer. An unknown or infeasible limit restarts the session,
    // because a receiver whose world was built by format 2 is never downgraded to legacy in place.
    private bool LimitHolds(long tick)
    {
        int unreliableLimit = net.MaxUnfragmentedPayloadBytes(slot, NetChannelReliability.UnreliableSequenced);
        int reliableLimit = net.MaxUnfragmentedPayloadBytes(slot, NetChannelReliability.ReliableOrdered);
        if (!options!.TrySelectPacketCap(unreliableLimit, reliableLimit, out int packetCap))
        {
            Fail(DisconnectReason.ReplicationRestart, "transport limit below the feasible minimum");
            return false;
        }
        if (packetCap >= PacketCap) return true;
        if (!SendOffer(packetCap, tick) && Failure is null)
            Fail(DisconnectReason.ReplicationRestart, "replacement mode offer send refused");
        return false;
    }

    // Allocates an epoch and hands a mode 1 offer for it to reliable transport. The epoch is spent before the send, so
    // a refused send burns it. Epochs are never reused either way.
    private bool SendOffer(int packetCap, long tick)
    {
        if (!epochs.TryNext(out ulong epoch))
        {
            // No epoch is left for this server lifetime. The session restarts and admission stays closed.
            Fail(DisconnectReason.ReplicationRestart, "epoch allocator exhausted");
            return false;
        }
        ReplicationModeOffer offer = RebuildProtocol.SelectedOffer(limits!, packetCap, options!.MaxChunksPerTick, epoch);
        if (!Send(RebuildProtocol.EncodeModeOffer(offer), NetChannelReliability.ReliableOrdered)) return false;
        recoveryStartTick ??= tick;
        phase = Phase.Offered;
        PacketCap = packetCap;
        Selection = RebuildProtocol.SelectionOf(offer);
        EnterBarrierState();
        return true;
    }

    private void SendFallback(ReplicationSelectionReason reason)
    {
        ReplicationModeOffer offer = RebuildProtocol.FallbackOffer(reason);
        if (!Send(RebuildProtocol.EncodeModeOffer(offer), NetChannelReliability.ReliableOrdered)) return;
        phase = Phase.Fallback;
        Selection = RebuildProtocol.SelectionOf(offer);
    }

    // Hands one frame to transport. A throw leaves the stream faulted before it propagates, uncaught.
    private bool Send(byte[] frame, NetChannelReliability reliability)
    {
        bool returned = false;
        try
        {
            bool handed = SendOverrideForTest?.Invoke(frame, reliability)
                ?? net.TrySendTo(slot, frame, reliability);
            returned = true;
            return handed;
        }
        finally
        {
            if (!returned) Faulted = true;
        }
    }

    private void CompleteBarrier()
    {
        writer!.AcknowledgeRebuild(slot, keyframeId);
        phase = Phase.Steady;
        recoveryStartTick = null;
        negotiating = false;
        EnterBarrierState();
    }

    private void EnterBarrier()
    {
        phase = Phase.Barrier;
        EnterBarrierState();
    }

    // Clears the frozen keyframe, its cursor and any coalesced repair request.
    private void EnterBarrierState()
    {
        keyframeChunks = null;
        keyframeId = default;
        keyframeTotalBytes = 0;
        chunkCursor = 0;
        keyframeCommitted = false;
        repairRequested = false;
    }

    private void Fail(DisconnectReason reason, string detail) => Failure ??= new ReplicationFailure(reason, detail);
}
