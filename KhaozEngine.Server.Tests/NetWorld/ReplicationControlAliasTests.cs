using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Real-host routing of client payloads that share the 0xC5 marker family. Every 18-byte frame is a MOVE and reaches
/// the command store after ordinary validation, even when its first bytes read as a format 2 sub-marker. A format 2
/// control on any other length is claimed before the permissive MOVE fallback: valid controls act only on their
/// specified channel, malformed ones are flagged and change nothing. The rate limiter runs before all of it.
/// </summary>
public class ReplicationControlAliasTests
{
    private static readonly MoveCommand Walk = new(new Vector2(0.6f, 0.8f), run: false, cameraYaw: 0.25f);

    public static IEnumerable<object[]> AliasedSequences()
    {
        foreach (RebuildHostKind kind in new[] { RebuildHostKind.World, RebuildHostKind.Sharded })
            foreach (int seq in new[] { 0x0000A1C5, 0x0000A2C5, 0x0000A3C5 })
                yield return new object[] { kind, seq };
    }

    public static IEnumerable<object[]> Markers()
    {
        foreach (RebuildHostKind kind in new[] { RebuildHostKind.World, RebuildHostKind.Sharded })
            foreach (byte marker in new byte[] { 0xA1, 0xA2, 0xA3 })
                yield return new object[] { kind, marker };
    }

    public static IEnumerable<object[]> Hosts() => ReplicationCapabilityMatrixTests.Hosts();

    // A valid 19-byte repair whose bytes also decode as a finite MOVE with axis X = 1: epoch byte 0 sits at offset 3,
    // so epoch 0x3F_8000_0001 puts 1.0f at the MOVE axis offset 4 and leaves the other MOVE floats zero.
    private const ulong MoveShapedEpoch = 0x3F_8000_0001UL;

    private static byte[] MoveShapedRepair() => RebuildProtocol.EncodeRepair(MoveShapedEpoch, 0, 0);

    public static IEnumerable<object[]> MalformedControls()
    {
        byte[] accept = RebuildProtocol.EncodeAcceptance(1);
        byte[] ack = RebuildProtocol.EncodeAck(new(1, 1));
        byte[] repair = MoveShapedRepair();
        byte[] With(byte[] source, int index, byte value)
        {
            byte[] copy = (byte[])source.Clone();
            copy[index] = value;
            return copy;
        }
        byte[] Resize(byte[] source, int length)
        {
            byte[] copy = new byte[length];
            Array.Copy(source, copy, Math.Min(length, source.Length));
            return copy;
        }
        byte[] ZeroEpoch(byte[] source, int offset)
        {
            byte[] copy = (byte[])source.Clone();
            copy.AsSpan(offset, 8).Clear();
            return copy;
        }

        var cases = new (string Name, byte[] Bytes)[]
        {
            ("accept 2 bytes", new byte[] { 0xC5, 0xA1 }),
            ("accept 12 bytes", Resize(accept, 12)),
            ("accept bad format", With(accept, 2, 3)),
            ("accept zero epoch", ZeroEpoch(accept, 3)),
            ("ack 13 bytes", Resize(ack, 13)),
            ("ack 15 bytes", Resize(ack, 15)),
            ("ack zero epoch", ZeroEpoch(ack, 2)),
            ("repair bad revision 19 bytes", With(repair, 2, 3)),
            ("repair 20 bytes", Resize(repair, 20)),
            ("repair 25 bytes", Resize(repair, 25)),
            ("repair zero epoch", ZeroEpoch(repair, 3)),
        };
        foreach (RebuildHostKind kind in new[] { RebuildHostKind.World, RebuildHostKind.Sharded })
            foreach ((string name, byte[] bytes) in cases)
                yield return new object[] { kind, name, bytes };
    }

    private static (RebuildHost Host, RawRebuildClient Client) LegacySession(RebuildHostKind kind,
        AntiCheatConfig? antiCheat = null)
    {
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(kind, transport, allowUnreliable: true, antiCheat: antiCheat);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: false);
        host.Pump(3, client);
        Assert.True(client.Joined);
        Assert.NotEqual(int.MinValue, client.LastMovementAck);
        return (host, client);
    }

    private static (RebuildHost Host, RawRebuildClient Client, RebuildServerStream Stream, ulong Epoch) OfferedSession(
        RebuildHostKind kind, AntiCheatConfig? antiCheat = null)
    {
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(kind, transport, allowUnreliable: true, antiCheat: antiCheat);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);
        host.Pump(3, client);   // connect, join, then the capability is answered
        ulong epoch = Assert.Single(client.Offers).Epoch;
        Assert.True(host.TryGetRebuildStream(client.Slot, out RebuildServerStream stream));
        return (host, client, stream, epoch);
    }

    private static void Step(RebuildHost host, RawRebuildClient client)
    {
        host.Poll();
        host.Tick();
        client.Poll();
    }

    private static void AssertMoved(RebuildHost host, RawRebuildClient client, byte[] move, int seq)
    {
        Vector3 before = host.PositionOf(client.Slot);
        client.Send(move);
        Step(host, client);
        Assert.Equal(seq, client.LastMovementAck);
        Assert.True(Vector3.Distance(before, host.PositionOf(client.Slot)) > 0.01f, "the move was simulated");
        Assert.Equal(0, host.CountSuspicious(client.Slot, SuspiciousReason.MalformedPacket));
    }

    private static void AssertNoMove(RebuildHost host, RawRebuildClient client, byte[] frame, int malformedRaised,
        NetChannelReliability reliability = NetChannelReliability.ReliableOrdered)
    {
        Vector3 before = host.PositionOf(client.Slot);
        int ackBefore = client.LastMovementAck;
        int malformedBefore = host.CountSuspicious(client.Slot, SuspiciousReason.MalformedPacket);
        client.Send(frame, reliability);
        Step(host, client);
        Assert.Equal(ackBefore, client.LastMovementAck);
        Assert.Equal(before, host.PositionOf(client.Slot));
        Assert.Equal(malformedBefore + malformedRaised,
            host.CountSuspicious(client.Slot, SuspiciousReason.MalformedPacket));
    }

    [Fact]
    public void MoveFrameLengthMatchesTheMoveEncoder() =>
        Assert.Equal(RebuildServerStream.MoveFrameBytes, MoveProtocol.EncodeMove(0, Walk).Length);

    [Theory]
    [MemberData(nameof(AliasedSequences))]
    public void MoveSequenceAliasingAControlReachesTheCommandStore(RebuildHostKind kind, int seq)
    {
        (RebuildHost host, RawRebuildClient client) = LegacySession(kind);
        byte[] move = MoveProtocol.EncodeMove(seq, Walk);
        Assert.Equal(0xC5, move[0]);
        Assert.Equal((byte)(seq >> 8), move[1]);

        AssertMoved(host, client, move, seq);
    }

    [Theory]
    [MemberData(nameof(Markers))]
    public void TruncatedControlOfLength18IsAnOrdinaryMove(RebuildHostKind kind, byte marker)
    {
        (RebuildHost host, RawRebuildClient client) = LegacySession(kind);
        byte[] truncated = Truncated(marker);

        AssertMoved(host, client, truncated, BinaryPrimitives.ReadInt32LittleEndian(truncated));
    }

    [Theory]
    [MemberData(nameof(Markers))]
    public void TruncatedControlWithNonFiniteFieldsIsAMalformedMove(RebuildHostKind kind, byte marker)
    {
        (RebuildHost host, RawRebuildClient client) = LegacySession(kind);
        byte[] nan = Truncated(marker);
        BinaryPrimitives.WriteSingleLittleEndian(nan.AsSpan(4), float.NaN);
        byte[] inf = Truncated(marker);
        BinaryPrimitives.WriteSingleLittleEndian(inf.AsSpan(13), float.PositiveInfinity);

        AssertNoMove(host, client, nan, malformedRaised: 1);
        AssertNoMove(host, client, inf, malformedRaised: 1);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ValidRepairOf19BytesStoresNoMove(RebuildHostKind kind)
    {
        byte[] repair = MoveShapedRepair();
        Assert.Equal(19, repair.Length);
        Assert.True(MoveProtocol.TryDecodeMove(repair, out _, out MoveCommand asMove), "the bytes would pass as a MOVE");
        Assert.Equal(1f, asMove.Move.X);
        (RebuildHost host, RawRebuildClient client) = LegacySession(kind);

        AssertNoMove(host, client, repair, malformedRaised: 0);
    }

    [Theory]
    [MemberData(nameof(MalformedControls))]
    public void RecognizedMalformedControlStoresNoMoveAndIsFlagged(RebuildHostKind kind, string name, byte[] frame)
    {
        Assert.NotEqual(18, frame.Length);
        (RebuildHost host, RawRebuildClient client) = LegacySession(kind);

        AssertNoMove(host, client, frame, malformedRaised: 1);
        Assert.True(host.TryGetReplicationSelection(client.Slot, out ReplicationSelection selection), name);
        Assert.Equal(default, selection);
    }

    [Theory]
    [MemberData(nameof(MalformedControls))]
    public void RecognizedMalformedControlChangesNoNegotiationState(RebuildHostKind kind, string name, byte[] frame)
    {
        (RebuildHost host, RawRebuildClient client, RebuildServerStream stream, ulong epoch) = OfferedSession(kind);
        Assert.True(host.TryGetReplicationSelection(client.Slot, out ReplicationSelection offered));
        Vector3 before = host.PositionOf(client.Slot);

        client.Send(frame);
        Step(host, client);

        Assert.Equal(1, host.CountSuspicious(client.Slot, SuspiciousReason.MalformedPacket));
        Assert.True(host.TryGetReplicationSelection(client.Slot, out ReplicationSelection after), name);
        Assert.Equal(offered, after);
        Assert.False(stream.Accepted);
        Assert.Equal(before, host.PositionOf(client.Slot));
        Assert.Equal(0, client.LegacyFramesAfterFirstOffer);

        client.Send(RebuildProtocol.EncodeAcceptance(epoch));
        Step(host, client);
        Assert.True(stream.Accepted);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ValidControlsActOnlyOnTheirSpecifiedChannel(RebuildHostKind kind)
    {
        (RebuildHost host, RawRebuildClient client, RebuildServerStream stream, ulong epoch) = OfferedSession(kind);

        client.Send(RebuildProtocol.EncodeAcceptance(epoch), NetChannelReliability.UnreliableSequenced);
        client.Send(RebuildProtocol.EncodeRepair(epoch, 0, 0), NetChannelReliability.UnreliableSequenced);
        Step(host, client);
        Assert.False(stream.Accepted);
        Assert.Equal(2, host.CountSuspicious(client.Slot, SuspiciousReason.MalformedPacket));

        client.Send(RebuildProtocol.EncodeAcceptance(epoch));
        client.Send(RebuildProtocol.EncodeAck(new(epoch, 1)), NetChannelReliability.UnreliableSequenced);
        Step(host, client);
        Assert.True(stream.Accepted);
        Assert.Equal(2, host.CountSuspicious(client.Slot, SuspiciousReason.MalformedPacket));
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void RateLimiterRunsBeforeFormat2Controls(RebuildHostKind kind)
    {
        // A burst of two covers the join's two capability controls and nothing more.
        var antiCheat = new AntiCheatConfig { MaxMessagesPerSecond = 0.03f, MessageBurst = 2f };
        (RebuildHost host, RawRebuildClient client, RebuildServerStream stream, ulong epoch) =
            OfferedSession(kind, antiCheat);

        client.Send(RebuildProtocol.EncodeAcceptance(epoch));
        Step(host, client);

        Assert.False(stream.Accepted);
        Assert.Equal(1, host.CountSuspicious(client.Slot, SuspiciousReason.RateLimited));
        Assert.Equal(0, host.CountSuspicious(client.Slot, SuspiciousReason.MalformedPacket));
    }

    private static byte[] Truncated(byte marker)
    {
        byte[] truncated = MoveProtocol.EncodeMove(0, Walk);
        truncated[0] = 0xC5;
        truncated[1] = marker;
        truncated[2] = RebuildProtocol.Format;
        truncated[3] = 0;
        return truncated;
    }
}
