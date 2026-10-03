using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Tests.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The format 2 keyframe barrier on both hosts: chunks under the cadence budget, one frozen keyframe per repair, no
/// routine delta before the exact reliable keyframe acknowledgement, collapse of changes made during a barrier, and
/// the writer and stream rows of the repair contract. Every schedule is a fixed number of host ticks, one cadence tick
/// each, and every length is read from the complete transport payload.
/// </summary>
public class ReplicationBarrierTests
{
    public static IEnumerable<object[]> Hosts() => ReplicationCapabilityMatrixTests.Hosts();

    [Theory]
    [MemberData(nameof(Hosts))]
    public void KeyframeChunksFollowCadenceBudget(RebuildHostKind kind)
    {
        V2Rig rig = V2Rig.Create(kind);
        long big = rig.SpawnPad(1, 0x51);
        V2Client client = rig.JoinSteady();
        RebuildServerStream stream = rig.Stream(client);
        RigKeyframe initial = Assert.Single(client.Keyframes);
        ulong oldEpoch = initial.Id.Epoch;

        // Grow the pad so the complete keyframe object is exactly 65530 bytes: 134 full chunks of 489 and a last
        // chunk of 4. The frame arithmetic is the test's own, not the encoder's.
        const int objectLength = 65530;
        int padLength = objectLength - initial.ObjectLength + Pad.FrameBytes(1) - Pad.FrameBytes(0x4000) + 0x4000;
        Assert.Equal(objectLength, initial.ObjectLength - Pad.FrameBytes(1) + Pad.FrameBytes(padLength));
        int lastChunkBytes = objectLength - (134 * 489);
        rig.SetPad(big, padLength, 0x52);

        var keyframeChunks = new List<RigSend>();
        var chunksPerCadenceTick = new List<int>();
        var deltasWhileBarrierActive = new List<RigSend>();
        for (int tick = 0; tick < 34; tick++)
        {
            List<RigSend> sends = rig.Step();
            List<RigSend> chunks = sends.Where(s => s.IsChunk).ToList();
            Assert.Equal(chunks.Count, stream.ChunksSentThisCadence);
            chunksPerCadenceTick.Add(chunks.Count);
            keyframeChunks.AddRange(chunks);
            deltasWhileBarrierActive.AddRange(sends.Where(s => s.IsDelta));
            Assert.DoesNotContain(sends, s => s.IsOffer);
        }

        Assert.Equal(135, keyframeChunks.Count);
        Assert.Equal(Enumerable.Repeat(4, 33).Append(3), chunksPerCadenceTick);
        Assert.All(keyframeChunks, c => Assert.Equal(c.IsLastChunk ? 23 + lastChunkBytes : 512, c.Payload.Length));
        Assert.Empty(deltasWhileBarrierActive);
        ulong repairEpoch = keyframeChunks[0].ChunkId.Epoch;
        Assert.Equal(oldEpoch + 1, repairEpoch);
        var repairId = new ReplicationPacketId(repairEpoch, 1);
        Assert.All(keyframeChunks, c =>
        {
            Assert.Equal(repairId, c.ChunkId);
            Assert.Equal((uint)objectLength, c.ChunkTotal);
            Assert.Equal(RebuildServerStream.KeyframeStreamId, c.ChunkStreamId);
            Assert.Equal((ushort)repairId.Sequence, c.ChunkFragmentSequence);
            Assert.Equal(135, c.ChunkCount);
            Assert.Equal(NetChannelReliability.ReliableOrdered, c.Reliability);
        });
        Assert.Equal(Enumerable.Range(0, 135), keyframeChunks.Select(c => c.ChunkIndex));
        RigKeyframe repaired = client.Keyframes[^1];
        Assert.Equal(repairId, repaired.Id);
        Assert.Equal(objectLength, repaired.ObjectLength);
        Assert.True(Pad.Contains(repaired.Body, padLength, 0x52), "the keyframe carries the grown pad");

        // The client acknowledged the complete keyframe reliably. Steady serving resumes on the next cadence tick.
        RigSend resumed = Assert.Single(rig.Step(), s => s.IsDelta);
        Assert.Equal(NetChannelReliability.UnreliableSequenced, resumed.Reliability);
        RebuildWireHeader header = RebuildWire.ReadHeader(resumed.DeltaBody);
        Assert.Equal(repairEpoch, header.Epoch);
        Assert.Equal(repairId.Sequence, header.Baseline);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void OversizeDeltaStartsOneFrozenKeyframe(RebuildHostKind kind)
    {
        V2Rig rig = V2Rig.Create(kind);
        long big = rig.SpawnPad(1, 0x61);
        long small = rig.SpawnPad(4, 0x11, dx: -2f, dz: -2f);
        V2Client client = rig.JoinSteady();
        ulong epoch = Assert.Single(client.Keyframes).Id.Epoch;
        int mark = rig.Transport.Sends.Count;

        rig.SetPad(big, 2500, 0x62);   // the routine delta now exceeds the 512-byte cap
        List<RigSend> first = rig.Step();
        rig.SetPad(small, 4, 0x19);    // a change after the freeze
        List<RigSend> second = rig.Step();

        Assert.DoesNotContain(first, s => s.IsDelta);
        Assert.DoesNotContain(second, s => s.IsDelta);
        RigKeyframe frozen = client.Keyframes[^1];
        int chunks = ChunksAt489(frozen.ObjectLength);
        Assert.InRange(chunks, 6, 8);
        Assert.Equal(4, first.Count(s => s.IsChunk));
        Assert.Equal(chunks - 4, second.Count(s => s.IsChunk));
        var repairId = new ReplicationPacketId(epoch + 1, 1);
        Assert.All(first.Concat(second).Where(s => s.IsChunk), c => Assert.Equal(repairId, c.ChunkId));
        Assert.Equal(epoch + 1, rig.Host.EpochHighWater);
        Assert.Equal(repairId, frozen.Id);
        Assert.True(Pad.Contains(frozen.Body, 2500, 0x62), "the keyframe carries the state that overflowed");
        Assert.True(Pad.Contains(frozen.Body, 4, 0x11), "the keyframe keeps the bytes frozen at the trigger");
        Assert.False(Pad.Contains(frozen.Body, 4, 0x19), "a change after the freeze is not in the keyframe");

        RigSend resumed = Assert.Single(rig.Step(), s => s.IsDelta);
        Assert.Equal(repairId.Sequence, RebuildWire.ReadHeader(resumed.DeltaBody).Baseline);
        Assert.True(Pad.Contains(resumed.DeltaBody, 4, 0x19), "the first resumed delta carries the later change");
        List<RigSend> run = rig.Transport.Sends.Skip(mark).ToList();
        Assert.DoesNotContain(run, s => s.IsOffer);
        Assert.All(run.Where(s => s.IsDelta || s.IsChunk), s => Assert.True(s.Payload.Length <= 512));
        Assert.Equal(new[] { epoch, epoch + 1 }, rig.Transport.Sends.Where(s => s.IsChunk)
            .Select(s => s.ChunkId.Epoch).Distinct());
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void NoDeltaBeforeExactReliableKeyframeAck(RebuildHostKind kind)
    {
        V2Rig rig = V2Rig.Create(kind);
        V2Client client = rig.Connect();
        client.AutoAckKeyframes = false;
        rig.Steps(4);   // connect, join, offer and acceptance, then the keyframe
        ReplicationPacketId id = Assert.Single(client.Keyframes).Id;
        AoiDeltaReplicator writer = rig.Writer;

        var wrong = new (ReplicationPacketId Id, NetChannelReliability Reliability)[]
        {
            (id, NetChannelReliability.UnreliableSequenced),
            (new ReplicationPacketId(id.Epoch, id.Sequence + 1), NetChannelReliability.ReliableOrdered),
            (new ReplicationPacketId(id.Epoch, id.Sequence - 1), NetChannelReliability.ReliableOrdered),
            (new ReplicationPacketId(id.Epoch + 1, id.Sequence), NetChannelReliability.ReliableOrdered),
        };
        foreach ((ReplicationPacketId ackId, NetChannelReliability reliability) in wrong)
        {
            client.Ack(ackId, reliability);
            List<RigSend> sends = rig.Step();
            Assert.DoesNotContain(sends, s => s.IsDelta || s.IsChunk || s.IsOffer);
            Assert.Null(writer.RebuildUsageForTest(client.Slot).AcknowledgedId);
        }

        client.Ack(id, NetChannelReliability.ReliableOrdered);
        RigSend delta = Assert.Single(rig.Step(), s => s.IsDelta);
        Assert.Equal(NetChannelReliability.UnreliableSequenced, delta.Reliability);
        RebuildWireHeader header = RebuildWire.ReadHeader(delta.DeltaBody);
        Assert.Equal(id.Epoch, header.Epoch);
        Assert.Equal(id.Sequence, header.Baseline);
        Assert.Equal(id, writer.RebuildUsageForTest(client.Slot).AcknowledgedId);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ChangesDuringBarrierCollapseIntoNewestProjection(RebuildHostKind kind)
    {
        var hidden = new HashSet<long>();
        V2Rig rig = V2Rig.Create(kind, visible: (_, netId) => !hidden.Contains(netId));
        rig.SpawnPad(2500, 0x55);   // the keyframe spans two cadence ticks
        long small = rig.SpawnPad(4, 0x11, dx: -2f, dz: -2f);
        long shown = rig.SpawnPad(4, 0x33, dx: 3f, dz: -3f);
        V2Client client = rig.JoinSteady(keyframeTicks: 2);
        client.AutoAckKeyframes = false;
        RigDelta last = client.Deltas[^1];
        int chunks = ChunksAt489(client.Keyframes[0].ObjectLength);
        Assert.InRange(chunks, 5, 8);

        client.RequestRepair(last.Id.Epoch, last.Id.Sequence, 0);
        Assert.Equal(4, rig.Step().Count(s => s.IsChunk));   // frozen here
        rig.SetPad(small, 4, 0x12);
        hidden.Add(shown);
        Assert.Equal(chunks - 4, rig.Step().Count(s => s.IsChunk));
        rig.SetPad(small, 4, 0x13);
        Assert.DoesNotContain(rig.Step(), s => s.IsDelta || s.IsChunk);
        rig.SetPad(small, 4, 0x14);
        Assert.DoesNotContain(rig.Step(), s => s.IsDelta || s.IsChunk);
        RigKeyframe keyframe = client.Keyframes[^1];
        Assert.True(Pad.Contains(keyframe.Body, 4, 0x11));
        Assert.True(Pad.Contains(keyframe.Body, 4, 0x33), "the entity was visible at the freeze");

        client.Ack(keyframe.Id, NetChannelReliability.ReliableOrdered);
        RigSend resumed = Assert.Single(rig.Step(), s => s.IsDelta);

        RebuildWireHeader header = RebuildWire.ReadHeader(resumed.DeltaBody);
        Assert.Equal(keyframe.Id.Epoch, header.Epoch);
        Assert.Equal(keyframe.Id.Sequence, header.Baseline);
        Assert.Equal(0, header.Flags);
        Assert.Equal(new[] { shown }, RebuildWire.ReadRemovedNetIds(resumed.DeltaBody));
        Assert.True(Pad.Contains(resumed.DeltaBody, 4, 0x14), "the newest value");
        Assert.False(Pad.Contains(resumed.DeltaBody, 4, 0x12), "no intermediate value");
        Assert.False(Pad.Contains(resumed.DeltaBody, 4, 0x13), "no intermediate value");
        var resumedId = new ReplicationPacketId(header.Epoch, header.Snapshot);
        Assert.True(rig.Writer.TryGetRetainedProjectionForTest(client.Slot, resumedId, out ReplicationProjection retained));
        IReadOnlyList<ProjectionEntry> entries = ProjectionDump.Of(retained);
        Assert.DoesNotContain(entries, e => e.NetId == shown);
        Assert.Equal(new byte[] { 0x14, 0x14, 0x14, 0x14 },
            Assert.Single(entries, e => e.NetId == small && e.TypeId == Pad.TypeId).Payload);
        Assert.Single(rig.Step(), s => s.IsDelta);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void RequestFloodDoesNotRestartActiveBarrier(RebuildHostKind kind)
    {
        V2Rig rig = V2Rig.Create(kind);
        rig.SpawnPad(4000, 0x55);   // three cadence ticks of chunks
        V2Client client = rig.JoinSteady(keyframeTicks: 3);
        client.AutoAckKeyframes = false;
        RigDelta last = client.Deltas[^1];
        ulong epoch = last.Id.Epoch;
        int count = ChunksAt489(client.Keyframes[0].ObjectLength);
        Assert.InRange(count, 9, 12);

        client.RequestRepair(epoch, last.Id.Sequence, 0);
        var perTick = new List<int>();
        var sends = new List<RigSend>();
        for (int tick = 0; tick < 5; tick++)
        {
            List<RigSend> step = rig.Step();
            perTick.Add(step.Count(s => s.IsChunk));
            sends.AddRange(step);
            client.RequestRepair(epoch, last.Id.Sequence, 0);
            client.RequestRepair(epoch + 1, 0, 0);
        }

        Assert.Equal(new[] { 4, 4, count - 8, 0, 0 }, perTick);
        List<RigSend> chunks = sends.Where(s => s.IsChunk).ToList();
        Assert.All(chunks, c => Assert.Equal(new ReplicationPacketId(epoch + 1, 1), c.ChunkId));
        Assert.Equal(Enumerable.Range(0, count), chunks.Select(c => c.ChunkIndex));
        Assert.Equal(epoch + 1, rig.Host.EpochHighWater);
        Assert.DoesNotContain(sends, s => s.IsOffer || s.IsDelta);
        Assert.True(rig.Host.TryGetReplicationSelection(client.Slot, out ReplicationSelection selection));
        Assert.Equal(epoch + 1, selection.Epoch);
        Assert.Equal(0, rig.Host.CountSuspicious(client.Slot, SuspiciousReason.MalformedPacket));
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void NeverSentKeyframeAckCannotPromote(RebuildHostKind kind)
    {
        V2Rig rig = V2Rig.Create(kind);
        rig.SpawnPad(4000, 0x55);
        V2Client client = rig.Connect();
        client.AutoAckKeyframes = false;
        List<RigSend> firstRun = rig.Steps(4);   // offer, then the first four chunks
        ReplicationPacketId id = firstRun.Where(s => s.IsChunk).Select(s => s.ChunkId).Distinct().Single();
        Assert.Equal(4, firstRun.Count(s => s.IsChunk));
        int count = firstRun.First(s => s.IsChunk).ChunkCount;
        Assert.InRange(count, 9, 12);

        client.Ack(id, NetChannelReliability.ReliableOrdered);   // the keyframe is not yet handed over
        Assert.Equal(4, rig.Step().Count(s => s.IsChunk));
        Assert.Equal(count - 8, rig.Step().Count(s => s.IsChunk));
        Assert.Equal(id, Assert.Single(client.Keyframes).Id);
        List<RigSend> waiting = rig.Steps(3);

        Assert.DoesNotContain(waiting, s => s.IsDelta || s.IsChunk);
        Assert.Null(rig.Writer.RebuildUsageForTest(client.Slot).AcknowledgedId);
        client.Ack(id, NetChannelReliability.ReliableOrdered);
        Assert.Single(rig.Step(), s => s.IsDelta);
        Assert.Equal(id, rig.Writer.RebuildUsageForTest(client.Slot).AcknowledgedId);
    }

    public static IEnumerable<object[]> RepairRows() =>
        from RebuildHostKind kind in new[] { RebuildHostKind.World, RebuildHostKind.Sharded }
        from RepairTrigger trigger in Enum.GetValues<RepairTrigger>()
        select new object[] { kind, trigger };

    [Theory]
    [MemberData(nameof(RepairRows))]
    public void RepairContractTriggersMapToOneAction(RebuildHostKind kind, RepairTrigger trigger)
    {
        V2Rig rig = V2Rig.Create(kind);
        long pad = rig.SpawnPad(4, 0x11);
        V2Client client = rig.JoinSteady();
        RebuildServerStream stream = rig.Stream(client);
        RigDelta last = client.Deltas[^1];
        ulong epoch = last.Id.Epoch;
        int offersBefore = client.Offers.Count;
        int mark = rig.Transport.Sends.Count;
        int window = 40;
        switch (trigger)
        {
            case RepairTrigger.NoAckWindow:
                client.AutoAckDeltas = false;   // the first delta's ack is already in flight
                break;
            case RepairTrigger.SequenceAmbiguous:
                rig.Writer.SeedRebuildSequenceForTest(client.Slot, unchecked(last.Id.Sequence + 0x8000_0000u));
                break;
            case RepairTrigger.OversizeDelta:
                rig.SetPad(pad, 600, 0x12);
                break;
            case RepairTrigger.RepairRequest:
                client.RequestRepair(epoch, last.Id.Sequence, 0);
                break;
            case RepairTrigger.LimitDropFeasible:
                rig.Transport.Limit = 400;
                break;
            case RepairTrigger.LimitDropInfeasible:
                rig.Transport.Limit = 280;
                break;
            case RepairTrigger.LimitUnknown:
                rig.Transport.Limit = 0;
                break;
            case RepairTrigger.CapacityExceeded:
                rig.SetPad(pad, 70_000, 0x12);
                break;
            case RepairTrigger.RecoveryDeadline:
                client.AutoAckKeyframes = false;
                client.RequestRepair(epoch, last.Id.Sequence, 0);
                window = 95;
                break;
            case RepairTrigger.SendRefused:
                stream.SendOverrideForTest = (_, _) => false;
                break;
        }
        rig.Steps(window);

        List<RigSend> run = rig.Transport.Sends.Skip(mark).ToList();
        ulong[] newEpochs = run.Where(s => s.IsChunk).Select(s => s.ChunkId.Epoch).Distinct().ToArray();
        int newOffers = client.Offers.Count - offersBefore;
        (int offers, int epochs, string? token) = trigger switch
        {
            RepairTrigger.NoAckWindow or RepairTrigger.SequenceAmbiguous or RepairTrigger.OversizeDelta
                or RepairTrigger.RepairRequest => (0, 1, (string?)null),
            RepairTrigger.LimitDropFeasible => (1, 1, null),
            RepairTrigger.LimitDropInfeasible or RepairTrigger.LimitUnknown or RepairTrigger.SendRefused =>
                (0, 0, ReplicationFailure.RestartToken),
            RepairTrigger.CapacityExceeded => (0, 0, ReplicationFailure.CapacityExceededToken),
            RepairTrigger.RecoveryDeadline => (0, 1, ReplicationFailure.RecoveryFailedToken),
            _ => throw new ArgumentOutOfRangeException(nameof(trigger)),
        };
        Assert.Equal(offers, newOffers);
        Assert.Equal(epochs, newEpochs.Length);
        Assert.Equal(token, client.RejectReason);
        Assert.Equal(token is null ? 1 : 0, rig.Host.PlayerCount);
        Assert.Equal(epoch + (ulong)(offers + epochs > 0 ? 1 : 0), rig.Host.EpochHighWater);
        if (epochs == 1) Assert.Equal(epoch + 1, newEpochs[0]);
        if (offers == 1)
        {
            ReplicationModeOffer offer = client.Offers[^1].Offer;
            Assert.Equal(400u, offer.PacketCap);
            Assert.Equal(epoch + 1, offer.Epoch);
        }
        if (trigger == RepairTrigger.NoAckWindow)
        {
            int firstChunk = run.FindIndex(s => s.IsChunk);
            Assert.Equal(31, run.Take(firstChunk).Count(s => s.IsDelta));
        }
        Assert.DoesNotContain(run, s => s.IsLegacy);
        Assert.All(run.Where(s => s.IsDelta || s.IsChunk), s => Assert.True(s.Payload.Length <= 512));
    }

    private static int ChunksAt489(int objectLength) => (objectLength + 488) / 489;

    [Fact]
    public void OwnerChangeStartsAFreshEpoch()
    {
        // Stream level: both hosts bind one owner per session, so only a direct stream can change it mid-epoch.
        var transport = new RigTransport(1400);
        var net = new NetServer(transport, 4, new AllowAllAuthenticator());
        var client = new NetClient(transport.Hub.CreateClient(), TestHandshake.Wire());
        for (int i = 0; i < 3; i++)
        {
            net.Poll();
            while (net.TryDequeueEvent(out _)) { }
            client.Poll();
            while (client.TryDequeueEvent(out _)) { }
        }
        Assert.True(client.Slot >= 0, "the client holds a slot");
        var writer = new AoiDeltaReplicator(Pad.Registry());
        var options = new ReplicationStreamOptions();
        var stream = new RebuildServerStream(net, client.Slot, writer, options, options.StreamLimits(),
            new ReplicationEpochAllocator());
        var world = new World();
        foreach (long netId in new long[] { 1, 2 })
        {
            Entity entity = world.Spawn();
            world.Set(entity, new NetId(netId));
            world.Set(entity, new PadState { Length = 4, Fill = (byte)netId });
        }
        var interest = new HashSet<long> { 1, 2 };

        stream.OnRebuildCapability(0);
        ulong epoch = stream.Selection.Epoch;
        stream.HandleControl(new RebuildClientControl(RebuildControlKind.Accept, epoch, 0, 0),
            NetChannelReliability.ReliableOrdered, 0);
        List<RigSend> keyframe = ServeOnce(1, ownerNetId: 1);
        Assert.Equal(new ReplicationPacketId(epoch, 1), Assert.Single(keyframe, s => s.IsChunk).ChunkId);
        stream.HandleControl(new RebuildClientControl(RebuildControlKind.Acknowledge, epoch, 1, 0),
            NetChannelReliability.ReliableOrdered, 1);
        Assert.Equal(epoch, RebuildWire.ReadHeader(Assert.Single(ServeOnce(2, ownerNetId: 1), s => s.IsDelta)
            .DeltaBody).Epoch);

        List<RigSend> changed = ServeOnce(3, ownerNetId: 2);

        Assert.DoesNotContain(changed, s => s.IsDelta || s.IsOffer);
        Assert.Equal(new ReplicationPacketId(epoch + 1, 1), Assert.Single(changed, s => s.IsChunk).ChunkId);
        Assert.Equal(epoch + 1, stream.Selection.Epoch);
        Assert.Null(stream.Failure);

        List<RigSend> ServeOnce(long tick, long ownerNetId)
        {
            int before = transport.Sends.Count;
            writer.BeginTick();
            stream.Serve(world, interest, ownerNetId, 0, tick, allowance: true);
            return transport.Sends.Skip(before).ToList();
        }
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void RepairWithUnchangedLimitsSendsNoOffer(RebuildHostKind kind)
    {
        V2Rig rig = V2Rig.Create(kind);
        V2Client client = rig.JoinSteady();
        RigDelta last = client.Deltas[^1];
        ulong epoch = last.Id.Epoch;

        client.RequestRepair(epoch, last.Id.Sequence, 0);
        List<RigSend> repair = rig.Step();

        RigSend chunk = Assert.Single(repair, s => s.IsChunk);
        Assert.Equal(new ReplicationPacketId(epoch + 1, 1), chunk.ChunkId);
        Assert.DoesNotContain(repair, s => s.IsOffer || s.IsDelta);
        Assert.Single(client.Offers);
        Assert.Equal(512, rig.Stream(client).PacketCap);
        Assert.True(rig.Host.TryGetReplicationSelection(client.Slot, out ReplicationSelection selection));
        Assert.Equal(new ReplicationSelection(ReplicationDeliveryMode.AcknowledgedUnreliable,
            ReplicationSelectionReason.Selected, epoch + 1), selection);

        RigSend resumed = Assert.Single(rig.Step(), s => s.IsDelta);
        Assert.Equal(epoch + 1, RebuildWire.ReadHeader(resumed.DeltaBody).Epoch);
        Assert.Single(client.Offers);
    }
}
