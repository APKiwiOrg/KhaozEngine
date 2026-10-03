using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Tests.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Viewer-scoped bytes under faults on both heads: owner-only and persist-only sentinels, a visibility table flipped
/// inside the fault window, a shard boundary crossing, slot reuse with a retired epoch replayed into the new session,
/// and a visibility change while a keyframe is frozen. The rig asserts every served interest set equals its own
/// visibility table and positions, so the table is checked against ghosts as well as owned entities.
/// </summary>
public sealed class DeltaVisibilityAcceptanceTests
{
    private const int Phase = 1;
    private const long MoverPattern = 0x0D15_EA5E_0000_0001;
    private const long ObserverPattern = 0x0D15_EA5E_0000_0002;
    private const long ReplacementPattern = 0x0D15_EA5E_0000_0003;
    private const long ServerOnlyPattern = 0x5E5E_0000_5E5E_0004;
    private const byte HiddenFill = 0x5B;

    public static TheoryData<bool> Heads => new() { false, true };

    [Theory]
    [MemberData(nameof(Heads))]
    public void OwnerVisibilityHandoffAndSlotReuseRemainScoped(bool sharded)
    {
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, Array.Empty<DeltaFault>(),
            new DeltaRigOptions
            {
                InputStartTick = 2,
                UseVisibilityTable = true,
                MoverSpawn = new Vector3(2f, 0f, 2f),
                ReplaceObserverAtTick = 40,
            });
        RigClient mover = rig[RigRole.Mover], observer = rig[RigRole.Observer];
        long hidden = 0, moverId = 0, observerId = 0;
        rig.At(5, () =>
        {
            moverId = rig.NetIdOf(RigRole.Mover);
            observerId = rig.NetIdOf(RigRole.Observer);
            // In cell (0, -1), so on the sharded head both viewers see it as a ghost of their home cell (0, 0).
            hidden = rig.SpawnEntity(5f, -3f, (w, e) =>
            {
                w.Set(e, new PadState { Length = 8, Fill = 0x5A });
                w.Set(e, new SentinelServerOnly { Pattern = ServerOnlyPattern });
            });
            rig.SetOnPlayer(RigRole.Mover, new SentinelOwner { Pattern = MoverPattern });
            rig.SetOnPlayer(RigRole.Mover, new SentinelServerOnly { Pattern = ServerOnlyPattern });
            rig.SetOnPlayer(RigRole.Observer, new SentinelOwner { Pattern = ObserverPattern });
        });
        rig.At(20, () => rig.Hide(RigRole.Observer, hidden));
        rig.At(22, () => rig.Set(hidden, new PadState { Length = 8, Fill = HiddenFill }));
        rig.At(25, () => rig.Show(RigRole.Observer, hidden));
        rig.At(30, () =>
        {
            Assert.True(rig.Host.TryGetPlayerState(mover.Slot, out PlayerMoveState state));
            state.Position = new Vector3(2f, state.Position.Y, -2f);
            rig.Host.SetPlayerState(mover.Slot, state, teleport: true);
        });
        rig.At(45, () => rig.SetOnPlayer(RigRole.Replacement, new SentinelOwner { Pattern = ReplacementPattern }));
        rig.AtSubtick(DeltaFaultSchedule.Subtick(42) + 1, () =>
        {
            RigClient replacement = rig[RigRole.Replacement];
            byte[] retiredState = observer.Downstream.Forwards.Last(f => f.Kind == FaultFrameKind.RebuildDelta).Payload;
            byte[] retiredAck = observer.Upstream!.Forwards.Last(f => f.Kind == FaultFrameKind.RoutineAck).Payload;
            replacement.Downstream.InjectAt(replacement.Connection, retiredState,
                NetChannelReliability.UnreliableSequenced, rig.SubtickNow);
            replacement.Upstream!.InjectAt(new NetConnectionId(1), retiredAck,
                NetChannelReliability.UnreliableSequenced, rig.SubtickNow);
        });
        rig.AfterPoll += (c, _) => AssertBuffersScoped(c, ForbiddenFor(c.Role));
        rig.Run();

        // Hide and show: never served while hidden, served again with its newest bytes.
        for (int tick = 20; tick <= 24; tick++)
            Assert.DoesNotContain(rig.ExpectedAt(RigRole.Observer, tick), e => e.NetId == hidden);
        Assert.Contains(rig.ExpectedAt(RigRole.Observer, 25), e => e.NetId == hidden);
        byte[] newest = Pad.Frame(8, HiddenFill);
        Assert.DoesNotContain(observer.Downstream.Forwards, f => f.SentSubtick < DeltaFaultSchedule.Subtick(25)
            && f.Payload.AsSpan().IndexOf(newest) >= 0);
        Assert.Contains(observer.Downstream.Forwards, f => f.SentSubtick == DeltaFaultSchedule.Subtick(25)
            && f.Payload.AsSpan().IndexOf(newest) >= 0);
        Assert.Contains(mover.Downstream.Forwards, f => f.SentSubtick == DeltaFaultSchedule.Subtick(22)
            && f.Payload.AsSpan().IndexOf(newest) >= 0);
        if (sharded)
        {
            Assert.True(rig.Host.TryGetEntity(hidden, out World owner, out _));
            Assert.NotSame(owner, rig.ServedWorld(RigRole.Replacement));
        }

        // Every outgoing byte is scoped: each owner pattern only on its own connection, the persist pattern nowhere.
        foreach (RigClient c in rig.Clients)
        {
            long[] forbidden = ForbiddenFor(c.Role);
            foreach (FaultForward f in c.Downstream.Forwards)
                Assert.False(ContainsAny(f.Payload, forbidden), $"{c.Role} received a forbidden pattern at {f.Subtick}");
        }
        Assert.Contains(mover.Downstream.Forwards, f => ContainsAny(f.Payload, new[] { MoverPattern }));
        Assert.Contains(observer.Downstream.Forwards, f => ContainsAny(f.Payload, new[] { ObserverPattern }));

        // The handoff keeps owner, net id and the owner-only value. Observers see the same net id on both sides.
        Assert.Equal(moverId, mover.Client.LocalNetId);
        Assert.True(rig.Host.TryGetPlayerState(mover.Slot, out PlayerMoveState crossed));
        Assert.True(crossed.Position.Z < 0f);
        Assert.True(mover.Client.TryGetComponent(moverId, out SentinelOwner kept));
        Assert.Equal(MoverPattern, kept.Pattern);
        Assert.Contains(rig.Trace.RemotesOf(observer.Index, moverId), r => r.Frame < Frame(DeltaFaultSchedule.Subtick(30)));
        Assert.Contains(rig.Trace.RemotesOf(observer.Index, moverId), r => r.Frame > Frame(DeltaFaultSchedule.Subtick(31)));
        Assert.True(observer.Accepted.All(id => id.Epoch == observer.Accepted.First().Epoch));

        // Slot reuse: the replacement holds slot 1 with a fresh epoch, the retired epoch changes nothing on either
        // side, and the new session never sees retired state.
        RigClient fresh = rig[RigRole.Replacement];
        Assert.Equal(rig.NetIdOf(RigRole.Replacement), fresh.Client.LocalNetId);
        Assert.NotEqual(observerId, fresh.Client.LocalNetId);
        ulong observerEpoch = observer.Accepted.Max(id => id.Epoch);
        Assert.NotEmpty(fresh.Accepted);
        Assert.All(fresh.Accepted, id => Assert.True(id.Epoch > observerEpoch));
        List<DeltaFrameRow> rows = rig.Trace.FramesOf(fresh.Index).ToList();
        DeltaFrameRow replay = DeltaReliabilityAcceptanceTests.RowAt(rows, DeltaFaultSchedule.Subtick(42) + 1);
        Assert.Equal(0, replay.IngestsThisPoll);
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, replay.Selection.Mode);
        Assert.True(replay.Selection.Epoch > observerEpoch);
        RebuildUsage usage = rig.Writer.RebuildUsageForTest(fresh.Slot);
        Assert.Equal(replay.Selection.Epoch, usage.AcknowledgedId!.Value.Epoch);
        Assert.True(fresh.Client.TryGetComponent(fresh.Client.LocalNetId, out SentinelOwner own));
        Assert.Equal(ReplacementPattern, own.Pattern);
        Assert.False(fresh.Client.TryGetComponent<SentinelOwner>(moverId, out _));
        Assert.Equal(WorldConnectionState.Connected, fresh.Client.ConnectionState);
        Assert.Equal(WorldConnectionState.Connected, mover.Client.ConnectionState);
    }

    [Theory]
    [MemberData(nameof(Heads))]
    public void VisibilityHideDuringFrozenKeyframeRemovesOnResume(bool sharded)
    {
        // 281 is the smallest cap that carries a 64 KiB keyframe in 255 chunks of cap - 23 bytes.
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, Array.Empty<DeltaFault>(),
            new DeltaRigOptions { InputStartTick = 2, ServerLimit = 281, UseVisibilityTable = true });
        RigClient mover = rig[RigRole.Mover];
        long big = 0, hide = 0, show = 0;
        rig.At(5, () =>
        {
            big = rig.SpawnEntity(5f, 5f, (w, e) => w.Set(e, new PadState { Length = 4, Fill = 0x10 }));
            hide = rig.SpawnEntity(6f, 4f, (w, e) => w.Set(e, new PadState { Length = 4, Fill = 0x20 }));
            show = rig.SpawnEntity(4f, 6f, (w, e) => w.Set(e, new PadState { Length = 4, Fill = 0x30 }));
            rig.Hide(RigRole.Mover, show);
        });
        // Freeze: a 2500-byte change cannot ride a datagram, so tick 20 starts one reliable keyframe that needs three
        // ticks of four chunks.
        rig.At(20, () => rig.Set(big, new PadState { Length = 2500, Fill = 0x11 }));
        rig.At(21, () => rig.Hide(RigRole.Mover, hide));
        rig.At(22, () =>
        {
            rig.Show(RigRole.Mover, show);
            rig.Set(big, new SentinelValue { Value = 22 });
        });
        rig.Run();

        List<FaultSend> sends = mover.Downstream.Sends;
        FaultSend first = sends.First(s => s.Kind == FaultFrameKind.KeyframeChunk && s.Subtick >= DeltaFaultSchedule.Subtick(20));
        ReplicationPacketId frozen = DeltaReliabilityAcceptanceTests.IdOf(first);
        Assert.Equal(DeltaFaultSchedule.Subtick(20), first.Subtick);
        int[] chunkTicks = sends.Where(s => s.Kind == FaultFrameKind.KeyframeChunk
            && DeltaReliabilityAcceptanceTests.IdOf(s) == frozen).Select(s => s.Subtick / 4).Distinct().ToArray();
        Assert.Equal(new[] { 20, 21, 22 }, chunkTicks);
        Assert.Equal(20, rig.BuildTickOf(RigRole.Mover, frozen));

        // The frozen keyframe carries the entity hidden mid-run and not the one shown mid-run, as authorized at tick 20.
        IReadOnlyList<ProjectionEntry> keyframe = rig.ExpectedAt(RigRole.Mover, 20);
        Assert.Contains(keyframe, e => e.NetId == hide);
        Assert.DoesNotContain(keyframe, e => e.NetId == show);
        Assert.Contains(frozen, mover.Accepted);

        // No delta before the exact keyframe ack. The first resumed state removes the hidden entity, adds the shown
        // one and carries the newest value.
        Assert.DoesNotContain(sends, s => s.Kind == FaultFrameKind.RebuildDelta
            && s.Subtick is >= 80 and < 92);
        FaultSend resumed = DeltaReliabilityAcceptanceTests.StateSentAt(mover, 23);
        RebuildWireHeader header = DeltaReliabilityAcceptanceTests.Header(resumed);
        Assert.Equal(frozen.Sequence, header.Baseline);
        Assert.Equal(new[] { hide }, RebuildWire.ReadRemovedNetIds(resumed.Payload.AsSpan(2 + RebuildProtocol.EnvelopeBytes)));
        IReadOnlyList<ProjectionEntry> after = rig.ExpectedAt(RigRole.Mover, 23);
        Assert.DoesNotContain(after, e => e.NetId == hide);
        Assert.Contains(after, e => e.NetId == show);
        Assert.Contains(after, e => e.NetId == big && e.TypeId == DeltaRigRegistry.ValueTypeId);
        Assert.Contains(DeltaReliabilityAcceptanceTests.IdOf(resumed), mover.Accepted);
        Assert.False(mover.Client.TryGetComponent<PadState>(hide, out _));
        Assert.True(mover.Client.TryGetComponent(show, out PadState shown));
        Assert.Equal((byte)0x30, shown.Fill);
        Assert.True(mover.Client.TryGetComponent(big, out SentinelValue value));
        Assert.Equal(22, value.Value);
        rig.Trace.AssertMaxima(mover.Index, new DeltaRebuildOptions(), 281);
        Assert.All(sends.Where(s => s.Kind is FaultFrameKind.RebuildDelta or FaultFrameKind.KeyframeChunk),
            s => Assert.True(s.Payload.Length <= 281));
    }

    private static int Frame(int subtick) => (subtick - Phase) / DeltaFaultSchedule.SubticksPerFrame;

    private static long[] ForbiddenFor(RigRole role) => role switch
    {
        RigRole.Mover => new[] { ObserverPattern, ReplacementPattern, ServerOnlyPattern },
        RigRole.Observer => new[] { MoverPattern, ReplacementPattern, ServerOnlyPattern },
        _ => new[] { MoverPattern, ObserverPattern, ServerOnlyPattern },
    };

    // Viewer buffers: every retained projection and every presentation array the client's view holds.
    private static void AssertBuffersScoped(RigClient c, long[] forbidden)
    {
        if (c.Client.DeltaRebuildForTest is { } rebuild)
            foreach (ReplicationPacketId id in c.Accepted)
                if (rebuild.TryGetRetainedForTest(id, out ReplicationProjection projection))
                    foreach (ProjectionEntry entry in ProjectionDump.Of(projection))
                        Assert.False(ContainsAny(entry.Payload, forbidden), $"{c.Role} retains a forbidden pattern in {id}");
        foreach (byte[] array in c.Client.ViewForTest.PresentationArraysForTest())
            Assert.False(ContainsAny(array, forbidden), $"{c.Role} presentation holds a forbidden pattern");
    }

    private static bool ContainsAny(byte[] data, IEnumerable<long> patterns)
    {
        foreach (long pattern in patterns)
            if (data.AsSpan().IndexOf(BitConverter.GetBytes(pattern)) >= 0) return true;
        return false;
    }
}
