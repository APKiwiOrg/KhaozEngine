using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Format 2 wire layouts, pinned against bytes written by hand from the spec table rather than from the encoder, plus
/// the length-first control demux: every 18-byte payload stays a MOVE and every recognized non-18 control is claimed.
/// </summary>
public class RebuildProtocolTests
{
    private static ReplicationModeOffer SelectedOffer(ulong epoch = 0x0102030405060708UL) =>
        new(2, ReplicationDeliveryMode.AcknowledgedUnreliable, 0, HistoryCount: 32, HistoryPayloadBytes: 2097152,
            MaxKeyframeBytes: 65536, EntityLimit: 1024, ComponentLimit: 16384, PacketCap: 512, ChunksPerTick: 4,
            Epoch: epoch);

    // Hand layout of SelectedOffer(): kind 6, format 2, mode 1, reason 0, then little-endian fields in spec order.
    private static readonly byte[] SelectedOfferGolden =
    {
        6, 2, 1, 0,
        32, 0,
        0x00, 0x00, 0x20, 0x00,
        0x00, 0x00, 0x01, 0x00,
        0x00, 0x04, 0x00, 0x00,
        0x00, 0x40, 0x00, 0x00,
        0x00, 0x02, 0x00, 0x00,
        4, 0,
        0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
    };

    private static byte[] Body(byte[] frame) => frame.AsSpan(1).ToArray();

    private static byte[] Append(byte[] source, params byte[] tail)
    {
        var result = new byte[source.Length + tail.Length];
        source.CopyTo(result, 0);
        tail.CopyTo(result, source.Length);
        return result;
    }

    [Fact]
    public void WireGenerationStaysThirteenAndControlSizesMatchTheSpec()
    {
        Assert.Equal(13, MoveProtocol.WireProtocolVersion);
        Assert.Equal(11, RebuildProtocol.EncodeAcceptance(1).Length);
        Assert.Equal(14, RebuildProtocol.EncodeAck(new(1, 1)).Length);
        Assert.Equal(19, RebuildProtocol.EncodeRepair(1, 1, 0).Length);
        Assert.Equal(36, RebuildProtocol.EncodeModeOffer(SelectedOffer()).Length);
    }

    [Fact]
    public void ReservedDiscriminatorsHaveTheirPlannedValues()
    {
        Assert.Equal(3, (byte)MoveProtocol.ClientControlKind.RebuildDeltaCapable);
        Assert.Equal(4, (byte)MoveProtocol.ServerFrameKind.RebuildDelta);
        Assert.Equal(5, (byte)MoveProtocol.ServerFrameKind.RebuildKeyframeChunk);
        Assert.Equal(6, (byte)MoveProtocol.ServerFrameKind.ReplicationMode);
        Assert.Equal(0xA1, (byte)RebuildControlKind.Accept);
        Assert.Equal(0xA2, (byte)RebuildControlKind.Acknowledge);
        Assert.Equal(0xA3, (byte)RebuildControlKind.Repair);
    }

    [Fact]
    public void SelectedModeOfferMatchesHandLayoutAndRoundTrips()
    {
        byte[] frame = RebuildProtocol.EncodeModeOffer(SelectedOffer());

        Assert.Equal(SelectedOfferGolden, frame);
        Assert.True(RebuildProtocol.TryDecodeModeOffer(Body(frame), out ReplicationModeOffer decoded));
        Assert.Equal(SelectedOffer(), decoded);
        Assert.Equal(new ReplicationSelection(ReplicationDeliveryMode.AcknowledgedUnreliable,
            ReplicationSelectionReason.Selected, 0x0102030405060708UL), RebuildProtocol.SelectionOf(decoded));
    }

    [Theory]
    [InlineData(ReplicationSelectionReason.UnavailableTransportLimit, 1)]
    [InlineData(ReplicationSelectionReason.DisabledServerPolicy, 2)]
    public void ModeZeroOfferCarriesZeroEpochAndLimits(ReplicationSelectionReason reason, byte wire)
    {
        byte[] expected = new byte[36];
        expected[0] = 6;
        expected[1] = 2;
        expected[3] = wire;

        ReplicationModeOffer offer = RebuildProtocol.FallbackOffer(reason);
        byte[] frame = RebuildProtocol.EncodeModeOffer(offer);

        Assert.Equal(expected, frame);
        Assert.True(RebuildProtocol.TryDecodeModeOffer(Body(frame), out ReplicationModeOffer decoded));
        Assert.Equal(ReplicationDeliveryMode.LegacyReliable, decoded.Mode);
        Assert.Equal(new ReplicationSelection(ReplicationDeliveryMode.LegacyReliable, reason, 0),
            RebuildProtocol.SelectionOf(decoded));
    }

    [Fact]
    public void WireReasonsMapExplicitlyAndUnnegotiatedNeverTravels()
    {
        Assert.Equal(0, RebuildProtocol.WireReason(ReplicationSelectionReason.Selected));
        Assert.Equal(1, RebuildProtocol.WireReason(ReplicationSelectionReason.UnavailableTransportLimit));
        Assert.Equal(2, RebuildProtocol.WireReason(ReplicationSelectionReason.DisabledServerPolicy));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RebuildProtocol.WireReason(ReplicationSelectionReason.Unnegotiated));
        Assert.True(RebuildProtocol.TryReadWireReason(0, out var selected));
        Assert.Equal(ReplicationSelectionReason.Selected, selected);
        Assert.True(RebuildProtocol.TryReadWireReason(1, out var limit));
        Assert.Equal(ReplicationSelectionReason.UnavailableTransportLimit, limit);
        Assert.True(RebuildProtocol.TryReadWireReason(2, out var policy));
        Assert.Equal(ReplicationSelectionReason.DisabledServerPolicy, policy);
        Assert.False(RebuildProtocol.TryReadWireReason(3, out _));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RebuildProtocol.FallbackOffer(ReplicationSelectionReason.Selected));
    }

    public static IEnumerable<object[]> BadOfferBodies()
    {
        byte[] good = Body(SelectedOfferGolden);
        byte[] Patch(Action<byte[]> edit)
        {
            byte[] copy = (byte[])good.Clone();
            edit(copy);
            return copy;
        }
        yield return new object[] { "short", good.AsSpan(0, 34).ToArray() };
        yield return new object[] { "trailing", Append(good, 0) };
        yield return new object[] { "format 1", Patch(b => b[0] = 1) };
        yield return new object[] { "format 3", Patch(b => b[0] = 3) };
        yield return new object[] { "mode 2", Patch(b => b[1] = 2) };
        yield return new object[] { "reason 3", Patch(b => b[2] = 3) };
        yield return new object[] { "mode 1 reason 1", Patch(b => b[2] = 1) };
        yield return new object[] { "zero v2 epoch", Patch(b => b.AsSpan(27, 8).Clear()) };
        yield return new object[] { "history 3", Patch(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3), 3)) };
        yield return new object[] { "history bytes overflow",
            Patch(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5), 0x80000000u)) };
        yield return new object[] { "keyframe overflow",
            Patch(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(9), uint.MaxValue)) };
        yield return new object[] { "keyframe not above envelope",
            Patch(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(9), 12)) };
        yield return new object[] { "zero entities", Patch(b => b.AsSpan(13, 4).Clear()) };
        yield return new object[] { "component overflow",
            Patch(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(17), 0x80000000u)) };
        yield return new object[] { "infeasible cap 280",
            Patch(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(21), 280)) };
        yield return new object[] { "cap overflow",
            Patch(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(21), uint.MaxValue)) };
        yield return new object[] { "zero chunks per tick", Patch(b => b.AsSpan(25, 2).Clear()) };
        byte[] modeZero = new byte[35];
        modeZero[0] = 2;
        modeZero[2] = 1;
        yield return new object[] { "mode 0 reason 0", Patch(b => { modeZero.CopyTo(b, 0); b[2] = 0; }) };
        yield return new object[] { "mode 0 nonzero epoch", Patch(b => { modeZero.CopyTo(b, 0); b[27] = 1; }) };
        yield return new object[] { "mode 0 nonzero limit", Patch(b => { modeZero.CopyTo(b, 0); b[21] = 1; }) };
    }

    [Theory]
    [MemberData(nameof(BadOfferBodies))]
    public void InvalidModeOfferIsRejected(string name, byte[] body)
    {
        Assert.False(RebuildProtocol.TryDecodeModeOffer(body, out _), name);
    }

    [Fact]
    public void ControlsMatchHandLayouts()
    {
        Assert.Equal(new byte[] { 0xC5, 0xA1, 2, 8, 7, 6, 5, 4, 3, 2, 1 },
            RebuildProtocol.EncodeAcceptance(0x0102030405060708UL));
        Assert.Equal(new byte[] { 0xC5, 0xA2, 8, 7, 6, 5, 4, 3, 2, 1, 0x44, 0x33, 0x22, 0x11 },
            RebuildProtocol.EncodeAck(new(0x0102030405060708UL, 0x11223344u)));
        Assert.Equal(new byte[] { 0xC5, 0xA3, 2, 8, 7, 6, 5, 4, 3, 2, 1, 0x44, 0x33, 0x22, 0x11, 0xDD, 0xCC, 0xBB, 0xAA },
            RebuildProtocol.EncodeRepair(0x0102030405060708UL, 0x11223344u, 0xAABBCCDDu));
    }

    [Fact]
    public void ValidControlsDecodeToExactlyOneFamily()
    {
        Assert.Equal(ControlReadResult.Valid, RebuildProtocol.DecodeClientControl(
            RebuildProtocol.EncodeAcceptance(9), out RebuildClientControl accept));
        Assert.Equal(new RebuildClientControl(RebuildControlKind.Accept, 9, 0, 0), accept);
        Assert.Equal(ControlReadResult.Valid, RebuildProtocol.DecodeClientControl(
            RebuildProtocol.EncodeAck(new(9, 77)), out RebuildClientControl ack));
        Assert.Equal(new RebuildClientControl(RebuildControlKind.Acknowledge, 9, 77, 0), ack);
        Assert.Equal(ControlReadResult.Valid, RebuildProtocol.DecodeClientControl(
            RebuildProtocol.EncodeRepair(9, 76, 70), out RebuildClientControl repair));
        Assert.Equal(new RebuildClientControl(RebuildControlKind.Repair, 9, 76, 70), repair);

        foreach (byte[] control in new[]
        {
            RebuildProtocol.EncodeAcceptance(9), RebuildProtocol.EncodeAck(new(9, 77)),
            RebuildProtocol.EncodeRepair(9, 76, 70),
        })
        {
            Assert.False(MoveProtocol.TryDecodeReplicationAck(control, out _));
            Assert.False(MoveProtocol.TryDecodeClientControl(control, out _));
            Assert.False(MoveProtocol.TryDecodeGameMessage(control, out _, out _));
        }

        // TryDecodeMove accepts any length of 18 or more, so a 19-byte repair can read as a move. Hosts therefore
        // run DecodeClientControl on every non-18 length before the MOVE fallback.
        Assert.True(MoveProtocol.TryDecodeMove(RebuildProtocol.EncodeRepair(9, 76, 70), out _, out _));
    }

    [Fact]
    public void LegacyFramesStayUnclaimed()
    {
        var move = new MoveCommand(new System.Numerics.Vector2(0.5f, -0.25f), false, 1.5f, false);
        Assert.Equal(ControlReadResult.Unclaimed, RebuildProtocol.DecodeClientControl(
            MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.DeltaCapable), out _));
        Assert.Equal(ControlReadResult.Unclaimed, RebuildProtocol.DecodeClientControl(
            MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.RebuildDeltaCapable), out _));
        Assert.Equal(ControlReadResult.Unclaimed, RebuildProtocol.DecodeClientControl(
            MoveProtocol.EncodeReplicationAck(5), out _));
        Assert.Equal(ControlReadResult.Unclaimed, RebuildProtocol.DecodeClientControl(
            MoveProtocol.EncodeGameMessage(7, new byte[] { 1, 2, 3 }), out _));
        Assert.Equal(ControlReadResult.Unclaimed, RebuildProtocol.DecodeClientControl(
            MoveProtocol.EncodeMove(1, move), out _));
        Assert.Equal(ControlReadResult.Unclaimed, RebuildProtocol.DecodeClientControl(ReadOnlySpan<byte>.Empty, out _));
        Assert.Equal(ControlReadResult.Unclaimed, RebuildProtocol.DecodeClientControl(new byte[] { 0xC5 }, out _));
    }

    public static IEnumerable<object[]> Length18Aliases()
    {
        var move = new MoveCommand(new System.Numerics.Vector2(0.75f, -0.5f), true, 2.25f, false);
        foreach (int seq in new[] { 0x0000A1C5, 0x0000A2C5, 0x0000A3C5 })
            yield return new object[] { $"move seq 0x{seq:X8}", MoveProtocol.EncodeMove(seq, move), true };

        // Sender-intended truncated controls: a control prefix whose remaining bytes still hold the same valid MOVE
        // fields at offsets 4, 8, 12, 13 and 17. Length 18 is a MOVE whatever the sender meant.
        foreach (byte marker in new byte[] { 0xA1, 0xA2, 0xA3 })
        {
            byte[] truncated = MoveProtocol.EncodeMove(0, move);
            truncated[0] = 0xC5;
            truncated[1] = marker;
            truncated[2] = 2;
            truncated[3] = 0;
            yield return new object[] { $"truncated {marker:X2}", truncated, true };

            byte[] nan = (byte[])truncated.Clone();
            BinaryPrimitives.WriteSingleLittleEndian(nan.AsSpan(4), float.NaN);
            yield return new object[] { $"truncated {marker:X2} NaN axis", nan, false };

            byte[] inf = (byte[])truncated.Clone();
            BinaryPrimitives.WriteSingleLittleEndian(inf.AsSpan(13), float.PositiveInfinity);
            yield return new object[] { $"truncated {marker:X2} Inf yaw", inf, false };
        }
    }

    [Theory]
    [MemberData(nameof(Length18Aliases))]
    public void EveryLength18ControlAliasIsUnclaimed(string name, byte[] move18, bool validMove)
    {
        Assert.Equal(18, move18.Length);
        Assert.Equal(ControlReadResult.Unclaimed, RebuildProtocol.DecodeClientControl(move18, out _));
        Assert.Equal(validMove, MoveProtocol.TryDecodeMove(move18, out int seq, out MoveCommand cmd));
        if (validMove)
        {
            Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(move18), seq);
            Assert.True(float.IsFinite(cmd.Move.X) && cmd.Move.X != 0f, name);
        }
    }

    public static IEnumerable<object[]> MalformedRecognizedControls()
    {
        byte[] accept = RebuildProtocol.EncodeAcceptance(5);
        byte[] ack = RebuildProtocol.EncodeAck(new(5, 6));
        byte[] repair = RebuildProtocol.EncodeRepair(5, 6, 4);
        byte[] With(byte[] source, int index, byte value)
        {
            byte[] copy = (byte[])source.Clone();
            copy[index] = value;
            return copy;
        }
        byte[] ZeroEpoch(byte[] source, int offset)
        {
            byte[] copy = (byte[])source.Clone();
            copy.AsSpan(offset, 8).Clear();
            return copy;
        }

        yield return new object[] { "accept 2 bytes", new byte[] { 0xC5, 0xA1 } };
        yield return new object[] { "accept 10 bytes", accept.AsSpan(0, 10).ToArray() };
        yield return new object[] { "accept 12 bytes", Append(accept, 0) };
        yield return new object[] { "accept 19 bytes", Append(accept, new byte[8]) };
        yield return new object[] { "accept format 1", With(accept, 2, 1) };
        yield return new object[] { "accept zero epoch", ZeroEpoch(accept, 3) };
        yield return new object[] { "ack 13 bytes", ack.AsSpan(0, 13).ToArray() };
        yield return new object[] { "ack overlong 15 bytes", Append(ack, 0) };
        yield return new object[] { "ack overlong 19 bytes", Append(ack, new byte[5]) };
        yield return new object[] { "ack zero epoch", ZeroEpoch(ack, 2) };
        yield return new object[] { "repair bad revision", With(repair, 2, 3) };
        yield return new object[] { "repair revision 0", With(repair, 2, 0) };
        yield return new object[] { "repair 17 bytes", repair.AsSpan(0, 17).ToArray() };
        yield return new object[] { "repair 20 bytes", Append(repair, 0) };
        yield return new object[] { "repair zero epoch", ZeroEpoch(repair, 3) };
    }

    [Theory]
    [MemberData(nameof(MalformedRecognizedControls))]
    public void RecognizedMalformedNon18ControlNeverFallsThrough(string name, byte[] control)
    {
        Assert.NotEqual(18, control.Length);
        Assert.True(ControlReadResult.Malformed == RebuildProtocol.DecodeClientControl(control, out _), name);
    }

    [Fact]
    public void BadRevisionRepairOf19BytesIsMalformed()
    {
        byte[] badRepair19 = RebuildProtocol.EncodeRepair(1, 1, 0);
        badRepair19[2] = 1;
        Assert.Equal(19, badRepair19.Length);
        Assert.Equal(ControlReadResult.Malformed, RebuildProtocol.DecodeClientControl(badRepair19, out _));
    }

    [Fact]
    public void EncodersRejectZeroEpoch()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RebuildProtocol.EncodeAcceptance(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RebuildProtocol.EncodeAck(new(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RebuildProtocol.EncodeRepair(0, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RebuildProtocol.EncodeKeyframeChunk(new(0, 1), 10, new byte[6]));
    }

    [Fact]
    public void DeltaFrameIsKindEnvelopeThenUnchangedBody()
    {
        byte[] body = { 2, 0, 9, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var packet = new ReplicationDeltaPacket(new(9, 3), new ReplicationPacketId(9, 2), false, body);

        byte[] frame = RebuildProtocol.EncodeDelta(0x0102030405060708L, 0x11223344, packet);

        byte[] expected = new byte[1 + 12 + body.Length];
        expected[0] = 4;
        new byte[] { 8, 7, 6, 5, 4, 3, 2, 1, 0x44, 0x33, 0x22, 0x11 }.CopyTo(expected, 1);
        body.CopyTo(expected, 13);
        Assert.Equal(expected, frame);
    }

    [Fact]
    public void KeyframeChunkIsKindIdentityTotalThenGenericChunk()
    {
        byte[] generic = { 0, 0, 3, 0, 2, 0xEE, 0xFF };

        byte[] frame = RebuildProtocol.EncodeKeyframeChunk(new(0x0102030405060708UL, 0xA0B0C0D0u), 1000, generic);

        byte[] expected =
        {
            5, 8, 7, 6, 5, 4, 3, 2, 1, 0xD0, 0xC0, 0xB0, 0xA0, 0xE8, 0x03, 0, 0,
            0, 0, 3, 0, 2, 0xEE, 0xFF,
        };
        Assert.Equal(expected, frame);
        Assert.Equal(23 - 1 - MessageFragmenter.HeaderBytes, RebuildProtocol.KeyframeChunkHeaderBytes);
    }

    [Fact]
    public void KeyframeChunkRejectsEmptyTotalAndHeaderlessChunk()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RebuildProtocol.EncodeKeyframeChunk(new(1, 1), 0, new byte[6]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RebuildProtocol.EncodeKeyframeChunk(new(1, 1), 10, new byte[4]));
    }

    [Fact]
    public void SelectedOfferCarriesValidatedLimits()
    {
        DeltaRebuildOptions limits = new ReplicationStreamOptions().ValidateForUnreliable(1f / 30f);

        ReplicationModeOffer offer = RebuildProtocol.SelectedOffer(limits, 512, 4, 0x0102030405060708UL);

        Assert.Equal(SelectedOffer(), offer);
        Assert.Throws<ArgumentException>(() => RebuildProtocol.SelectedOffer(limits, 280, 4, 7));
        Assert.Throws<ArgumentException>(() => RebuildProtocol.SelectedOffer(limits, 512, 4, 0));
    }
}
