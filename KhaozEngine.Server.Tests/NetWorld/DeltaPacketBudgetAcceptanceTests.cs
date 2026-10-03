using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Packet, keyframe and metadata boundaries on both heads. Every size below is computed from the documented wire
/// layout, independently of the writer: a state datagram is session byte, kind byte, 12-byte envelope, 18-byte header
/// and two counts, then per changed entity its net id, entry flag, removed count, frames and terminator. A keyframe
/// object is the envelope, header, both counts and a full entry per entity.
/// </summary>
public sealed class DeltaPacketBudgetAcceptanceTests
{
    private const int Phase = 1;

    // Wire sizes of the fixed parts, from the format 2 layout.
    private const int DatagramPrefix = 1 + 1 + 12 + 18 + 4 + 4;
    private const int EntryBytes = 8 + 1 + 4 + 2;
    private const int ExtensionFrameHead = 2;
    private const int KeyframePrefix = 12 + 18 + 4 + 4;
    private const int PositionFrame = 2 + 16;
    private const int MovementFrame = 2 + 56;
    private const int OwnerStateFrame = 2 + 8;

    public static TheoryData<bool, string> Cases()
    {
        var data = new TheoryData<bool, string>();
        foreach (bool sharded in new[] { false, true })
            foreach (string c in new[] { "Cap512", "Cap281", "UnknownLimit", "Infeasible280", "ShrinkDuringRepair",
                "Keyframe64KiB", "MetadataCap" })
                data.Add(sharded, c);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PacketAtCapAndOneByteOverSelectCorrectPath(bool sharded, string budgetCase)
    {
        switch (budgetCase)
        {
            case "Cap512": AtCapThenOneOver(sharded, 512); break;
            case "Cap281": AtCapThenOneOver(sharded, 281); break;
            case "UnknownLimit": FallsBack(sharded, 0); break;
            case "Infeasible280": FallsBack(sharded, 280); break;
            case "ShrinkDuringRepair": ShrinkDuringRepair(sharded); break;
            case "Keyframe64KiB": KeyframeAtLimitThenOneOver(sharded); break;
            case "MetadataCap": MetadataAtLimitThenOneOver(sharded); break;
            default: throw new ArgumentOutOfRangeException(nameof(budgetCase));
        }
    }

    // A datagram of exactly the cap goes unreliable. One byte more starts exactly one reliable keyframe.
    private static void AtCapThenOneOver(bool sharded, int cap)
    {
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, Array.Empty<DeltaFault>(),
            new DeltaRigOptions { InputStartTick = 2, ServerLimit = cap });
        long pad = 0;
        int atCap = PadLengthForDatagram(cap);
        rig.At(5, () => pad = rig.SpawnEntity(5f, 5f, (w, e) => w.Set(e, new PadState { Length = 10, Fill = 1 })));
        rig.At(20, () => rig.Set(pad, new PadState { Length = atCap, Fill = 2 }));
        rig.At(25, () => rig.Set(pad, new PadState { Length = atCap + 1, Fill = 3 }));
        rig.Run();

        RigClient mover = rig[RigRole.Mover];
        Assert.Equal(DatagramPrefix, DeltaReliabilityAcceptanceTests.StateSentAt(mover, 19).Payload.Length);
        FaultSend exact = DeltaReliabilityAcceptanceTests.StateSentAt(mover, 20);
        Assert.Equal(cap, exact.Payload.Length);
        Assert.Equal(NetChannelReliability.UnreliableSequenced, exact.Reliability);
        Assert.Contains(DeltaReliabilityAcceptanceTests.IdOf(exact), mover.Accepted);

        Assert.DoesNotContain(mover.Downstream.Sends, s => s.Kind == FaultFrameKind.RebuildDelta
            && s.Subtick == DeltaFaultSchedule.Subtick(25));
        List<FaultSend> repair = mover.Downstream.Sends.Where(s => s.Kind == FaultFrameKind.KeyframeChunk
            && s.Subtick >= DeltaFaultSchedule.Subtick(25)).ToList();
        Assert.Single(repair.Select(s => DeltaReliabilityAcceptanceTests.IdOf(s)).Distinct());
        Assert.Equal(DeltaFaultSchedule.Subtick(25), repair[0].Subtick);
        Assert.All(repair, s => Assert.Equal(NetChannelReliability.ReliableOrdered, s.Reliability));
        Assert.Contains(DeltaReliabilityAcceptanceTests.IdOf(repair[0]), mover.Accepted);
        Assert.Contains(DeltaReliabilityAcceptanceTests.IdOf(DeltaReliabilityAcceptanceTests.StateSentAt(mover, 27)),
            mover.Accepted);
        AssertEverySendFits(rig, cap);
    }

    // An unknown or infeasible limit selects reliable fallback and names the transport limit.
    private static void FallsBack(bool sharded, int limit)
    {
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, Array.Empty<DeltaFault>(),
            new DeltaRigOptions { InputStartTick = 2, ServerLimit = limit });
        rig.Run();

        foreach (RigClient c in rig.Clients)
        {
            var expected = new ReplicationSelection(ReplicationDeliveryMode.LegacyReliable,
                ReplicationSelectionReason.UnavailableTransportLimit, 0);
            Assert.Equal(expected, c.Client.ReplicationSelection);
            Assert.True(rig.Host.TryGetReplicationSelection(c.Slot, out ReplicationSelection server));
            Assert.Equal(expected, server);
            FaultSend offer = Assert.Single(c.Downstream.Sends, s => s.Kind == FaultFrameKind.ReplicationMode);
            Assert.Equal(DeltaFaultSchedule.Subtick(2), offer.Subtick);
            Assert.DoesNotContain(c.Downstream.Sends, s => s.Kind is FaultFrameKind.RebuildDelta or FaultFrameKind.KeyframeChunk);
            Assert.Contains(c.Downstream.Sends, s => s.Kind == FaultFrameKind.LegacyDelta);
            Assert.True(rig.Trace.IngestsOf(c.Index).Count() > 100);
        }
    }

    // A limit drop while keyframe chunks are in flight abandons that epoch at once and renegotiates the width.
    private static void ShrinkDuringRepair(bool sharded)
    {
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, Array.Empty<DeltaFault>(),
            new DeltaRigOptions { InputStartTick = 2 });
        long pad = 0;
        rig.At(5, () => pad = rig.SpawnEntity(5f, 5f, (w, e) => w.Set(e, new PadState { Length = 10, Fill = 1 })));
        rig.At(20, () => rig.Set(pad, new PadState { Length = 3000, Fill = 2 }));
        rig.At(21, () => rig.SetServerLimit(RigRole.Mover, 400));
        rig.Run();

        RigClient mover = rig[RigRole.Mover];
        List<FaultSend> chunks = mover.Downstream.Sends.Where(s => s.Kind == FaultFrameKind.KeyframeChunk
            && s.Subtick >= DeltaFaultSchedule.Subtick(20)).ToList();
        ReplicationPacketId abandoned = DeltaReliabilityAcceptanceTests.IdOf(chunks[0]);
        List<FaultSend> first = chunks.Where(s => DeltaReliabilityAcceptanceTests.IdOf(s) == abandoned).ToList();
        Assert.Equal(4, first.Count);
        Assert.All(first, s => Assert.Equal(DeltaFaultSchedule.Subtick(20), s.Subtick));
        Assert.DoesNotContain(abandoned, mover.Accepted);

        FaultSend offer = Assert.Single(mover.Downstream.Sends, s => s.Kind == FaultFrameKind.ReplicationMode
            && s.Subtick > DeltaFaultSchedule.Subtick(2));
        Assert.Equal(DeltaFaultSchedule.Subtick(21), offer.Subtick);
        Assert.True(RebuildProtocol.TryDecodeModeOffer(offer.Payload.AsSpan(2), out ReplicationModeOffer replacement));
        Assert.Equal(400u, replacement.PacketCap);
        Assert.True(replacement.Epoch > abandoned.Epoch);
        List<FaultSend> renewed = chunks.Where(s => DeltaReliabilityAcceptanceTests.IdOf(s).Epoch == replacement.Epoch)
            .ToList();
        Assert.NotEmpty(renewed);
        Assert.All(renewed, s => Assert.True(s.Payload.Length <= 400 && s.Subtick >= DeltaFaultSchedule.Subtick(22)));
        Assert.True(renewed.Take(renewed.Count - 1).All(s => s.Payload.Length == 400));
        Assert.Contains(DeltaReliabilityAcceptanceTests.IdOf(renewed[^1]), mover.Accepted);
        Assert.Equal(377, mover.Client.RebuildStreamForTest!.ReassemblyWidth);
        foreach (FaultSend s in mover.Downstream.Sends.Where(IsStateFrame))
            Assert.True(s.Payload.Length <= (s.Subtick >= DeltaFaultSchedule.Subtick(21) ? 400 : 512),
                $"{s.Payload.Length} bytes at subtick {s.Subtick}");
        Assert.Equal(WorldConnectionState.Connected, mover.Client.ConnectionState);
    }

    // A complete keyframe object of exactly 64 KiB is served. One byte more is a typed capacity failure.
    private static void KeyframeAtLimitThenOneOver(bool sharded)
    {
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, Array.Empty<DeltaFault>(),
            new DeltaRigOptions { InputStartTick = 2 });
        // Own player: position, movement and owner state. The other player: position and movement. The pad entity:
        // position and a pad frame with a three-byte length.
        int fixedBytes = KeyframePrefix + EntryBytes + PositionFrame + MovementFrame + OwnerStateFrame
            + EntryBytes + PositionFrame + MovementFrame + EntryBytes + PositionFrame + ExtensionFrameHead + 3;
        int length = (64 * 1024) - fixedBytes;
        long pad = 0;
        rig.At(5, () => pad = rig.SpawnEntity(5f, 5f, (w, e) => w.Set(e, new PadState { Length = 10, Fill = 1 })));
        rig.At(20, () => rig.Set(pad, new PadState { Length = length, Fill = 2 }));
        rig.At(60, () => rig.Set(pad, new PadState { Length = length + 1, Fill = 3 }));
        rig.Run();

        foreach (RigClient c in rig.Clients)
        {
            List<FaultSend> chunks = c.Downstream.Sends.Where(s => s.Kind == FaultFrameKind.KeyframeChunk
                && s.Subtick >= DeltaFaultSchedule.Subtick(20)).ToList();
            ReplicationPacketId id = DeltaReliabilityAcceptanceTests.IdOf(chunks[0]);
            Assert.All(chunks, s => Assert.Equal(id, DeltaReliabilityAcceptanceTests.IdOf(s)));
            Assert.Equal(64u * 1024u, BinaryPrimitives.ReadUInt32LittleEndian(chunks[0].Payload.AsSpan(14)));
            Assert.Equal(135, chunks.Count);
            Assert.Equal(53, chunks[^1].Subtick / DeltaFaultSchedule.SubticksPerServerTick);
            Assert.Contains(id, c.Accepted);
            Assert.Equal(WorldConnectionState.Disconnected, c.Client.ConnectionState);
            Assert.Equal(DisconnectReason.ReplicationCapacityExceeded, c.Client.DisconnectReason);
            Assert.DoesNotContain(c.Downstream.Sends, s => IsStateFrame(s) && s.Subtick >= DeltaFaultSchedule.Subtick(60));
            Assert.Equal(DeltaFaultSchedule.Subtick(60) + Phase,
                rig.Trace.FramesOf(c.Index).First(r => r.State == WorldConnectionState.Disconnected).Subtick);
        }
        AssertEverySendFits(rig, 512);
    }

    // A projection of exactly the configured component frame count is served, one frame more is a capacity failure.
    private static void MetadataAtLimitThenOneOver(bool sharded)
    {
        // Each viewer holds five player frames (own position, movement and owner state, the other's position and
        // movement) plus a position and a zero-byte tag per spawned entity: 5 + 2 * 5 is the cap.
        var stream = new ReplicationStreamOptions { Limits = new DeltaRebuildOptions { MaxComponents = 15 } };
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, Array.Empty<DeltaFault>(),
            new DeltaRigOptions { InputStartTick = 2, Stream = stream });
        var tagged = new List<long>();
        rig.At(20, () =>
        {
            for (int i = 0; i < 5; i++)
                tagged.Add(rig.SpawnEntity(4f + i, 5f, (w, e) => w.Set(e, new SentinelTag())));
        });
        rig.At(30, () => rig.Set(tagged[0], new SentinelValue { Value = 1 }));
        rig.Run();

        foreach (RigClient c in rig.Clients)
        {
            Assert.Equal(15, rig.ExpectedAt(c.Role, 29).Count);
            Assert.Equal(16, rig.ExpectedAt(c.Role, 30).Count);
            Assert.Contains(DeltaReliabilityAcceptanceTests.IdOf(DeltaReliabilityAcceptanceTests.StateSentAt(c, 29)),
                c.Accepted);
            Assert.DoesNotContain(c.Downstream.Sends, s => IsStateFrame(s) && s.Subtick >= DeltaFaultSchedule.Subtick(30));
            Assert.Equal(DisconnectReason.ReplicationCapacityExceeded, c.Client.DisconnectReason);
        }
        AssertEverySendFits(rig, 512);
    }

    // Two-byte 7-bit length for these pad sizes, so the datagram is the prefix, one entry and one pad frame.
    private static int PadLengthForDatagram(int datagramBytes) =>
        datagramBytes - DatagramPrefix - EntryBytes - ExtensionFrameHead - 2;

    private static bool IsStateFrame(FaultSend s) =>
        s.Kind is FaultFrameKind.RebuildDelta or FaultFrameKind.KeyframeChunk or FaultFrameKind.ReplicationMode;

    private static void AssertEverySendFits(DeltaReliabilityRig rig, int cap)
    {
        foreach (RigClient c in rig.Clients)
            foreach (FaultSend s in c.Downstream.Sends.Where(IsStateFrame))
                Assert.True(s.Payload.Length <= cap, $"{c.Role} sent {s.Payload.Length} bytes over {cap}");
    }
}
