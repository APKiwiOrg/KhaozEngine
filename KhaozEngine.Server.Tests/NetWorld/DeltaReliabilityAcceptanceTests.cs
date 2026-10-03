using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Tests.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Finite loss, reorder and acknowledgement schedules on both server heads, plus the fault transport's own contract.
/// Each schedule names its faults by ordinal against the join timeline documented on <see cref="DeltaFaultSchedule"/>
/// and asserts the tick each fault matched, so a drifted timeline fails rather than silently testing another packet.
/// Every case runs at phase 1, so commands are in flight when state arrives.
/// </summary>
public sealed class DeltaReliabilityAcceptanceTests
{
    private const int Phase = 1;
    private const NetChannelReliability Reliable = NetChannelReliability.ReliableOrdered;
    private const NetChannelReliability Unreliable = NetChannelReliability.UnreliableSequenced;
    private static readonly NetConnectionId ServerId = new(1);

    public static TheoryData<bool> Heads => new() { false, true };

    public static TheoryData<bool, bool> HeadsByCase => new() { { false, false }, { false, true }, { true, false }, { true, true } };

    [Fact]
    public void FaultTransportCountsOrdinalsPerDirectionAndFamily()
    {
        var hub = new InMemoryTransportHub();
        INetTransport client = hub.CreateClient();
        var up = new DeltaFaultTransport(client, FaultDirection.ClientToServer,
            new[] { DeltaFaultSchedule.DropRoutineAck(2) }, 0);
        byte[] markerMove = MoveProtocol.EncodeMove(0x0000A2C5, new MoveCommand(Vector2.UnitY, false, 0f));
        Assert.Equal((byte)0xA2, markerMove[1]);
        up.Send(ServerId, Data(markerMove), Reliable);
        up.Send(ServerId, Data(RebuildProtocol.EncodeAck(new ReplicationPacketId(1, 5))), Unreliable);
        up.Send(ServerId, Data(RebuildProtocol.EncodeAck(new ReplicationPacketId(1, 6))), Unreliable);
        up.Send(ServerId, Data(RebuildProtocol.EncodeAck(new ReplicationPacketId(1, 6))), Reliable);
        up.Send(ServerId, Data(MoveProtocol.EncodeReplicationAck(3)), Reliable);
        up.Send(ServerId, Data([.. RebuildProtocol.EncodeAck(new ReplicationPacketId(1, 7)), 0]), Unreliable);
        up.Send(ServerId, Data(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.DeltaCapable)), Reliable);
        up.Send(ServerId, Data(RebuildProtocol.EncodeAck(new ReplicationPacketId(1, 8))), Unreliable);
        Assert.Equal(
            new (byte, int)[]
            {
                (FaultFrameKind.Move, 1), (FaultFrameKind.RoutineAck, 1), (FaultFrameKind.RoutineAck, 2),
                (FaultFrameKind.KeyframeAck, 1), (FaultFrameKind.LegacyAck, 1), (FaultFrameKind.Unclassified, 0),
                (FaultFrameKind.Control, 1), (FaultFrameKind.RoutineAck, 3),
            },
            up.Sends.Select(s => (s.Kind, s.Ordinal)));
        Assert.Equal(7, up.Forwards.Count);
        Assert.DoesNotContain(up.Forwards, f => f.Kind == FaultFrameKind.RoutineAck && f.Ordinal == 2);
        Assert.Empty(up.Unmatched);

        var down = new DeltaFaultTransport(hub.Server, FaultDirection.ServerToClient,
            new[] { DeltaFaultSchedule.DropState(2) }, 0)
        {
            Connection = new NetConnectionId(1),
        };
        down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.RebuildDelta, 1), Unreliable);
        down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.Notice, 2), Reliable);
        down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.RebuildDelta, 3), Unreliable);
        down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.RebuildDelta, 4), Unreliable);
        Assert.Equal(new[] { 1, 1, 2, 3 }, down.Sends.Select(s => s.Ordinal));
        Assert.Equal(new byte[] { 1, 2, 4 }, ReceivedMarks(client));
    }

    [Fact]
    public void FaultTransportKeepsReliableOrderBehindADelayedFrame()
    {
        var hub = new InMemoryTransportHub();
        INetTransport client = hub.CreateClient();
        var fault = new DeltaFault(1, FaultDirection.ServerToClient, FaultFrameKind.GameMessage, FaultAction.Delay, 10);
        var down = new DeltaFaultTransport(hub.Server, FaultDirection.ServerToClient, new[] { fault }, 0);
        down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.GameMessage, 1), Reliable);
        down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.Notice, 2), Reliable);
        down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.RebuildDelta, 3), Unreliable);
        down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.KeyframeChunk, 4), Reliable);
        Assert.Equal(3, down.HeldCount);
        down.AdvanceTo(9);
        Assert.Equal(new byte[] { 3 }, ReceivedMarks(client));
        down.AdvanceTo(10);
        Assert.Equal(new byte[] { 1, 2, 4 }, ReceivedMarks(client));
        Assert.Equal(0, down.HeldCount);
    }

    [Fact]
    public void FaultTransportDuplicatesUnreliableDataAndBoundsRelease()
    {
        var hub = new InMemoryTransportHub();
        INetTransport client = hub.CreateClient();
        var down = new DeltaFaultTransport(hub.Server, FaultDirection.ServerToClient,
            new[] { DeltaFaultSchedule.DuplicateState(1, 3), DeltaFaultSchedule.DelayState(2, 8) }, 0);
        down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.RebuildDelta, 1), Unreliable);
        down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.RebuildDelta, 2), Unreliable);
        Assert.Equal(new byte[] { 1 }, ReceivedMarks(client));
        down.AdvanceTo(3);
        Assert.Equal(new byte[] { 1 }, ReceivedMarks(client));
        down.AdvanceTo(7);
        Assert.Empty(ReceivedMarks(client));
        down.AdvanceTo(8);
        Assert.Equal(new byte[] { 2 }, ReceivedMarks(client));
        Assert.Equal(0, down.HeldCount);
        Assert.Equal(2, down.MaxHeldCount);
    }

    [Fact]
    public void FaultTransportDeliversHeldDataBeforeTheTerminalEvent()
    {
        var hub = new InMemoryTransportHub();
        INetTransport client = hub.CreateClient();
        var fault = new DeltaFault(1, FaultDirection.ServerToClient, FaultFrameKind.Notice, FaultAction.Delay, 40);
        var down = new DeltaFaultTransport(hub.Server, FaultDirection.ServerToClient, new[] { fault }, 0);
        down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.Notice, 1), Reliable);
        down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.Notice, 2), Reliable);
        down.Disconnect(new NetConnectionId(1));
        client.Poll();
        var events = new List<NetEventType>();
        var marks = new List<byte>();
        while (client.TryDequeueEvent(out NetEvent ev))
        {
            events.Add(ev.Type);
            if (ev.Type == NetEventType.Data) marks.Add(ev.Data[^1]);
        }
        Assert.Equal(new[] { NetEventType.Connected, NetEventType.Data, NetEventType.Data, NetEventType.Disconnected },
            events);
        Assert.Equal(new byte[] { 1, 2 }, marks);
        Assert.Equal(0, down.HeldCount);
    }

    [Fact]
    public void FaultTransportRejectsSixtyFiveQueuedFramesAndLongOrdinaryDelay()
    {
        var hub = new InMemoryTransportHub();
        hub.CreateClient();
        var fault = new DeltaFault(1, FaultDirection.ServerToClient, FaultFrameKind.Notice, FaultAction.Delay, 200);
        var down = new DeltaFaultTransport(hub.Server, FaultDirection.ServerToClient, new[] { fault }, 0);
        for (int i = 0; i < DeltaFaultSchedule.MaxQueuedFrames; i++)
            down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.Notice, (byte)i), Reliable);
        Assert.Equal(64, down.HeldCount);
        InvalidOperationException overflow = Assert.Throws<InvalidOperationException>(() =>
            down.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.Notice, 65), Reliable));
        Assert.Contains("fixture error", overflow.Message, StringComparison.OrdinalIgnoreCase);

        var late = new DeltaFaultTransport(hub.Server, FaultDirection.ServerToClient,
            new[] { DeltaFaultSchedule.DelayState(1, 9) }, 0);
        Assert.Throws<InvalidOperationException>(() =>
            late.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.RebuildDelta, 1), Unreliable));
        Assert.Throws<InvalidOperationException>(() => DeltaFaultSchedule.Validate(
            new[] { new DeltaFault(1, FaultDirection.ServerToClient, FaultFrameKind.KeyframeChunk, FaultAction.Drop, 0) }));
        var dropReliable = new DeltaFaultTransport(hub.Server, FaultDirection.ServerToClient,
            new[] { new DeltaFault(1, FaultDirection.ServerToClient, FaultFrameKind.GameMessage, FaultAction.Drop, 0) }, 0);
        Assert.Throws<InvalidOperationException>(() =>
            dropReliable.Send(new NetConnectionId(1), ServerFrame(FaultFrameKind.GameMessage, 1), Reliable));
    }

    [Fact]
    public void UnreliableGameFrameMaySuppressASequencedAck()
    {
        // The backend's shared sequenced channel can let a newer consumer frame supersede a routine ack. The finite
        // table models that as a drop of the ack while the game frame is delivered.
        var hub = new InMemoryTransportHub();
        INetTransport client = hub.CreateClient();
        var up = new DeltaFaultTransport(client, FaultDirection.ClientToServer,
            new[] { DeltaFaultSchedule.DropRoutineAck(1) }, 0);
        up.Send(ServerId, Data(RebuildProtocol.EncodeAck(new ReplicationPacketId(1, 1))), Unreliable);
        up.Send(ServerId, Data(MoveProtocol.EncodeGameMessage(7, new byte[] { 9 })), Unreliable);
        Assert.Equal(new[] { FaultFrameKind.ClientGameMessage }, up.Forwards.Select(f => f.Kind));
        Assert.Empty(up.Unmatched);
    }

    [Theory]
    [MemberData(nameof(Heads))]
    public void ReliableDefaultDelayedAckRestoresAllEdges(bool sharded)
    {
        // Legacy ack ordinal n answers the delta sent at tick n + 1: ordinal 8 answers tick 9, held to tick 13.
        var hold = new DeltaFault(8, FaultDirection.ClientToServer, FaultFrameKind.LegacyAck, FaultAction.Delay,
            DeltaFaultSchedule.Subtick(13));
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, new[] { hold },
            new DeltaRigOptions { ServerUnreliable = false, ClientUnreliable = false, InputStartTick = 2 });
        SentinelScene scene = SentinelScene.Install(rig, tick12: true);
        rig.Run();

        RigClient mover = rig[RigRole.Mover];
        FaultSend held = Assert.Single(mover.Upstream!.Sends, s => s.Fault is not null);
        FaultSend tick9 = Assert.Single(mover.Downstream.Sends,
            s => s.Kind == FaultFrameKind.LegacyDelta && s.Subtick == DeltaFaultSchedule.Subtick(9));
        Assert.True(MoveProtocol.TryDecodeReplicationAck(held.Payload.AsSpan(1), out int ackedSeq));
        Assert.Equal(LegacyHeader(tick9).Snapshot, ackedSeq);
        Assert.Equal(DeltaFaultSchedule.Subtick(13), mover.Upstream.Forwards.Single(f => f.Ordinal == 8
            && f.Kind == FaultFrameKind.LegacyAck).Subtick);

        // Every legacy header names the projection sent just before it, whatever the acknowledgement lag.
        int previous = -1;
        foreach (FaultSend delta in mover.Downstream.Sends.Where(s => s.Kind == FaultFrameKind.LegacyDelta))
        {
            LegacyDeltaHeader header = LegacyHeader(delta);
            Assert.Equal(previous, header.Baseline);
            previous = header.Snapshot;
        }
        scene.AssertEdgesAt(rig, RigRole.Mover, intermediateTick: 10, revertTick: 11);
        Assert.Equal(WorldConnectionState.Connected, mover.Client.ConnectionState);
    }

    [Theory]
    [MemberData(nameof(HeadsByCase))]
    public void AckRelativeEmptyDeltaRestoresAllEdges(bool sharded, bool deliverIntermediate)
    {
        DeltaFault fault = deliverIntermediate
            ? DeltaFaultSchedule.DropRoutineAck(DeltaFaultSchedule.RoutineAckOrdinalFor(10))
            : DeltaFaultSchedule.DropState(DeltaFaultSchedule.StateOrdinalSentAt(10));
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, new[] { fault },
            new DeltaRigOptions { InputStartTick = 2 });
        SentinelScene scene = SentinelScene.Install(rig, tick12: false);
        rig.Run();

        RigClient mover = rig[RigRole.Mover];
        FaultSend tick9 = StateSentAt(mover, 9), tick10 = StateSentAt(mover, 10), tick11 = StateSentAt(mover, 11);
        RebuildWireHeader reverted = Header(tick11);
        Assert.Equal(Header(tick9).Snapshot, reverted.Baseline);
        Assert.Equal(0, reverted.RemovedCount);
        Assert.Equal(0, reverted.ChangedCount);
        Assert.True(Header(tick10).ChangedCount > 0);
        ReplicationPacketId intermediate = IdOf(tick10);
        if (deliverIntermediate)
        {
            FaultSend dropped = Assert.Single(mover.Upstream!.Sends, s => s.Fault is not null);
            Assert.Equal(DeltaFaultSchedule.Subtick(10) + 3, dropped.Subtick);
            Assert.Equal(intermediate, AckedId(dropped));
            Assert.Contains(intermediate, mover.Accepted);
            scene.AssertIntermediateSeen(rig, RigRole.Mover, intermediate);
        }
        else
        {
            Assert.Equal(DeltaFaultSchedule.Subtick(10),
                Assert.Single(mover.Downstream.Sends, s => s.Fault is not null).Subtick);
            Assert.DoesNotContain(intermediate, mover.Accepted);
        }
        Assert.Contains(IdOf(tick11), mover.Accepted);
        scene.AssertEdgesAt(rig, RigRole.Mover, intermediateTick: 10, revertTick: 11);
        rig.Trace.AssertMonotonicAccepted(mover.Index);
    }

    [Theory]
    [MemberData(nameof(HeadsByCase))]
    public void Ordinal4After5AndDuplicate5NeverReingests(bool sharded, bool wrap)
    {
        DeltaFault[] faults =
        {
            DeltaFaultSchedule.DelayState(4, DeltaFaultSchedule.Subtick(7) + DeltaFaultSchedule.MaxOrdinaryDelaySubticks),
            DeltaFaultSchedule.DuplicateState(5, DeltaFaultSchedule.Subtick(8) + 2),
        };
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, faults, new DeltaRigOptions
        {
            InputStartTick = 2,
            MoverKeyframeSequence = wrap ? uint.MaxValue - 4u : null,
        });
        RigClient mover = rig[RigRole.Mover];
        ulong firstEpoch = 0;
        uint firstEpochLast = 0;
        rig.At(12, () =>
        {
            ReplicationPacketId latest = mover.Client.DeltaRebuildForTest!.LatestAcceptedId!.Value;
            firstEpoch = latest.Epoch;
            firstEpochLast = latest.Sequence;
            rig.SetServerLimit(RigRole.Mover, 480);
        });
        // Two hand-built datagrams, each alone in a poll: a half-range sequence in the live epoch, then a newer
        // sequence in the epoch the limit change retired.
        rig.AtSubtick(DeltaFaultSchedule.Subtick(20) + 2, () =>
        {
            ReplicationPacketId live = IdOf(StateSentAt(mover, 20));
            mover.Downstream.InjectAt(mover.Connection, Datagram(mover.Client.LocalNetId, 100_000, live.Epoch,
                unchecked(live.Sequence + 0x8000_0000u), live.Sequence), Unreliable, rig.SubtickNow);
        });
        rig.AtSubtick(DeltaFaultSchedule.Subtick(21) + 2, () =>
            mover.Downstream.InjectAt(mover.Connection, Datagram(mover.Client.LocalNetId, 100_000, firstEpoch,
                firstEpochLast + 1, firstEpochLast), Unreliable, rig.SubtickNow));
        rig.Run();

        FaultSend d4 = StateSentAt(mover, 7), d5 = StateSentAt(mover, 8);
        Assert.Equal(4, d4.Ordinal);
        Assert.Equal(5, d5.Ordinal);
        if (wrap)
        {
            Assert.Equal(uint.MaxValue, IdOf(d4).Sequence);
            Assert.Equal(0u, IdOf(d5).Sequence);
        }
        Assert.True(Envelope(d4).MovementAck < Envelope(d5).MovementAck);
        Assert.DoesNotContain(IdOf(d4), mover.Accepted);
        Assert.Contains(IdOf(d5), mover.Accepted);

        List<DeltaFrameRow> rows = rig.Trace.FramesOf(mover.Index).ToList();
        DeltaFrameRow accept5 = RowAt(rows, DeltaFaultSchedule.Subtick(8) + 1);
        Assert.Equal(1, accept5.IngestsThisPoll);
        Assert.Equal(IdOf(d5), accept5.AcceptedId);
        // The duplicate of 5 arrives alone. Ordinal 4 arrives with ordinal 6 and only 6 is ingested.
        AssertNothingIngested(RowAt(rows, DeltaFaultSchedule.Subtick(8) + 3), PreviousRow(rows, DeltaFaultSchedule.Subtick(8) + 3));
        DeltaFrameRow release4 = RowAt(rows, DeltaFaultSchedule.Subtick(9) + 1);
        Assert.Equal(2, release4.DeliveredThisPoll);
        Assert.Equal(1, release4.IngestsThisPoll);
        Assert.Equal(IdOf(StateSentAt(mover, 9)), release4.AcceptedId);
        Assert.Equal(Envelope(StateSentAt(mover, 9)).MovementAck, release4.MovementAck);

        // The limit change retired the first epoch. Both hand-built datagrams are ignored without ingest.
        Assert.True(rows[^1].Selection.Epoch > firstEpoch);
        foreach (int subtick in new[] { DeltaFaultSchedule.Subtick(20) + 3, DeltaFaultSchedule.Subtick(21) + 3 })
        {
            DeltaFrameRow row = RowAt(rows, subtick);
            Assert.Equal(1, row.DeliveredThisPoll);
            AssertNothingIngested(row, PreviousRow(rows, subtick));
        }
        rig.Trace.AssertMonotonicAccepted(mover.Index);
        Assert.Equal(WorldConnectionState.Connected, mover.Client.ConnectionState);
        Assert.Equal(DisconnectReason.None, mover.Client.DisconnectReason);
    }

    [Theory]
    [MemberData(nameof(Heads))]
    public void LostAndReorderedAcksRemainBounded(bool sharded)
    {
        // Ack ordinal n advertises the datagram before it and reaches the server at tick n + 3. Ordinals 1 to 3 are
        // lost, 4 is released behind 5, then 7 to 37 are lost: the server's baseline stays on datagram 5 while the
        // datagrams sent at ticks 9 to 39 make exactly 31 new committed sends.
        var faults = new List<DeltaFault>(DeltaFaultSchedule.DropRoutineAcks(1, 3))
        {
            DeltaFaultSchedule.DelayRoutineAck(4, (4 * 4) + 11 + DeltaFaultSchedule.MaxOrdinaryDelaySubticks),
        };
        faults.AddRange(DeltaFaultSchedule.DropRoutineAcks(7, 37));
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, faults);
        RigClient mover = rig[RigRole.Mover];
        var acknowledged = new Dictionary<int, ReplicationPacketId?>();
        var newSent = new Dictionary<int, int>();
        for (int tick = 5; tick <= 45; tick++)
        {
            int after = tick - 1;
            rig.At(tick, () =>
            {
                RebuildUsage usage = rig.Writer.RebuildUsageForTest(mover.Slot);
                acknowledged[after] = usage.AcknowledgedId;
                newSent[after] = usage.NewSentCount;
                Assert.True(usage.RetainedCount <= 32, $"tick {after}: {usage.RetainedCount} retained");
                Assert.True(usage.RetainedBytes <= 2 * 1024 * 1024);
            });
        }
        rig.Run();

        ReplicationPacketId keyframe = mover.Downstream.Sends
            .Where(s => s.Kind == FaultFrameKind.KeyframeChunk).Select(s => ChunkId(s)).First();
        Assert.Equal((4 * 4) + 11, mover.Upstream!.Sends.Single(s => s.Ordinal == 4
            && s.Kind == FaultFrameKind.RoutineAck).Subtick);
        for (int tick = 4; tick <= 7; tick++) Assert.Equal(keyframe, acknowledged[tick]);
        Assert.Equal(IdOf(StateSentAt(mover, 7)), acknowledged[8]);
        Assert.Equal(IdOf(StateSentAt(mover, 8)), acknowledged[9]);
        for (int tick = 9; tick <= 39; tick++)
        {
            Assert.Equal(IdOf(StateSentAt(mover, 8)), acknowledged[tick]);
            Assert.Equal(tick - 8, newSent[tick]);
        }
        Assert.Equal(31, newSent[39]);

        // Exactly one fresh reliable barrier, at tick 40, and steady state resumes in the new epoch.
        ulong[] epochs = mover.Downstream.Sends.Where(s => s.Kind == FaultFrameKind.KeyframeChunk)
            .Select(s => ChunkId(s).Epoch).Distinct().ToArray();
        Assert.Equal(2, epochs.Length);
        FaultSend repair = mover.Downstream.Sends.First(s => s.Kind == FaultFrameKind.KeyframeChunk
            && ChunkId(s).Epoch == epochs[1]);
        Assert.Equal(DeltaFaultSchedule.Subtick(40), repair.Subtick);
        Assert.Equal(Reliable, repair.Reliability);
        Assert.DoesNotContain(mover.Downstream.Sends, s => s.Kind == FaultFrameKind.RebuildDelta
            && s.Subtick == DeltaFaultSchedule.Subtick(40));
        Assert.Equal(epochs[1], IdOf(StateSentAt(mover, 41)).Epoch);
        Assert.Contains(IdOf(StateSentAt(mover, 41)), mover.Accepted);
        Assert.Contains(ChunkId(repair), mover.Accepted);
        rig.Trace.AssertMaxima(mover.Index, new DeltaRebuildOptions(), 512);
        rig.Trace.AssertMonotonicAccepted(mover.Index);
        Assert.Equal(WorldConnectionState.Connected, mover.Client.ConnectionState);
    }

    [Theory]
    [MemberData(nameof(Heads))]
    public void PrunedClientBaselineIsRecoverable(bool sharded)
    {
        var limits = new DeltaRebuildOptions { MaxRetainedProjections = 4 };
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, Array.Empty<DeltaFault>(),
            new DeltaRigOptions { Stream = new ReplicationStreamOptions { Limits = limits } });
        RigClient mover = rig[RigRole.Mover];
        ReplicationPacketId pruned = default;
        IReadOnlyList<ProjectionEntry>? liveBefore = null;
        int injectAt = DeltaFaultSchedule.Subtick(20) + 2;
        rig.AtSubtick(injectAt, () =>
        {
            // The datagram sent at tick 11 was built on the one sent at tick 10, a real server baseline the client
            // has since pruned. Force it as the baseline of a newer sequence.
            pruned = IdOf(StateSentAt(mover, 10));
            Assert.Equal(pruned.Sequence, Header(StateSentAt(mover, 11)).Baseline);
            Assert.False(mover.Client.DeltaRebuildForTest!.TryGetRetainedForTest(pruned, out _));
            ReplicationPacketId live = IdOf(StateSentAt(mover, 20));
            mover.Downstream.InjectAt(mover.Connection, Datagram(mover.Client.LocalNetId, 100_000, live.Epoch,
                live.Sequence + 1000u, pruned.Sequence), Unreliable, rig.SubtickNow);
            liveBefore = rig.LiveProjection(RigRole.Mover);
        });
        rig.AfterPoll += (c, subtick) =>
        {
            if (c.Role != RigRole.Mover || subtick != injectAt + 1) return;
            rig.Compare(liveBefore!, rig.LiveProjection(RigRole.Mover), "live world after a missing baseline");
            Assert.True(c.Client.RebuildStreamForTest!.RecoveryActive);
        };
        rig.Run();

        List<DeltaFrameRow> rows = rig.Trace.FramesOf(mover.Index).ToList();
        AssertNothingIngested(RowAt(rows, injectAt + 1), PreviousRow(rows, injectAt + 1));
        FaultSend request = Assert.Single(mover.Upstream!.Sends, s => s.Kind == FaultFrameKind.Repair);
        Assert.Equal(injectAt + 1, request.Subtick);
        Assert.Equal(Reliable, request.Reliability);
        Assert.Equal(ControlReadResult.Valid, RebuildProtocol.DecodeClientControl(request.Payload.AsSpan(1),
            out RebuildClientControl control));
        Assert.Equal(pruned.Sequence, control.MissingBaselineSequence);
        Assert.Equal(IdOf(StateSentAt(mover, 20)).Sequence, control.SnapshotSequence);

        // The server repairs at the next tick in a fresh epoch, and the client recovers through it.
        FaultSend repair = mover.Downstream.Sends.Last(s => s.Kind == FaultFrameKind.KeyframeChunk);
        Assert.Equal(DeltaFaultSchedule.Subtick(21), repair.Subtick);
        Assert.True(ChunkId(repair).Epoch > pruned.Epoch);
        Assert.Contains(ChunkId(repair), mover.Accepted);
        Assert.Contains(IdOf(StateSentAt(mover, 22)), mover.Accepted);
        rig.Trace.AssertMaxima(mover.Index, limits, 512);
        Assert.All(rows, r => Assert.True(r.ClientRetainedCount <= 4));
        Assert.Equal(WorldConnectionState.Connected, mover.Client.ConnectionState);
        Assert.Equal(DisconnectReason.None, mover.Client.DisconnectReason);
    }

    internal static byte[] Data(byte[] body) => SessionFrame.Write(SessionOpcode.Data, body);

    private static byte[] ServerFrame(byte kind, byte mark) => Data(new byte[] { kind, 0, 0, mark });

    private static List<byte> ReceivedMarks(INetTransport client)
    {
        client.Poll();
        var marks = new List<byte>();
        while (client.TryDequeueEvent(out NetEvent ev))
            if (ev.Type == NetEventType.Data) marks.Add(ev.Data[^1]);
        return marks;
    }

    internal static FaultSend StateSentAt(RigClient c, int tick) => Assert.Single(c.Downstream.Sends,
        s => s.Kind == FaultFrameKind.RebuildDelta && s.Subtick == DeltaFaultSchedule.Subtick(tick));

    internal static ReplicationPacketId IdOf(FaultSend send)
    {
        Assert.True(DeltaReliabilityRig.TryReadStateId(send.Kind, send.Payload, out ReplicationPacketId id, out _, out _));
        return id;
    }

    internal static ReplicationPacketId ChunkId(FaultSend send) => IdOf(send);

    internal static RebuildWireHeader Header(FaultSend send) =>
        RebuildWire.ReadHeader(send.Payload.AsSpan(2 + RebuildProtocol.EnvelopeBytes));

    internal static (long LocalNetId, int MovementAck) Envelope(FaultSend send) =>
        (BinaryPrimitives.ReadInt64LittleEndian(send.Payload.AsSpan(2)),
            BinaryPrimitives.ReadInt32LittleEndian(send.Payload.AsSpan(10)));

    private static LegacyDeltaHeader LegacyHeader(FaultSend send) =>
        LegacyDeltaWire.ReadHeader(send.Payload.AsSpan(2 + 12));

    private static ReplicationPacketId AckedId(FaultSend send)
    {
        Assert.Equal(ControlReadResult.Valid, RebuildProtocol.DecodeClientControl(send.Payload.AsSpan(1),
            out RebuildClientControl control));
        return new ReplicationPacketId(control.Epoch, control.SnapshotSequence);
    }

    /// <summary>A hand-built empty state datagram as the server would frame it, session byte included.</summary>
    internal static byte[] Datagram(long localNetId, int movementAck, ulong epoch, uint sequence, uint baseline)
    {
        var frame = new byte[2 + RebuildProtocol.EnvelopeBytes + RebuildWire.HeaderBytes + 8];
        frame[0] = (byte)SessionOpcode.Data;
        frame[1] = FaultFrameKind.RebuildDelta;
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(2), localNetId);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(10), movementAck);
        Span<byte> body = frame.AsSpan(2 + RebuildProtocol.EnvelopeBytes);
        body[0] = RebuildProtocol.Format;
        BinaryPrimitives.WriteUInt64LittleEndian(body[2..], epoch);
        BinaryPrimitives.WriteUInt32LittleEndian(body[10..], sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body[14..], baseline);
        return frame;
    }

    internal static DeltaFrameRow RowAt(List<DeltaFrameRow> rows, int subtick) =>
        Assert.Single(rows, r => r.Subtick == subtick);

    internal static DeltaFrameRow PreviousRow(List<DeltaFrameRow> rows, int subtick) =>
        rows.Last(r => r.Subtick < subtick);

    /// <summary>The poll changed nothing a stale or rejected packet could touch: no ingest, accept, movement ack or
    /// pending command change, and the session is still connected.</summary>
    internal static void AssertNothingIngested(DeltaFrameRow row, DeltaFrameRow previous)
    {
        Assert.Equal(0, row.IngestsThisPoll);
        Assert.Equal(previous.IngestCount, row.IngestCount);
        Assert.Equal(previous.AcceptedCount, row.AcceptedCount);
        Assert.Equal(previous.AcceptedId, row.AcceptedId);
        Assert.Equal(previous.MovementAck, row.MovementAck);
        Assert.Equal(row.PendingBeforePoll, row.PendingAfterPoll);
        Assert.Equal(WorldConnectionState.Connected, row.State);
    }
}

/// <summary>
/// The presence and value edges every reversion case drives on one viewer: a sentinel entity whose value goes 1, 2, 1,
/// whose flags turn on and off, whose zero-byte tag toggles and whose padding is removed and re-added with its original
/// bytes, one entity that leaves the interest radius and re-enters unchanged, one that enters and leaves, and an
/// owner-only value on the viewer's own player that goes 1, 2, 1. A persist and migrate sentinel rides along and must
/// never be served.
/// </summary>
internal sealed class SentinelScene
{
    public const byte PadFill = 0x41;
    public const int PadLength = 3;

    public long Sentinel { get; private set; }
    public long Leaver { get; private set; }
    public long Enterer { get; private set; }

    public static SentinelScene Install(DeltaReliabilityRig rig, bool tick12)
    {
        var scene = new SentinelScene();
        rig.At(5, () =>
        {
            scene.Sentinel = rig.SpawnEntity(5f, 5f, (w, e) =>
            {
                w.Set(e, new SentinelValue { Value = 1 });
                w.Set(e, new SentinelFlag { Flags = 0 });
                w.Set(e, new PadState { Length = PadLength, Fill = PadFill });
                w.Set(e, new SentinelServerOnly { Pattern = 0x5E5E_5E5E_5E5E_5E5E });
            });
            scene.Leaver = rig.SpawnEntity(6f, 4f, (w, e) => w.Set(e, new SentinelValue { Value = 7 }));
            scene.Enterer = rig.SpawnEntity(40f, 42f, (w, e) => w.Set(e, new SentinelValue { Value = 9 }));
            rig.SetOnPlayer(RigRole.Mover, new SentinelOwner { Pattern = 1 });
        });
        rig.At(10, () =>
        {
            rig.Set(scene.Sentinel, new SentinelValue { Value = 2 });
            rig.Set(scene.Sentinel, new SentinelFlag { Flags = 1 });
            rig.Set(scene.Sentinel, new SentinelTag());
            rig.Remove<PadState>(scene.Sentinel);
            rig.MoveEntity(scene.Leaver, 40f, 40f);
            rig.MoveEntity(scene.Enterer, 4f, 6f);
            rig.SetOnPlayer(RigRole.Mover, new SentinelOwner { Pattern = 2 });
        });
        rig.At(11, () =>
        {
            rig.Set(scene.Sentinel, new SentinelValue { Value = 1 });
            rig.Set(scene.Sentinel, new SentinelFlag { Flags = 0 });
            rig.Remove<SentinelTag>(scene.Sentinel);
            rig.Set(scene.Sentinel, new PadState { Length = PadLength, Fill = PadFill });
            rig.MoveEntity(scene.Leaver, 6f, 4f);
            rig.MoveEntity(scene.Enterer, 40f, 42f);
            rig.SetOnPlayer(RigRole.Mover, new SentinelOwner { Pattern = 1 });
        });
        if (tick12) rig.At(12, () => rig.Set(scene.Sentinel, new SentinelFlag { Flags = 2 }));
        return scene;
    }

    /// <summary>The reference projections hold every edge at the intermediate and reverted ticks, the viewer ingested
    /// both, and its public component reads end on the reverted state.</summary>
    public void AssertEdgesAt(DeltaReliabilityRig rig, RigRole viewer, int intermediateTick, int revertTick)
    {
        long own = rig[viewer].Client.LocalNetId;
        IReadOnlyList<ProjectionEntry> mid = rig.ExpectedAt(viewer, intermediateTick);
        IReadOnlyList<ProjectionEntry> back = rig.ExpectedAt(viewer, revertTick);
        Assert.Equal(BitConverter.GetBytes(2), Payload(mid, Sentinel, DeltaRigRegistry.ValueTypeId));
        Assert.Equal(new byte[] { 1 }, Payload(mid, Sentinel, DeltaRigRegistry.FlagTypeId));
        Assert.Equal(Array.Empty<byte>(), Payload(mid, Sentinel, DeltaRigRegistry.TagTypeId));
        Assert.Null(Payload(mid, Sentinel, Pad.TypeId));
        Assert.DoesNotContain(mid, e => e.NetId == Leaver);
        Assert.Contains(mid, e => e.NetId == Enterer);
        Assert.Equal(BitConverter.GetBytes(2L), Payload(mid, own, DeltaRigRegistry.OwnerTypeId));
        AssertReverted(back, own);
        Assert.DoesNotContain(mid.Concat(back), e => e.TypeId == DeltaRigRegistry.ServerOnlyTypeId);

        WorldClient client = rig[viewer].Client;
        Assert.True(client.TryGetComponent(Sentinel, out SentinelValue value));
        Assert.Equal(1, value.Value);
        Assert.False(client.TryGetComponent<SentinelTag>(Sentinel, out _));
        Assert.True(client.TryGetComponent(Sentinel, out PadState pad));
        Assert.Equal((PadLength, PadFill), (pad.Length, pad.Fill));
        Assert.True(client.TryGetComponent(Leaver, out SentinelValue leaver));
        Assert.Equal(7, leaver.Value);
        Assert.False(client.TryGetComponent<SentinelValue>(Enterer, out _));
        Assert.True(client.TryGetComponent(own, out SentinelOwner owner));
        Assert.Equal(1L, owner.Pattern);
        Assert.False(client.TryGetComponent<SentinelServerOnly>(Sentinel, out _));
        Assert.Contains(rig.Trace.IngestsOf(rig[viewer].Index), r => r.SourceTick == revertTick);
    }

    /// <summary>The viewer ingested the intermediate state and showed it.</summary>
    public void AssertIntermediateSeen(DeltaReliabilityRig rig, RigRole viewer, ReplicationPacketId id)
    {
        DeltaIngestRow row = Assert.Single(rig.Trace.IngestsOf(rig[viewer].Index), r => r.Id == id);
        Assert.Equal(10, row.SourceTick);
    }

    private void AssertReverted(IReadOnlyList<ProjectionEntry> projection, long own)
    {
        Assert.Equal(BitConverter.GetBytes(1), Payload(projection, Sentinel, DeltaRigRegistry.ValueTypeId));
        Assert.Equal(new byte[] { 0 }, Payload(projection, Sentinel, DeltaRigRegistry.FlagTypeId));
        Assert.Null(Payload(projection, Sentinel, DeltaRigRegistry.TagTypeId));
        Assert.Equal(Enumerable.Repeat(PadFill, PadLength).ToArray(), Payload(projection, Sentinel, Pad.TypeId));
        Assert.Contains(projection, e => e.NetId == Leaver);
        Assert.DoesNotContain(projection, e => e.NetId == Enterer);
        Assert.Equal(BitConverter.GetBytes(1L), Payload(projection, own, DeltaRigRegistry.OwnerTypeId));
    }

    private static byte[]? Payload(IReadOnlyList<ProjectionEntry> projection, long netId, ushort typeId) =>
        projection.Where(e => e.NetId == netId && e.TypeId == typeId).Select(e => e.Payload).SingleOrDefault();
}

