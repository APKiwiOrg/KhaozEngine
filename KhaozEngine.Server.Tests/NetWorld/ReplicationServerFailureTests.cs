using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>Where a send fault is injected.</summary>
public enum FaultPoint
{
    RoutineDelta,
    KeyframeChunk,
    RoutineDeltaThenLeft,
}

/// <summary>
/// Server-side format 2 failures on both hosts: a refused send and a thrown send restart the session, a projection
/// that can never fit ends it with the typed capacity failure, and a repair that does not complete ends it with the
/// typed recovery failure on the deadline tick.
/// </summary>
public class ReplicationServerFailureTests
{
    public static IEnumerable<object[]> Hosts() => ReplicationCapabilityMatrixTests.Hosts();

    [Theory]
    [InlineData(RebuildHostKind.World, false)]
    [InlineData(RebuildHostKind.World, true)]
    [InlineData(RebuildHostKind.Sharded, false)]
    [InlineData(RebuildHostKind.Sharded, true)]
    public void SendFalseDisconnectsWithRestart(RebuildHostKind kind, bool keyframeChunk)
    {
        V2Rig rig = V2Rig.Create(kind);
        rig.SpawnPad(4000, 0x55);
        V2Client client = rig.JoinSteady(keyframeTicks: 3);
        RebuildServerStream stream = rig.Stream(client);
        int slot = client.Slot;
        if (keyframeChunk)
        {
            client.AutoAckKeyframes = false;
            RigDelta last = client.Deltas[^1];
            client.RequestRepair(last.Id.Epoch, last.Id.Sequence, 0);
            Assert.Equal(4, rig.Step().Count(s => s.IsChunk));
        }
        var attempts = new List<(byte Kind, NetChannelReliability Reliability, ReplicationPacketId? Candidate)>();
        stream.SendOverrideForTest = (frame, reliability) =>
        {
            attempts.Add((frame[0], reliability, rig.Writer.RebuildUsageForTest(slot).CandidateId));
            return false;
        };

        List<RigSend> sends = rig.Step();

        (byte kindByte, NetChannelReliability channel, ReplicationPacketId? candidate) = Assert.Single(attempts);
        Assert.Equal(keyframeChunk ? (byte)MoveProtocol.ServerFrameKind.RebuildKeyframeChunk
            : (byte)MoveProtocol.ServerFrameKind.RebuildDelta, kindByte);
        Assert.Equal(keyframeChunk ? NetChannelReliability.ReliableOrdered : NetChannelReliability.UnreliableSequenced,
            channel);
        Assert.NotNull(candidate);   // built, never committed
        Assert.DoesNotContain(sends, s => s.IsDelta || s.IsChunk || s.IsLegacy);
        Assert.Equal(ReplicationFailure.RestartToken, client.RejectReason);
        Assert.Equal(0, rig.Host.PlayerCount);
    }

    [Theory]
    [InlineData(RebuildHostKind.World, FaultPoint.RoutineDelta)]
    [InlineData(RebuildHostKind.World, FaultPoint.KeyframeChunk)]
    [InlineData(RebuildHostKind.World, FaultPoint.RoutineDeltaThenLeft)]
    [InlineData(RebuildHostKind.Sharded, FaultPoint.RoutineDelta)]
    [InlineData(RebuildHostKind.Sharded, FaultPoint.KeyframeChunk)]
    [InlineData(RebuildHostKind.Sharded, FaultPoint.RoutineDeltaThenLeft)]
    public void ThrownSendFaultsAndDisconnectsWithRestartNextServe(RebuildHostKind kind, FaultPoint point)
    {
        V2Rig rig = V2Rig.Create(kind);
        rig.SpawnPad(4000, 0x55);
        V2Client client = rig.JoinSteady(keyframeTicks: 3);
        RebuildServerStream stream = rig.Stream(client);
        int slot = client.Slot;
        if (point == FaultPoint.KeyframeChunk)
        {
            client.AutoAckKeyframes = false;
            RigDelta last = client.Deltas[^1];
            client.RequestRepair(last.Id.Epoch, last.Id.Sequence, 0);
            rig.Transport.ThrowWhen = s => s.IsChunk && s.ChunkIndex == 1;   // the second chunk of the repair
        }
        else
        {
            rig.Transport.ThrowWhen = s => s.IsDelta;
        }
        int mark = rig.Transport.Sends.Count;

        rig.Host.Poll();
        RebuildUsage before = rig.Writer.RebuildUsageForTest(slot);
        Assert.Throws<IOException>(() => rig.Host.Tick());
        client.Poll();

        Assert.True(stream.Faulted);
        Assert.Null(stream.Failure);
        RebuildUsage after = rig.Writer.RebuildUsageForTest(slot);
        ReplicationPacketId candidate = Assert.IsType<ReplicationPacketId>(after.CandidateId);
        List<RigSend> handed = rig.Transport.Sends.Skip(mark).ToList();
        if (point == FaultPoint.KeyframeChunk)
        {
            Assert.Equal(new ReplicationPacketId(before.AcknowledgedId!.Value.Epoch + 1, 1), candidate);
            Assert.Equal(0, Assert.Single(handed, s => s.IsChunk).ChunkIndex);
            Assert.Equal(0, after.NewSentCount);
        }
        else
        {
            Assert.Null(before.CandidateId);
            Assert.Equal(before.NewSentCount, after.NewSentCount);
            Assert.DoesNotContain(handed, s => s.IsDelta);
        }

        // An uncommitted candidate can never be acknowledged.
        client.Ack(candidate, NetChannelReliability.ReliableOrdered);
        client.Ack(candidate, NetChannelReliability.UnreliableSequenced);
        if (point == FaultPoint.RoutineDeltaThenLeft) client.Disconnect();
        rig.Host.Poll();
        if (point == FaultPoint.RoutineDeltaThenLeft)
        {
            // The leave forgets the faulted stream. The connection is already gone, so nothing more is sent.
            Assert.Equal(0, rig.Host.PlayerCount);
            Assert.False(rig.Host.TryGetRebuildStream(slot, out _));
            rig.Host.Tick();
            Assert.Null(client.RejectReason);
            return;
        }
        RebuildUsage acked = rig.Writer.RebuildUsageForTest(slot);
        Assert.Equal(after.AcknowledgedId, acked.AcknowledgedId);
        Assert.Equal(candidate, acked.CandidateId);
        rig.Host.Tick();
        client.Poll();

        Assert.Equal(ReplicationFailure.RestartToken, client.RejectReason);
        Assert.Equal(0, rig.Host.PlayerCount);
        Assert.False(rig.Host.TryGetRebuildStream(slot, out _));
    }

    [Theory]
    [InlineData(RebuildHostKind.World, false)]
    [InlineData(RebuildHostKind.World, true)]
    [InlineData(RebuildHostKind.Sharded, false)]
    [InlineData(RebuildHostKind.Sharded, true)]
    public void CapacityExceededDisconnectsTyped(RebuildHostKind kind, bool initialKeyframe)
    {
        V2Rig rig = V2Rig.Create(kind);
        long pad = rig.SpawnPad(initialKeyframe ? 70_000 : 4, 0x11);
        V2Client client;
        if (initialKeyframe)
        {
            client = rig.Connect();
            rig.Steps(4);   // the keyframe after acceptance cannot fit 64 KiB
        }
        else
        {
            client = rig.JoinSteady();
            rig.SetPad(pad, 70_000, 0x12);
            rig.Step();
        }

        Assert.Equal(ReplicationFailure.CapacityExceededToken, client.RejectReason);
        Assert.Equal(0, rig.Host.PlayerCount);
        Assert.Equal(initialKeyframe ? 0 : 1, rig.Transport.Sends.Count(s => s.IsChunk));
        Assert.Equal(initialKeyframe ? 0 : 1, rig.Transport.Sends.Count(s => s.IsDelta));
        Assert.Equal(1UL, rig.Host.EpochHighWater);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void RecoveryDeadlineDisconnectsAtTick90(RebuildHostKind kind)
    {
        V2Rig rig = V2Rig.Create(kind);
        V2Client client = rig.JoinSteady();
        client.AutoAckKeyframes = false;
        RigDelta last = client.Deltas[^1];

        client.RequestRepair(last.Id.Epoch, last.Id.Sequence, 0);
        RigSend chunk = Assert.Single(rig.Step(), s => s.IsChunk);   // the repair starts on this cadence tick
        Assert.Equal(last.Id.Epoch + 1, chunk.ChunkId.Epoch);
        rig.Steps(89);

        Assert.Equal(1, rig.Host.PlayerCount);
        Assert.Null(client.RejectReason);

        rig.Step();   // repair start + 90

        Assert.Equal(ReplicationFailure.RecoveryFailedToken, client.RejectReason);
        Assert.Equal(0, rig.Host.PlayerCount);
        Assert.Equal(last.Id.Epoch + 1, rig.Host.EpochHighWater);
    }
}
