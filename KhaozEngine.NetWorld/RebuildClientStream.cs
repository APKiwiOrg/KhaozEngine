using System;
using System.Buffers.Binary;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>
/// One connection's format 2 receiver on a client: mode offer validation and acceptance, reliable keyframe chunk
/// assembly, reconstruction through <see cref="ClientDeltaRebuild"/>, routine and keyframe acknowledgements,
/// missing-baseline repair requests and the recovery deadline. It binds one <see cref="NetClient"/> and one view, so a
/// client builds a new stream wherever it builds a new connection and view.
/// </summary>
/// <remarks>
/// <para>Only a reliable mode offer or the first reliable chunk of a numerically newer epoch authorizes an epoch. A
/// datagram never does. A well-formed datagram flagged as a keyframe is ignored, because keyframe-ness comes from the
/// delivery path, while a wrong format byte or an unknown flag bit is malformed. A replacement offer on a live stream
/// adopts its new epoch and chunk width, retires older traffic and keeps the published world until the new keyframe
/// publishes.</para>
/// <para>Acknowledgements advertise <see cref="ClientDeltaRebuild.AckTarget"/>: one unreliable routine ack per cadence
/// tick, sent after the receive drain and repeated while idle, and one reliable ack for each accepted keyframe. While
/// the target is null, after a new epoch and before its keyframe, no routine ack or repair request is sent.</para>
/// <para>Negotiation, a new epoch and a missing baseline run the recovery deadline. Only an accepted projection ends
/// it. Stale, ignored and missing-baseline traffic never extends it. Expiry, capacity exhaustion and a refused offer
/// set <see cref="Failure"/>, which the client ends the session with.</para>
/// </remarks>
internal sealed class RebuildClientStream
{
    /// <summary>The smallest complete keyframe object: envelope, format 2 header and two empty counts. The complete
    /// empty state datagram without its session and kind bytes.</summary>
    internal const int MinKeyframeObjectBytes = ReplicationStreamOptions.MinStateDatagramBytes - 2;

    // Kind 5 after its kind byte: [epoch u64][sequence u32][total u32], then one generic fragment chunk.
    private const int ChunkHeaderBytes = RebuildProtocol.KeyframeChunkHeaderBytes - 1;
    private const int SessionFrameBytes = 1;
    private const byte KeyframeFlag = 1;
    private const int ModeOfferBodyBytes = RebuildProtocol.ModeOfferBytes - 1;

    private readonly NetClient net;
    private readonly ReplicationStreamOptions options;
    private readonly ReplicationCadence cadence;
    private MessageReassembler? reassembler;
    private ReplicationModeOffer offer;
    // The highest epoch this connection ever authorized. Survives Reset, so ExpectEpoch is only ever called with a
    // strictly greater one and can never throw.
    private ulong epochFloor;
    private bool assembling;
    private ReplicationPacketId assemblyId;
    private uint assemblyTotal;
    private bool allowance;
    private long? recoveryStart;
    private ReplicationPacketId? repairMissing;
    private long? lastRepairTick;

    /// <param name="net">The connection the stream sends its controls through.</param>
    /// <param name="registry">The view's registry.</param>
    /// <param name="view">The live presentation view accepted projections publish into.</param>
    /// <param name="options">The client's validated stream options.</param>
    /// <param name="tickSeconds">The client's configured tick, the cadence length for acks and deadlines.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal RebuildClientStream(NetClient net, ReplicationRegistry registry, ClientReplicationView view,
        ReplicationStreamOptions options, float tickSeconds)
    {
        this.net = net ?? throw new ArgumentNullException(nameof(net));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        Rebuild = new ClientDeltaRebuild(registry, view, options.StreamLimits());
        cadence = new ReplicationCadence(tickSeconds);
    }

    /// <summary>The connection's selection. Legacy reliable and unnegotiated until an offer is answered.</summary>
    internal ReplicationSelection Selection { get; private set; }

    /// <summary>A typed failure the client must end the session with, or null. Cleared by <see cref="Reset"/>.</summary>
    internal ReplicationFailure? Failure { get; private set; }

    /// <summary>The connection's receiver. Read by friend tests through the client's diagnostics.</summary>
    internal ClientDeltaRebuild Rebuild { get; }

    /// <summary>True once a mode 1 offer was accepted: only accepted projections then count as valid state, and
    /// legacy state frames are ignored.</summary>
    internal bool OwnsLiveness => Selection.Mode == ReplicationDeliveryMode.AcknowledgedUnreliable;

    /// <summary>True while negotiation, a new epoch or a missing baseline runs the recovery deadline.</summary>
    internal bool RecoveryActive => recoveryStart is not null;

    /// <summary>Friend-test diagnostic: the keyframe chunk width of the accepted offer, 0 before one.</summary>
    internal int ReassemblyWidth => reassembler?.ChunkPayloadBytes ?? 0;

    /// <summary>Friend-test diagnostic: a keyframe assembly is in progress.</summary>
    internal bool AssemblyPending => assembling;

    /// <summary>Friend-test diagnostic: accepted projections on this connection.</summary>
    internal int AcceptedCount { get; private set; }

    /// <summary>Friend-test diagnostic: the largest transport payload this stream handed over, session byte included.</summary>
    internal int MaxTransportPayloadBytesSent { get; private set; }

    /// <summary>Answers one mode offer body, the frame after its kind byte. An offer on the unreliable channel and an
    /// exact duplicate of the current offer are ignored. A mode 0 offer before any mode 1 sets the fallback selection.
    /// A mode 1 offer within the client's limits, with an epoch above every one this connection authorized, is adopted
    /// and accepted reliably, initial and replacement alike.</summary>
    /// <returns>False when the session must end: <see cref="Failure"/> names a policy refusal, otherwise
    /// <paramref name="error"/> names an incompatible decode.</returns>
    internal bool ReceiveOffer(ReadOnlySpan<byte> body, NetChannelReliability reliability, out string? error)
    {
        error = null;
        if (Failure is not null || reliability != NetChannelReliability.ReliableOrdered) return true;
        if (!RebuildProtocol.TryDecodeModeOffer(body, out ReplicationModeOffer candidate))
        {
            if (IsInfeasibleWidthOffer(body)) return Refuse("the offered chunk width cannot carry its keyframe");
            error = "Malformed format 2 replication mode offer.";
            return false;
        }
        if (OwnsLiveness)
        {
            if (candidate == offer && candidate.Epoch == Selection.Epoch) return true;
            if (candidate.Mode != ReplicationDeliveryMode.AcknowledgedUnreliable)
            {
                error = "A mode 0 offer arrived on a live format 2 stream.";
                return false;
            }
        }
        else if (candidate.Mode == ReplicationDeliveryMode.LegacyReliable)
        {
            Selection = RebuildProtocol.SelectionOf(candidate);
            return true;
        }
        if (candidate.Epoch <= epochFloor)
        {
            error = $"Offer epoch {candidate.Epoch} is not above authorized epoch {epochFloor}.";
            return false;
        }
        if (OutsideLimits(candidate) is string field) return Refuse($"the offered {field} exceeds this client's limit");
        Adopt(candidate);
        return true;
    }

    /// <summary>Offers one kind 4 frame, the frame after its kind byte. Ignored until a mode 1 offer was accepted.
    /// A wrong format byte or an unknown flag bit is malformed. A well-formed keyframe-flagged datagram and a half-range
    /// ambiguous sequence are ignored like stale traffic. A missing baseline schedules one coalesced repair request
    /// when an ack target exists.</summary>
    /// <returns>The reconstruction result. Only <see cref="DeltaRebuildResult.Accepted"/> sets the envelope outs.
    /// <see cref="DeltaRebuildResult.Invalid"/> ends the session: <see cref="Failure"/> names a capacity failure,
    /// otherwise <paramref name="error"/> names an incompatible decode.</returns>
    internal DeltaRebuildResult ReceiveDelta(World world, ReadOnlyMemory<byte> frame, NetChannelReliability reliability,
        out long localNetId, out int movementAck, out string? error)
    {
        localNetId = 0;
        movementAck = 0;
        error = null;
        if (Failure is not null || !OwnsLiveness) return DeltaRebuildResult.DuplicateOrStale;
        if (frame.Length < RebuildProtocol.EnvelopeBytes) return Malformed("A format 2 delta is shorter than its envelope.", out error);
        ReadOnlyMemory<byte> body = frame[RebuildProtocol.EnvelopeBytes..];
        if (body.Length > 1)
        {
            ReadOnlySpan<byte> head = body.Span;
            if (head[0] != RebuildProtocol.Format)
                return Malformed($"Unknown replication body format {head[0]}.", out error);
            if ((head[1] & ~KeyframeFlag) != 0)
                return Malformed($"Format 2 flags 0x{head[1]:X2} carry an unknown bit.", out error);
            if (head[1] == KeyframeFlag) return DeltaRebuildResult.DuplicateOrStale;   // keyframes arrive only as chunks
        }
        DeltaRebuildResult result = Classify(Rebuild.TryApply(world, body, out _, out ReplicationPacketId? missing,
            out error), error);
        if (result == DeltaRebuildResult.Accepted)
        {
            OnAccepted();
            ReadEnvelope(frame.Span, out localNetId, out movementAck);
        }
        else if (result == DeltaRebuildResult.MissingBaseline && Rebuild.AckTarget is not null)
        {
            repairMissing = missing;
            recoveryStart ??= cadence.Tick;
        }
        return result;
    }

    /// <summary>Offers one kind 5 frame, the frame after its kind byte. Ignored before an accepted offer, on the
    /// unreliable channel, and for a retired or already established epoch. Every other chunk is validated against the
    /// accepted width and keyframe limit before any byte is copied: stream id 0, low 16 sequence bits, a total within
    /// limits, the chunk count that total needs, and continuity with the assembly in progress. The first chunk of a
    /// numerically newer epoch authorizes it and discards the partial assembly. A partial assembly returns
    /// <see cref="DeltaRebuildResult.DuplicateOrStale"/>. A complete object must match its declared total exactly and
    /// carry a keyframe body with the chunk's own id. An accepted keyframe is acknowledged reliably at once.</summary>
    /// <returns>As <see cref="ReceiveDelta"/>.</returns>
    internal DeltaRebuildResult ReceiveChunk(World world, ReadOnlyMemory<byte> frame, NetChannelReliability reliability,
        out long localNetId, out int movementAck, out string? error)
    {
        localNetId = 0;
        movementAck = 0;
        error = null;
        if (Failure is not null || reassembler is null || reliability != NetChannelReliability.ReliableOrdered)
            return DeltaRebuildResult.DuplicateOrStale;
        ReadOnlySpan<byte> span = frame.Span;
        if (span.Length < ChunkHeaderBytes + MessageFragmenter.HeaderBytes)
            return Malformed("A keyframe chunk is shorter than its headers.", out error);
        var id = new ReplicationPacketId(BinaryPrimitives.ReadUInt64LittleEndian(span),
            BinaryPrimitives.ReadUInt32LittleEndian(span[8..]));
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        if (id.Epoch == 0) return Malformed("A keyframe chunk names epoch zero.", out error);
        if (id.Epoch < Selection.Epoch || Rebuild.LatestAcceptedId?.Epoch == id.Epoch)
            return DeltaRebuildResult.DuplicateOrStale;
        if (ChunkHeaderError(span[ChunkHeaderBytes..], id, total) is string headerError)
            return Malformed(headerError, out error);
        if (id.Epoch > Selection.Epoch) AdvanceEpoch(id.Epoch);
        if (span[ChunkHeaderBytes + 3] == 0)
        {
            reassembler.DropConnection(0);
            assembling = true;
            assemblyId = id;
            assemblyTotal = total;
        }
        if (!reassembler.TryComplete(span[ChunkHeaderBytes..], out ReadOnlyMemory<byte> assembled, out string? reason))
        {
            if (reason is null) return DeltaRebuildResult.DuplicateOrStale;
            assembling = false;
            return Malformed($"Keyframe chunk refused: {reason}.", out error);
        }
        assembling = false;
        if (assembled.Length != total)
            return Malformed($"Keyframe object holds {assembled.Length} bytes, declared {total}.", out error);
        return ApplyKeyframe(world, assembled, id, out localNetId, out movementAck, out error);
    }

    /// <summary>Adds elapsed client time to the cadence. A zero or negative step is a drain and consumes nothing.</summary>
    internal void Advance(float dt)
    {
        if (cadence.Advance(dt)) allowance = true;
    }

    /// <summary>The scheduled control work after a receive drain: the recovery deadline, at most one repair request
    /// per <see cref="ReplicationStreamOptions.RepairRequestIntervalTicks"/>, and one routine ack when this cadence tick
    /// granted an allowance and an ack target exists.</summary>
    internal void AfterReceiveDrain()
    {
        bool due = allowance;
        allowance = false;
        if (Failure is not null || !OwnsLiveness) return;
        long tick = cadence.Tick;
        if (recoveryStart is long start && tick - start >= options.RecoveryDeadlineTicks)
        {
            Failure = new ReplicationFailure(DisconnectReason.ReplicationRecoveryFailed,
                Rebuild.AckTarget is null ? "no keyframe before the recovery deadline" : "repair deadline");
            return;
        }
        if (repairMissing is ReplicationPacketId missing && Rebuild.AckTarget is ReplicationPacketId latest
            && (lastRepairTick is not long last || tick - last >= options.RepairRequestIntervalTicks))
        {
            Send(RebuildProtocol.EncodeRepair(latest.Epoch, latest.Sequence, missing.Sequence),
                NetChannelReliability.ReliableOrdered);
            lastRepairTick = tick;
        }
        if (due && Rebuild.AckTarget is ReplicationPacketId target)
            Send(RebuildProtocol.EncodeAck(target), NetChannelReliability.UnreliableSequenced);
    }

    /// <summary>Clears the assembly, retained projections, pins, ack target, deadline, repair state, selection and
    /// failure for a Joined edge on the same connection. The live world stays. The epoch floor survives, so no earlier
    /// stream can be revived.</summary>
    internal void Reset()
    {
        Rebuild.Reset();
        reassembler = null;
        assembling = false;
        offer = default;
        Selection = default;
        Failure = null;
        allowance = false;
        recoveryStart = null;
        repairMissing = null;
        lastRepairTick = null;
    }

    private DeltaRebuildResult ApplyKeyframe(World world, ReadOnlyMemory<byte> assembled, ReplicationPacketId id,
        out long localNetId, out int movementAck, out string? error)
    {
        localNetId = 0;
        movementAck = 0;
        ReadOnlyMemory<byte> body = assembled[RebuildProtocol.EnvelopeBytes..];
        ReadOnlySpan<byte> header = body.Span;
        var bodyId = new ReplicationPacketId(BinaryPrimitives.ReadUInt64LittleEndian(header[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[10..]));
        if ((header[1] & KeyframeFlag) == 0 || bodyId != id)
            return Malformed($"Keyframe chunks {id} carry a body for {bodyId} that is not its keyframe.", out error);
        DeltaRebuildResult result = Classify(Rebuild.TryApply(world, body, out ReplicationPacketId accepted, out _,
            out error), error);
        if (result != DeltaRebuildResult.Accepted) return result;
        OnAccepted();
        ReadEnvelope(assembled.Span, out localNetId, out movementAck);
        Send(RebuildProtocol.EncodeAck(accepted), NetChannelReliability.ReliableOrdered);
        return result;
    }

    // The generic chunk header checks that need no assembly state, plus continuity with the assembly in progress.
    private string? ChunkHeaderError(ReadOnlySpan<byte> chunk, ReplicationPacketId id, uint total)
    {
        int width = reassembler!.ChunkPayloadBytes;
        int index = chunk[3];
        int count = chunk[4];
        if (chunk[0] != RebuildServerStream.KeyframeStreamId) return $"Keyframe chunk names stream {chunk[0]}.";
        if (BinaryPrimitives.ReadUInt16LittleEndian(chunk[1..]) != unchecked((ushort)id.Sequence))
            return $"Keyframe chunk fragment sequence does not match sequence {id.Sequence}.";
        if (total < MinKeyframeObjectBytes || total > offer.MaxKeyframeBytes)
            return $"Keyframe total {total} is outside {MinKeyframeObjectBytes}..{offer.MaxKeyframeBytes}.";
        if (count != (int)((total + (uint)width - 1) / (uint)width))
            return $"Keyframe total {total} at width {width} cannot take {count} chunks.";
        bool restart = index == 0 && id.Epoch > Selection.Epoch;
        if (assembling && !restart && (index == 0 || id != assemblyId || total != assemblyTotal))
            return $"Keyframe chunk {id} does not continue assembly {assemblyId}.";
        if (!assembling && index != 0) return $"Keyframe chunk {id} index {index} has no assembly to continue.";
        return null;
    }

    private void Adopt(in ReplicationModeOffer accepted)
    {
        offer = accepted;
        reassembler = new MessageReassembler(0, ReplicationStreamOptions.ChunkWidth((int)accepted.PacketCap),
            (int)accepted.MaxKeyframeBytes, maxPartialAssemblies: 1);
        Selection = RebuildProtocol.SelectionOf(accepted);
        AdvanceEpoch(accepted.Epoch);
        Send(RebuildProtocol.EncodeAcceptance(accepted.Epoch), NetChannelReliability.ReliableOrdered);
    }

    // Authorizes a strictly greater epoch: older traffic retires, the partial assembly is discarded, and the published
    // world stays until the epoch's keyframe publishes.
    private void AdvanceEpoch(ulong epoch)
    {
        Rebuild.ExpectEpoch(epoch);
        epochFloor = epoch;
        Selection = Selection with { Epoch = epoch };
        reassembler!.DropConnection(0);
        assembling = false;
        repairMissing = null;
        recoveryStart ??= cadence.Tick;
    }

    private void OnAccepted()
    {
        AcceptedCount++;
        recoveryStart = null;
        repairMissing = null;
    }

    // The first limit an offer exceeds, or null. A client transport limit of 0 or less is unknown and bounds nothing.
    private string? OutsideLimits(in ReplicationModeOffer o)
    {
        DeltaRebuildOptions mine = options.Limits;
        if (o.HistoryCount > mine.MaxRetainedProjections) return "history count";
        if (o.HistoryPayloadBytes > (uint)mine.MaxRetainedPayloadBytes) return "history payload bytes";
        if (o.MaxKeyframeBytes > (uint)mine.MaxKeyframeBytes) return "keyframe bytes";
        if (o.EntityLimit > (uint)mine.MaxEntities) return "entity limit";
        if (o.ComponentLimit > (uint)mine.MaxComponents) return "component limit";
        if (o.PacketCap > (uint)options.MaxTransportPayloadBytes) return "packet cap";
        if (o.ChunksPerTick > options.MaxChunksPerTick) return "chunks per tick";
        if (!TransportCarries(o.PacketCap, NetChannelReliability.UnreliableSequenced)
            || !TransportCarries(o.PacketCap, NetChannelReliability.ReliableOrdered)) return "packet cap for this transport";
        return null;
    }

    private bool TransportCarries(uint packetCap, NetChannelReliability reliability)
    {
        int limit = net.MaxUnfragmentedPayloadBytes(reliability);
        return limit <= 0 || (uint)limit >= packetCap;
    }

    // A mode 1 offer whose only fault is a cap that cannot carry its keyframe in 255 chunks. The decoder rejects it as
    // malformed, but it is a limit the client refuses by policy.
    private static bool IsInfeasibleWidthOffer(ReadOnlySpan<byte> body)
    {
        if (body.Length != ModeOfferBodyBytes || body[0] != RebuildProtocol.Format
            || body[1] != (byte)ReplicationDeliveryMode.AcknowledgedUnreliable) return false;
        uint keyframe = BinaryPrimitives.ReadUInt32LittleEndian(body[9..]);
        uint cap = BinaryPrimitives.ReadUInt32LittleEndian(body[21..]);
        return keyframe is > RebuildProtocol.EnvelopeBytes and <= int.MaxValue && cap <= int.MaxValue
            && !ReplicationStreamOptions.IsFeasiblePacketCap((int)cap, (int)keyframe);
    }

    // Half-range ambiguity is ignored like stale traffic (D2.13). Capacity exhaustion is a typed failure.
    private DeltaRebuildResult Classify(DeltaRebuildResult result, string? error)
    {
        if (result != DeltaRebuildResult.Invalid) return result;
        if (Rebuild.LastFailure == DeltaRebuildFailure.SequenceAmbiguous) return DeltaRebuildResult.DuplicateOrStale;
        if (Rebuild.LastFailure == DeltaRebuildFailure.CapacityExceeded)
            Failure = new ReplicationFailure(DisconnectReason.ReplicationCapacityExceeded, error ?? "capacity exceeded");
        return result;
    }

    private bool Refuse(string detail)
    {
        Failure = new ReplicationFailure(DisconnectReason.ReplicationPolicyRefused, detail);
        return false;
    }

    private static DeltaRebuildResult Malformed(string message, out string? error)
    {
        error = message;
        return DeltaRebuildResult.Invalid;
    }

    private static void ReadEnvelope(ReadOnlySpan<byte> envelope, out long localNetId, out int movementAck)
    {
        localNetId = BinaryPrimitives.ReadInt64LittleEndian(envelope);
        movementAck = BinaryPrimitives.ReadInt32LittleEndian(envelope[sizeof(long)..]);
    }

    private void Send(byte[] frame, NetChannelReliability reliability)
    {
        if (net.TrySend(frame, reliability))
            MaxTransportPayloadBytesSent = Math.Max(MaxTransportPayloadBytesSent, SessionFrameBytes + frame.Length);
    }
}
