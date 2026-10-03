using System.Collections.Generic;
using System.Linq;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Transport limit changes on a live format 2 stream: growth keeps the epoch and its cap, a feasible drop below the cap
/// renegotiates with a new offer, epoch and width, and an infeasible or unknown limit restarts the session rather than
/// downgrading a receiver built by format 2 to legacy in place.
/// </summary>
public class ReplicationLimitChangeTests
{
    public static IEnumerable<object[]> Hosts() => ReplicationCapabilityMatrixTests.Hosts();

    [Theory]
    [MemberData(nameof(Hosts))]
    public void LimitGrowthKeepsEpochAndCap(RebuildHostKind kind)
    {
        var options = new ReplicationStreamOptions { MaxTransportPayloadBytes = 1400 };
        V2Rig rig = V2Rig.Create(kind, limit: 512, stream: options);
        long pad = rig.SpawnPad(4, 0x11);
        V2Client client = rig.JoinSteady();
        RebuildServerStream stream = rig.Stream(client);
        Assert.Equal(512, stream.PacketCap);
        Assert.Equal(512u, client.Offers[0].Offer.PacketCap);
        ulong epoch = client.Deltas[^1].Id.Epoch;

        rig.Transport.Limit = 1400;
        int mark = rig.Transport.Sends.Count;
        List<RigSend> grown = rig.Steps(2);
        Assert.Equal(2, grown.Count(s => s.IsDelta));
        rig.SetPad(pad, 700, 0x12);   // a delta between the old cap and the grown limit
        rig.Steps(4);

        List<RigSend> after = rig.Transport.Sends.Skip(mark).Where(s => s.IsData).ToList();
        Assert.Single(client.Offers);
        Assert.DoesNotContain(after, s => s.IsOffer);
        Assert.All(after, s => Assert.True(s.Payload.Length <= 512, $"{s.Kind} payload {s.Payload.Length}"));
        Assert.Equal(512, stream.PacketCap);
        List<RigSend> chunks = after.Where(s => s.IsChunk).ToList();
        Assert.All(chunks, c => Assert.Equal(new ReplicationPacketId(epoch + 1, 1), c.ChunkId));
        Assert.All(chunks.Where(c => !c.IsLastChunk), c => Assert.Equal(512, c.Payload.Length));
        Assert.True(chunks.Count >= 2, "the oversize delta became a keyframe at the epoch's width");
        Assert.True(Pad.Contains(client.Keyframes[^1].Body, 700, 0x12));
        Assert.True(rig.Host.TryGetReplicationSelection(client.Slot, out ReplicationSelection selection));
        Assert.Equal(epoch + 1, selection.Epoch);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void LimitDropBelowCapRenegotiatesWithNewWidth(RebuildHostKind kind)
    {
        V2Rig rig = V2Rig.Create(kind);
        rig.SpawnPad(1000, 0x55);   // the keyframe needs several chunks at either width
        V2Client client = rig.JoinSteady();
        client.AutoAccept = false;
        RebuildServerStream stream = rig.Stream(client);
        ulong epoch = client.Deltas[^1].Id.Epoch;

        rig.Transport.Limit = 400;
        List<RigSend> drop = rig.Step();

        RigSend offerSend = Assert.Single(drop, s => s.IsOffer);
        Assert.DoesNotContain(drop, s => s.IsDelta || s.IsChunk);
        ReplicationModeOffer offer = client.Offers[^1].Offer;
        Assert.Equal(2, client.Offers.Count);
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, offer.Mode);
        Assert.Equal(400u, offer.PacketCap);
        Assert.Equal(epoch + 1, offer.Epoch);
        Assert.Equal(RebuildProtocol.ModeOfferBytes + 1, offerSend.Payload.Length);
        Assert.Equal(400, stream.PacketCap);
        Assert.Equal(377, ReplicationStreamOptions.ChunkWidth(stream.PacketCap));

        // The keyframe waits for the new offer's acceptance.
        Assert.DoesNotContain(rig.Steps(3), s => s.IsDelta || s.IsChunk || s.IsOffer);
        client.Accept(offer.Epoch);
        List<RigSend> chunks = rig.Step().Where(s => s.IsChunk).ToList();

        RigKeyframe keyframe = client.Keyframes[^1];
        Assert.Equal(new ReplicationPacketId(epoch + 1, 1), keyframe.Id);
        int expectedChunks = (keyframe.ObjectLength + 376) / 377;
        Assert.Equal(expectedChunks, chunks.Count);
        Assert.True(expectedChunks >= 4, "a multi-chunk keyframe at width 377");
        Assert.All(chunks, c => Assert.Equal(keyframe.Id, c.ChunkId));
        Assert.All(chunks.Where(c => !c.IsLastChunk), c => Assert.Equal(400, c.Payload.Length));
        Assert.Equal(23 + keyframe.ObjectLength - ((expectedChunks - 1) * 377), chunks[^1].Payload.Length);
        RigSend resumed = Assert.Single(rig.Step(), s => s.IsDelta);
        Assert.True(resumed.Payload.Length <= 400);
        Assert.Equal(epoch + 1, rig.Host.EpochHighWater);
        Assert.Equal(2, client.Offers.Count);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void LimitDropBelowFeasibleDisconnectsWithRestart(RebuildHostKind kind)
    {
        V2Rig rig = V2Rig.Create(kind);
        V2Client client = rig.JoinSteady();
        int mark = rig.Transport.Sends.Count;

        rig.Transport.Limit = 280;
        rig.Step();

        Assert.Equal(ReplicationFailure.RestartToken, client.RejectReason);
        Assert.Equal(0, rig.Host.PlayerCount);
        Assert.Equal(0, client.LegacyFramesAfterFirstOffer);
        Assert.Single(client.Offers);
        Assert.DoesNotContain(rig.Transport.Sends.Skip(mark), s => s.IsLegacy || s.IsOffer || s.IsDelta || s.IsChunk);

        // A fresh session negotiates against the reduced limit and is told the transport limit is unusable.
        rig.Steps(2);
        V2Client fresh = rig.Connect();
        rig.Steps(4);
        (_, ReplicationModeOffer fallback) = Assert.Single(fresh.Offers);
        Assert.Equal(RebuildProtocol.FallbackOffer(ReplicationSelectionReason.UnavailableTransportLimit), fallback);
        Assert.True(fresh.LegacyFrames.Count > 0, "the fresh session is served reliable legacy state");
    }

    [Theory]
    [InlineData(RebuildHostKind.World, false)]
    [InlineData(RebuildHostKind.World, true)]
    [InlineData(RebuildHostKind.Sharded, false)]
    [InlineData(RebuildHostKind.Sharded, true)]
    public void LiveStreamNeverDowngradesToLegacy(RebuildHostKind kind, bool duringBarrier)
    {
        V2Rig rig = V2Rig.Create(kind);
        rig.SpawnPad(4000, 0x55);   // a keyframe of three cadence ticks
        V2Client client = rig.JoinSteady(keyframeTicks: 3);
        if (duringBarrier)
        {
            client.AutoAckKeyframes = false;
            RigDelta last = client.Deltas[^1];
            client.RequestRepair(last.Id.Epoch, last.Id.Sequence, 0);
            Assert.Equal(4, rig.Step().Count(s => s.IsChunk));
        }
        int mark = rig.Transport.Sends.Count;

        rig.Transport.Limit = 0;
        rig.Steps(3);

        Assert.Equal(ReplicationFailure.RestartToken, client.RejectReason);
        Assert.Equal(0, rig.Host.PlayerCount);
        Assert.Single(client.Offers);
        Assert.Equal(0, client.LegacyFramesAfterFirstOffer);
        Assert.DoesNotContain(rig.Transport.Sends.Skip(mark), s => s.IsLegacy || s.IsOffer || s.IsDelta || s.IsChunk);
    }
}
