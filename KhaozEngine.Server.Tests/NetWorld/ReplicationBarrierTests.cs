using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
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

/// <summary>One writer or stream row of the repair contract table.</summary>
public enum RepairTrigger
{
    NoAckWindow,
    SequenceAmbiguous,
    OversizeDelta,
    RepairRequest,
    LimitDropFeasible,
    LimitDropInfeasible,
    LimitUnknown,
    CapacityExceeded,
    RecoveryDeadline,
    SendRefused,
}

/// <summary>A replicated padding component of a chosen length and fill byte, so a case controls projection and delta
/// sizes exactly.</summary>
internal struct PadState : IComponent
{
    public int Length;
    public byte Fill;
}

/// <summary>The padding codec and its expected wire frame, written by hand rather than by the encoder.</summary>
internal static class Pad
{
    public const ushort TypeId = ReplicationRegistry.FirstExtensionTypeId + 9;

    public static ReplicationRegistry Registry() => MoveProtocol.CreateRegistry(r => r.Register<PadState>(TypeId,
        static (pad, writer) =>
        {
            for (int i = 0; i < pad.Length; i++) writer.Write(pad.Fill);
        },
        static reader =>
        {
            int length = (int)(reader.BaseStream.Length - reader.BaseStream.Position);
            byte[] bytes = reader.ReadBytes(length);
            return new PadState { Length = length, Fill = length == 0 ? (byte)0 : bytes[0] };
        }));

    /// <summary>Frame bytes on the wire: type id, 7-bit length, payload.</summary>
    public static int FrameBytes(int length) => sizeof(ushort) + SevenBitBytes(length) + length;

    /// <summary>The exact frame a pad of <paramref name="length"/> bytes of <paramref name="fill"/> takes.</summary>
    public static byte[] Frame(int length, byte fill)
    {
        var frame = new byte[FrameBytes(length)];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, TypeId);
        int at = sizeof(ushort);
        for (uint v = (uint)length; ; v >>= 7)
        {
            if (v < 0x80) { frame[at++] = (byte)v; break; }
            frame[at++] = (byte)(v | 0x80);
        }
        frame.AsSpan(at).Fill(fill);
        return frame;
    }

    /// <summary>True when <paramref name="body"/> carries the exact pad frame.</summary>
    public static bool Contains(ReadOnlySpan<byte> body, int length, byte fill) =>
        body.IndexOf(Frame(length, fill)) >= 0;

    private static int SevenBitBytes(int value)
    {
        int bytes = 1;
        for (uint v = (uint)value; v >= 0x80; v >>= 7) bytes++;
        return bytes;
    }
}

/// <summary>One payload the server handed to the rig transport, session frame byte included, with its host tick.</summary>
internal readonly record struct RigSend(int Step, byte[] Payload, NetChannelReliability Reliability)
{
    // [session opcode][server frame kind][...]. A chunk continues [epoch u64][seq u32][total u32][stream][seq16]
    // [index][count][bytes]. A delta continues [localNetId i64][movementAck i32][format 2 body].
    private const int ChunkGeneric = 18;

    public bool IsData => Payload.Length > 1 && Payload[0] == (byte)SessionOpcode.Data;
    public MoveProtocol.ServerFrameKind Kind => (MoveProtocol.ServerFrameKind)Payload[1];
    public bool IsChunk => IsData && Kind == MoveProtocol.ServerFrameKind.RebuildKeyframeChunk;
    public bool IsDelta => IsData && Kind == MoveProtocol.ServerFrameKind.RebuildDelta;
    public bool IsOffer => IsData && Kind == MoveProtocol.ServerFrameKind.ReplicationMode;
    public bool IsLegacy => IsData && Kind is MoveProtocol.ServerFrameKind.Snapshot or MoveProtocol.ServerFrameKind.Delta;

    public ReplicationPacketId ChunkId => new(BinaryPrimitives.ReadUInt64LittleEndian(Payload.AsSpan(2)),
        BinaryPrimitives.ReadUInt32LittleEndian(Payload.AsSpan(10)));
    public uint ChunkTotal => BinaryPrimitives.ReadUInt32LittleEndian(Payload.AsSpan(14));
    public byte ChunkStreamId => Payload[ChunkGeneric];
    public ushort ChunkFragmentSequence => BinaryPrimitives.ReadUInt16LittleEndian(Payload.AsSpan(ChunkGeneric + 1));
    public int ChunkIndex => Payload[ChunkGeneric + 3];
    public int ChunkCount => Payload[ChunkGeneric + 4];
    public bool IsLastChunk => ChunkIndex == ChunkCount - 1;
    public byte[] DeltaBody => Payload[(2 + RebuildProtocol.EnvelopeBytes)..];
}

/// <summary>A server transport over the in-memory hub that answers one configured limit per channel, records every
/// payload with its host tick and can throw on a chosen send.</summary>
internal sealed class RigTransport : INetTransport
{
    private readonly INetTransport inner;

    public RigTransport(int limit)
    {
        Hub = new InMemoryTransportHub();
        inner = Hub.Server;
        Limit = limit;
    }

    public InMemoryTransportHub Hub { get; }
    public int UnreliableLimit { get; set; }
    public int ReliableLimit { get; set; }
    public int Limit { set { UnreliableLimit = value; ReliableLimit = value; } }
    public int Step { get; set; }
    public List<RigSend> Sends { get; } = new();

    /// <summary>Throws an <see cref="IOException"/> from the first send it matches, then clears itself.</summary>
    public Func<RigSend, bool>? ThrowWhen { get; set; }

    public int MaxUnfragmentedPayloadBytes(NetConnectionId connection, NetChannelReliability reliability) =>
        reliability == NetChannelReliability.ReliableOrdered ? ReliableLimit : UnreliableLimit;

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        var send = new RigSend(Step, payload.ToArray(), reliability);
        if (ThrowWhen is { } predicate && predicate(send))
        {
            ThrowWhen = null;
            throw new IOException("injected send fault");
        }
        Sends.Add(send);
        inner.Send(target, payload, reliability);
    }

    public void Poll() => inner.Poll();
    public bool TryDequeueEvent(out NetEvent ev) => inner.TryDequeueEvent(out ev);
    public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);
    public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) => inner.Disconnect(connection, reason);
    public void Dispose() => inner.Dispose();
}

internal readonly record struct RigKeyframe(int Step, ReplicationPacketId Id, long LocalNetId, int MovementAck,
    byte[] Body, int ObjectLength);

internal readonly record struct RigDelta(int Step, long LocalNetId, int MovementAck, byte[] Body)
{
    public RebuildWireHeader Header => RebuildWire.ReadHeader(Body);
    public ReplicationPacketId Id => new(Header.Epoch, Header.Snapshot);
}

/// <summary>
/// A raw format 2 client: joins, advertises both delta capabilities reliably, accepts mode 1 offers, reassembles
/// keyframe chunks and checks each object against its declared total, and acknowledges keyframes reliably and routine
/// deltas unreliably unless told not to. It never reconstructs state.
/// </summary>
internal sealed class V2Client
{
    private readonly NetClient net;
    private readonly bool requestRebuild;
    private readonly List<byte> assembly = new();
    private ReplicationPacketId? assemblingId;
    private uint assemblyTotal;

    public V2Client(INetTransport transport, bool requestRebuild)
    {
        net = new NetClient(transport, TestHandshake.Wire());
        this.requestRebuild = requestRebuild;
    }

    public bool AutoAccept { get; set; } = true;
    public bool AutoAckKeyframes { get; set; } = true;
    public bool AutoAckDeltas { get; set; } = true;
    public int Step { get; set; }
    public bool Joined { get; private set; }
    public int Slot => net.Slot;
    public string? RejectReason { get; private set; }
    public List<(int Step, ReplicationModeOffer Offer)> Offers { get; } = new();
    public List<RigKeyframe> Keyframes { get; } = new();
    public List<RigDelta> Deltas { get; } = new();
    public List<(int Step, MoveProtocol.ServerFrameKind Kind)> LegacyFrames { get; } = new();

    public int LegacyFramesAfterFirstOffer =>
        Offers.Count == 0 ? 0 : LegacyFrames.Count(f => f.Step > Offers[0].Step);

    public void Ack(ReplicationPacketId id, NetChannelReliability reliability) =>
        net.Send(RebuildProtocol.EncodeAck(id), reliability);

    public void Accept(ulong epoch) =>
        net.Send(RebuildProtocol.EncodeAcceptance(epoch), NetChannelReliability.ReliableOrdered);

    public void RequestRepair(ulong epoch, uint lastAccepted, uint missingBaseline) =>
        net.Send(RebuildProtocol.EncodeRepair(epoch, lastAccepted, missingBaseline), NetChannelReliability.ReliableOrdered);

    public void Disconnect() => net.Disconnect();

    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ClientSessionEventKind.Joined:
                    Joined = true;
                    net.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.DeltaCapable),
                        NetChannelReliability.ReliableOrdered);
                    if (requestRebuild)
                        net.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.RebuildDeltaCapable),
                            NetChannelReliability.ReliableOrdered);
                    break;
                case ClientSessionEventKind.Rejected:
                    RejectReason = ev.RejectReason;
                    break;
                case ClientSessionEventKind.Data:
                    OnFrame(ev.Data);
                    break;
            }
        }
    }

    private void OnFrame(byte[] data)
    {
        if (!MoveProtocol.TryDecodeServerFrame(data, out MoveProtocol.ServerFrameKind kind, out byte[] payload)) return;
        switch (kind)
        {
            case MoveProtocol.ServerFrameKind.Snapshot or MoveProtocol.ServerFrameKind.Delta:
                LegacyFrames.Add((Step, kind));
                break;
            case MoveProtocol.ServerFrameKind.ReplicationMode:
                Assert.True(RebuildProtocol.TryDecodeModeOffer(payload, out ReplicationModeOffer offer), "offer decodes");
                Offers.Add((Step, offer));
                if (AutoAccept && offer.Mode == ReplicationDeliveryMode.AcknowledgedUnreliable) Accept(offer.Epoch);
                break;
            case MoveProtocol.ServerFrameKind.RebuildKeyframeChunk:
                OnChunk(payload);
                break;
            case MoveProtocol.ServerFrameKind.RebuildDelta:
                var delta = new RigDelta(Step, BinaryPrimitives.ReadInt64LittleEndian(payload),
                    BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8)), payload[RebuildProtocol.EnvelopeBytes..]);
                Deltas.Add(delta);
                if (AutoAckDeltas) Ack(delta.Id, NetChannelReliability.UnreliableSequenced);
                break;
        }
    }

    private void OnChunk(byte[] payload)
    {
        var id = new ReplicationPacketId(BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8)));
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12));
        ReadOnlySpan<byte> chunk = payload.AsSpan(16);
        int index = chunk[3];
        int count = chunk[4];
        if (index == 0)
        {
            assemblingId = id;
            assemblyTotal = total;
            assembly.Clear();
        }
        else if (assemblingId != id)
        {
            return;
        }
        Assert.Equal(assemblyTotal, total);
        assembly.AddRange(chunk[MessageFragmenter.HeaderBytes..].ToArray());
        if (index != count - 1) return;
        byte[] whole = assembly.ToArray();
        Assert.Equal((int)total, whole.Length);
        assemblingId = null;
        Keyframes.Add(new RigKeyframe(Step, id, BinaryPrimitives.ReadInt64LittleEndian(whole),
            BinaryPrimitives.ReadInt32LittleEndian(whole.AsSpan(8)), whole[RebuildProtocol.EnvelopeBytes..],
            whole.Length));
        if (AutoAckKeyframes) Ack(id, NetChannelReliability.ReliableOrdered);
    }
}

/// <summary>A host, its rig transport and raw format 2 clients, stepped together one host tick at a time with the
/// configured tick length, so every step is exactly one cadence tick unless a case passes a shorter frame.</summary>
internal sealed class V2Rig
{
    private V2Rig(RigTransport transport, RebuildHost host)
    {
        Transport = transport;
        Host = host;
    }

    public RigTransport Transport { get; }
    public RebuildHost Host { get; }
    public List<V2Client> Clients { get; } = new();
    public int StepIndex { get; private set; }
    public AoiDeltaReplicator Writer => Host.Writer!;

    public static V2Rig Create(RebuildHostKind kind, int limit = 1400, ReplicationStreamOptions? stream = null,
        Func<int, long, bool>? visible = null)
    {
        var transport = new RigTransport(limit);
        return new V2Rig(transport, RebuildHost.Create(kind, transport, allowUnreliable: true, visible: visible,
            stream: stream, registry: Pad.Registry()));
    }

    public V2Client Connect(bool requestRebuild = true)
    {
        var client = new V2Client(Transport.Hub.CreateClient(), requestRebuild);
        Clients.Add(client);
        return client;
    }

    /// <summary>Connects one client and runs it into steady serving on its exact schedule: connect, join and the
    /// answered capability take three ticks, the keyframe takes <paramref name="keyframeTicks"/>, and the first routine
    /// delta follows its reliable acknowledgement one tick later.</summary>
    public V2Client JoinSteady(int keyframeTicks = 1)
    {
        V2Client client = Connect();
        Steps(3);
        (_, ReplicationModeOffer offer) = Assert.Single(client.Offers);
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, offer.Mode);
        Steps(keyframeTicks);
        Assert.Single(client.Keyframes);
        Assert.Empty(client.Deltas);
        Step();
        Assert.Single(client.Deltas);
        return client;
    }

    public List<RigSend> Step(float dt = RebuildHost.Dt)
    {
        StepIndex++;
        Transport.Step = StepIndex;
        foreach (V2Client client in Clients) client.Step = StepIndex;
        int before = Transport.Sends.Count;
        Host.Poll();
        Host.Tick(dt);
        foreach (V2Client client in Clients) client.Poll();
        return Transport.Sends.GetRange(before, Transport.Sends.Count - before);
    }

    public List<RigSend> Steps(int count)
    {
        var sends = new List<RigSend>();
        for (int i = 0; i < count; i++) sends.AddRange(Step());
        return sends;
    }

    public long SpawnPad(int length, byte fill, float dx = 2f, float dz = 2f) =>
        Host.SpawnEntity(RebuildHost.Spawn.X + dx, RebuildHost.Spawn.Z + dz,
            (world, entity) => world.Set(entity, new PadState { Length = length, Fill = fill }));

    public void SetPad(long netId, int length, byte fill)
    {
        Assert.True(Host.TryGetEntity(netId, out World world, out Entity entity), $"entity {netId} exists");
        world.Set(entity, new PadState { Length = length, Fill = fill });
    }

    public RebuildServerStream Stream(V2Client client)
    {
        Assert.True(Host.TryGetRebuildStream(client.Slot, out RebuildServerStream stream), "the slot has a stream");
        return stream;
    }
}
