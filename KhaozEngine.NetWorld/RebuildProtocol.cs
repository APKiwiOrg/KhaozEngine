using System;
using System.Buffers.Binary;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Which format 2 client control a frame carries. The value is its sub-marker after the 0xC5 marker.</summary>
internal enum RebuildControlKind : byte
{
    /// <summary><c>[0xC5][0xA1][format 2][epoch:ulong]</c>, 11 bytes, reliable.</summary>
    Accept = 0xA1,

    /// <summary><c>[0xC5][0xA2][epoch:ulong][snapshotSeq:uint]</c>, 14 bytes. Routine acks are unreliable, a
    /// keyframe ack is reliable.</summary>
    Acknowledge = 0xA2,

    /// <summary><c>[0xC5][0xA3][format 2][epoch:ulong][lastAcceptedSeq:uint][missingBaselineSeq:uint]</c>, 19 bytes,
    /// reliable.</summary>
    Repair = 0xA3,
}

/// <summary>The outcome of <see cref="RebuildProtocol.DecodeClientControl"/>.</summary>
internal enum ControlReadResult
{
    /// <summary>Not a format 2 control. The frame continues through the legacy demux, MOVE included.</summary>
    Unclaimed,

    /// <summary>A well-formed format 2 control.</summary>
    Valid,

    /// <summary>A recognized format 2 sub-marker with a bad length, revision or field. Rejected, never passed on.</summary>
    Malformed,
}

/// <summary>A decoded format 2 client control. Fields a kind does not carry are zero.</summary>
internal readonly record struct RebuildClientControl(
    RebuildControlKind Kind,
    ulong Epoch,
    uint SnapshotSequence,
    uint MissingBaselineSequence);

/// <summary>
/// A format 2 mode offer as it travels in <see cref="MoveProtocol.ServerFrameKind.ReplicationMode"/>. <see cref="Reason"/>
/// is the wire reason byte: 0 selected, 1 unavailable transport limit, 2 disabled server policy. Mode 0 carries a zero
/// epoch and zero limits.
/// </summary>
internal readonly record struct ReplicationModeOffer(
    byte Format,
    ReplicationDeliveryMode Mode,
    byte Reason,
    ushort HistoryCount,
    uint HistoryPayloadBytes,
    uint MaxKeyframeBytes,
    uint EntityLimit,
    uint ComponentLimit,
    uint PacketCap,
    ushort ChunksPerTick,
    ulong Epoch);

/// <summary>
/// Format 2 wire encodings for negotiated unreliable delta replication. Wire generation stays
/// <see cref="MoveProtocol.WireProtocolVersion"/>: every frame here is gated on capability
/// <see cref="MoveProtocol.ClientControlKind.RebuildDeltaCapable"/>. All multibyte fields are little-endian. Returned
/// server frames carry their kind byte but not the session frame byte.
/// </summary>
/// <remarks>
/// Client control demux is length first. Every 18-byte frame is a MOVE and is never inspected here, even when its first
/// two bytes are a recognized sub-marker, because valid move sequences such as <c>0x0000A1C5</c> begin that way. On any
/// other length a recognized sub-marker claims the frame, and a bad length, revision or field makes it
/// <see cref="ControlReadResult.Malformed"/> rather than letting it fall through to the MOVE decode.
/// </remarks>
internal static class RebuildProtocol
{
    /// <summary>The format revision byte every format 2 frame and offer carries.</summary>
    internal const byte Format = 2;

    /// <summary>The local net id and movement ack envelope ahead of every format 2 body.</summary>
    internal const int EnvelopeBytes = 12;

    /// <summary>Accept control length.</summary>
    internal const int AcceptBytes = 11;

    /// <summary>Acknowledge control length.</summary>
    internal const int AckBytes = 14;

    /// <summary>Repair control length.</summary>
    internal const int RepairBytes = 19;

    /// <summary>Mode offer length, kind byte included.</summary>
    internal const int ModeOfferBytes = 36;

    /// <summary>Keyframe chunk header length, kind byte included, before the generic fragment chunk.</summary>
    internal const int KeyframeChunkHeaderBytes = 17;

    /// <summary>Wire reason byte for a selected mode.</summary>
    internal const byte WireReasonSelected = 0;

    /// <summary>Wire reason byte for an unknown or unusable transport limit.</summary>
    internal const byte WireReasonTransportLimit = 1;

    /// <summary>Wire reason byte for a server policy that does not allow format 2.</summary>
    internal const byte WireReasonServerPolicy = 2;

    // Matches MoveProtocol's private client control marker. The format 2 sub-markers are RebuildControlKind values.
    private const byte ClientControlMarker = 0xC5;
    private const int MoveBytes = 18;
    private const int ModeOfferBodyBytes = ModeOfferBytes - 1;

    /// <summary>The public reason a wire reason byte stands for. False for an unknown byte.</summary>
    internal static bool TryReadWireReason(byte wire, out ReplicationSelectionReason reason)
    {
        switch (wire)
        {
            case WireReasonSelected: reason = ReplicationSelectionReason.Selected; return true;
            case WireReasonTransportLimit: reason = ReplicationSelectionReason.UnavailableTransportLimit; return true;
            case WireReasonServerPolicy: reason = ReplicationSelectionReason.DisabledServerPolicy; return true;
            default: reason = ReplicationSelectionReason.Unnegotiated; return false;
        }
    }

    /// <summary>The wire byte for a reason an offer can carry. <see cref="ReplicationSelectionReason.Unnegotiated"/>
    /// never travels.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> has no wire byte.</exception>
    internal static byte WireReason(ReplicationSelectionReason reason) => reason switch
    {
        ReplicationSelectionReason.Selected => WireReasonSelected,
        ReplicationSelectionReason.UnavailableTransportLimit => WireReasonTransportLimit,
        ReplicationSelectionReason.DisabledServerPolicy => WireReasonServerPolicy,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unnegotiated has no wire reason."),
    };

    /// <summary>The selection a valid offer establishes.</summary>
    internal static ReplicationSelection SelectionOf(in ReplicationModeOffer offer)
    {
        if (!TryReadWireReason(offer.Reason, out ReplicationSelectionReason reason))
            throw new ArgumentException($"Unknown wire reason {offer.Reason}.", nameof(offer));
        return new ReplicationSelection(offer.Mode, reason, offer.Epoch);
    }

    /// <summary>A mode 0 offer with zero epoch and limits.</summary>
    /// <param name="reason"><see cref="ReplicationSelectionReason.UnavailableTransportLimit"/> or
    /// <see cref="ReplicationSelectionReason.DisabledServerPolicy"/>.</param>
    internal static ReplicationModeOffer FallbackOffer(ReplicationSelectionReason reason)
    {
        if (reason is not (ReplicationSelectionReason.UnavailableTransportLimit
            or ReplicationSelectionReason.DisabledServerPolicy))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "A mode 0 offer names a fallback reason.");
        return new ReplicationModeOffer(Format, ReplicationDeliveryMode.LegacyReliable, WireReason(reason),
            0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>A mode 1 offer carrying the server-selected limits.</summary>
    /// <param name="limits">Validated stream limits, envelope charge included.</param>
    /// <param name="packetCap">The selected transport payload cap.</param>
    /// <param name="chunksPerTick">Reliable keyframe chunks per cadence tick.</param>
    /// <param name="epoch">The nonzero epoch the offer establishes.</param>
    internal static ReplicationModeOffer SelectedOffer(DeltaRebuildOptions limits, int packetCap, int chunksPerTick,
        ulong epoch)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var offer = new ReplicationModeOffer(Format, ReplicationDeliveryMode.AcknowledgedUnreliable, WireReasonSelected,
            checked((ushort)limits.MaxRetainedProjections), checked((uint)limits.MaxRetainedPayloadBytes),
            checked((uint)limits.MaxKeyframeBytes), checked((uint)limits.MaxEntities),
            checked((uint)limits.MaxComponents), checked((uint)packetCap), checked((ushort)chunksPerTick), epoch);
        if (!IsValidOffer(offer)) throw new ArgumentException("The selected limits do not form a valid offer.");
        return offer;
    }

    /// <summary>Encodes <c>[kind 6][format][mode][reason][history:ushort][historyBytes:uint][keyframeBytes:uint]
    /// [entities:uint][components:uint][packetCap:uint][chunksPerTick:ushort][epoch:ulong]</c>, 36 bytes.</summary>
    /// <exception cref="ArgumentException">The offer would not decode.</exception>
    internal static byte[] EncodeModeOffer(in ReplicationModeOffer offer)
    {
        if (!IsValidOffer(offer)) throw new ArgumentException("The mode offer is not valid.", nameof(offer));
        var b = new byte[ModeOfferBytes];
        b[0] = (byte)MoveProtocol.ServerFrameKind.ReplicationMode;
        b[1] = offer.Format;
        b[2] = (byte)offer.Mode;
        b[3] = offer.Reason;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), offer.HistoryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6), offer.HistoryPayloadBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10), offer.MaxKeyframeBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(14), offer.EntityLimit);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(18), offer.ComponentLimit);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(22), offer.PacketCap);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), offer.ChunksPerTick);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(28), offer.Epoch);
        return b;
    }

    /// <summary>Decodes a mode offer body, the frame after its kind byte. False for a wrong length, an unknown format,
    /// mode or reason, a mode and reason that disagree, a mode 0 offer with any nonzero field, a mode 1 offer with a
    /// zero epoch, or limits that overflow or cannot run a stream.</summary>
    internal static bool TryDecodeModeOffer(ReadOnlySpan<byte> body, out ReplicationModeOffer offer)
    {
        offer = default;
        if (body.Length != ModeOfferBodyBytes) return false;
        var candidate = new ReplicationModeOffer(body[0], (ReplicationDeliveryMode)body[1], body[2],
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(3)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(5)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(9)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(13)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(17)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(21)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(25)),
            BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(27)));
        if (!IsValidOffer(candidate)) return false;
        offer = candidate;
        return true;
    }

    /// <summary>Encodes <c>[0xC5][0xA1][format 2][epoch:ulong]</c>, 11 bytes.</summary>
    internal static byte[] EncodeAcceptance(ulong epoch)
    {
        RequireEpoch(epoch);
        var b = new byte[AcceptBytes];
        b[0] = ClientControlMarker;
        b[1] = (byte)RebuildControlKind.Accept;
        b[2] = Format;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(3), epoch);
        return b;
    }

    /// <summary>Encodes <c>[0xC5][0xA2][epoch:ulong][snapshotSeq:uint]</c>, 14 bytes.</summary>
    internal static byte[] EncodeAck(ReplicationPacketId id)
    {
        RequireEpoch(id.Epoch);
        var b = new byte[AckBytes];
        b[0] = ClientControlMarker;
        b[1] = (byte)RebuildControlKind.Acknowledge;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(2), id.Epoch);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10), id.Sequence);
        return b;
    }

    /// <summary>Encodes <c>[0xC5][0xA3][format 2][epoch:ulong][lastAcceptedSeq:uint][missingBaselineSeq:uint]</c>,
    /// 19 bytes. The format byte keeps the repair off the 18-byte MOVE length.</summary>
    internal static byte[] EncodeRepair(ulong epoch, uint lastAccepted, uint missingBaseline)
    {
        RequireEpoch(epoch);
        var b = new byte[RepairBytes];
        b[0] = ClientControlMarker;
        b[1] = (byte)RebuildControlKind.Repair;
        b[2] = Format;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(3), epoch);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(11), lastAccepted);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(15), missingBaseline);
        return b;
    }

    /// <summary>Length-first tri-state decode of one client payload. Length 18 is always
    /// <see cref="ControlReadResult.Unclaimed"/> and no byte is read. On any other length a 0xC5 marker followed by a
    /// format 2 sub-marker claims the frame: it is <see cref="ControlReadResult.Valid"/> only at its exact length, with
    /// the format revision where it carries one, and with a nonzero epoch. Everything else on that path is
    /// <see cref="ControlReadResult.Malformed"/>. Legacy controls, acks and game messages are unclaimed.</summary>
    internal static ControlReadResult DecodeClientControl(ReadOnlySpan<byte> data, out RebuildClientControl control)
    {
        control = default;
        if (data.Length == MoveBytes) return ControlReadResult.Unclaimed;
        if (data.Length < 2 || data[0] != ClientControlMarker) return ControlReadResult.Unclaimed;
        return (RebuildControlKind)data[1] switch
        {
            RebuildControlKind.Accept => DecodeAccept(data, out control),
            RebuildControlKind.Acknowledge => DecodeAck(data, out control),
            RebuildControlKind.Repair => DecodeRepair(data, out control),
            _ => ControlReadResult.Unclaimed,
        };
    }

    private static ControlReadResult DecodeAccept(ReadOnlySpan<byte> data, out RebuildClientControl control)
    {
        control = default;
        if (data.Length != AcceptBytes || data[2] != Format) return ControlReadResult.Malformed;
        ulong epoch = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(3));
        if (epoch == 0) return ControlReadResult.Malformed;
        control = new RebuildClientControl(RebuildControlKind.Accept, epoch, 0, 0);
        return ControlReadResult.Valid;
    }

    private static ControlReadResult DecodeAck(ReadOnlySpan<byte> data, out RebuildClientControl control)
    {
        control = default;
        if (data.Length != AckBytes) return ControlReadResult.Malformed;
        ulong epoch = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(2));
        if (epoch == 0) return ControlReadResult.Malformed;
        control = new RebuildClientControl(RebuildControlKind.Acknowledge, epoch,
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(10)), 0);
        return ControlReadResult.Valid;
    }

    private static ControlReadResult DecodeRepair(ReadOnlySpan<byte> data, out RebuildClientControl control)
    {
        control = default;
        if (data.Length != RepairBytes || data[2] != Format) return ControlReadResult.Malformed;
        ulong epoch = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(3));
        if (epoch == 0) return ControlReadResult.Malformed;
        control = new RebuildClientControl(RebuildControlKind.Repair, epoch,
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(11)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(15)));
        return ControlReadResult.Valid;
    }

    /// <summary>Encodes <c>[kind 4][localNetId:long][movementAck:int][format 2 body]</c>. The body is the packet's own
    /// bytes, unchanged.</summary>
    internal static byte[] EncodeDelta(long localNetId, int movementAck, ReplicationDeltaPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ReadOnlySpan<byte> body = packet.Bytes.Span;
        var b = new byte[1 + EnvelopeBytes + body.Length];
        b[0] = (byte)MoveProtocol.ServerFrameKind.RebuildDelta;
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(1), localNetId);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(9), movementAck);
        body.CopyTo(b.AsSpan(1 + EnvelopeBytes));
        return b;
    }

    /// <summary>Encodes <c>[kind 5][epoch:ulong][snapshotSeq:uint][totalBytes:uint][generic fragment chunk]</c>. The
    /// chunk is one <see cref="MessageFragmenter"/> chunk, its five-byte header included.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A zero epoch, a zero total or a chunk shorter than its header.</exception>
    internal static byte[] EncodeKeyframeChunk(ReplicationPacketId id, uint totalBytes, ReadOnlySpan<byte> genericChunk)
    {
        RequireEpoch(id.Epoch);
        if (totalBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes), totalBytes, "A keyframe object is never empty.");
        if (genericChunk.Length < MessageFragmenter.HeaderBytes)
            throw new ArgumentOutOfRangeException(nameof(genericChunk), genericChunk.Length,
                "A generic fragment chunk carries its header.");
        var b = new byte[KeyframeChunkHeaderBytes + genericChunk.Length];
        b[0] = (byte)MoveProtocol.ServerFrameKind.RebuildKeyframeChunk;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(1), id.Epoch);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(9), id.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(13), totalBytes);
        genericChunk.CopyTo(b.AsSpan(KeyframeChunkHeaderBytes));
        return b;
    }

    private static bool IsValidOffer(in ReplicationModeOffer offer)
    {
        if (offer.Format != Format) return false;
        if (offer.Mode == ReplicationDeliveryMode.LegacyReliable)
        {
            return offer.Reason is WireReasonTransportLimit or WireReasonServerPolicy
                && offer.HistoryCount == 0 && offer.HistoryPayloadBytes == 0 && offer.MaxKeyframeBytes == 0
                && offer.EntityLimit == 0 && offer.ComponentLimit == 0 && offer.PacketCap == 0
                && offer.ChunksPerTick == 0 && offer.Epoch == 0;
        }
        if (offer.Mode != ReplicationDeliveryMode.AcknowledgedUnreliable) return false;
        return offer.Reason == WireReasonSelected
            && offer.Epoch != 0
            && offer.HistoryCount >= ReplicationStreamOptions.MinRetainedProjections
            && FitsPositiveInt(offer.HistoryPayloadBytes)
            && FitsPositiveInt(offer.MaxKeyframeBytes) && offer.MaxKeyframeBytes > EnvelopeBytes
            && FitsPositiveInt(offer.EntityLimit)
            && FitsPositiveInt(offer.ComponentLimit)
            && FitsPositiveInt(offer.PacketCap)
            && offer.ChunksPerTick >= 1
            && ReplicationStreamOptions.IsFeasiblePacketCap((int)offer.PacketCap, (int)offer.MaxKeyframeBytes);
    }

    private static bool FitsPositiveInt(uint value) => value is >= 1 and <= int.MaxValue;

    private static void RequireEpoch(ulong epoch)
    {
        if (epoch == 0) throw new ArgumentOutOfRangeException(nameof(epoch), epoch, "A format 2 epoch is nonzero.");
    }
}
