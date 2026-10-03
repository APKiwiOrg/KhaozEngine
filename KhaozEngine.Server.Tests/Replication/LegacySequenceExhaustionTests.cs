using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Global signed sequence exhaustion on both legacy writers. <see cref="int.MaxValue"/> is the last capture sequence a
/// writer may produce. A further capture is refused without scanning or incrementing, <c>Forget</c> never resets the
/// shared counter, and only the explicit global reset after exhaustion clears the counter, the shared capture state
/// and every slot. The counter is seeded next to the boundary through a test seam, so no test walks the signed range.
/// </summary>
public class LegacySequenceExhaustionTests
{
    private struct Value : IComponent { public int Number; }

    public static TheoryData<string> Writers => new()
    {
        nameof(LegacyWriterFixture.WholeWorld),
        nameof(LegacyWriterFixture.Aoi),
    };

    private static readonly HashSet<long> Interest = new() { 1, 2 };

    private static LegacyWriterFixture NewFixture(string kind, out Entity mover)
    {
        var registry = new ReplicationRegistry();
        registry.Register<Value>(1, (v, bw) => bw.Write(v.Number), br => new Value { Number = br.ReadInt32() });
        LegacyWriterFixture f = LegacyWriterFixture.Create(kind, registry);
        mover = f.ServerWorld.Spawn();
        f.ServerWorld.Set(mover, new NetId(1));
        f.ServerWorld.Set(mover, new Value { Number = 1 });
        Entity other = f.ServerWorld.Spawn();
        f.ServerWorld.Set(other, new NetId(2));
        f.ServerWorld.Set(other, new Value { Number = 20 });
        return f;
    }

    // Two ordinary low-sequence captures served to slots 0 and 1, so slot state and retained history exist.
    private static void PopulateTwoSlots(LegacyWriterFixture f, Entity mover)
    {
        int first = f.CaptureNext();
        f.Serve(0, Interest);
        f.Serve(1, Interest);
        f.Ack(0, first);
        f.ServerWorld.Set(mover, new Value { Number = 2 });
        f.CaptureNext();
        f.Serve(0, Interest);
        f.Serve(1, Interest);
        Assert.Equal(2, f.CurrentSeq);
        Assert.Equal(2, f.SlotCount);
        Assert.Equal(1, f.HistoryCount);
    }

    // Seeds the counter one below the boundary, captures once to reach it and serves both slots there.
    private static void ExhaustAndServeBothSlots(LegacyWriterFixture f, Entity mover)
    {
        int lastLowSequence = f.CurrentSeq;
        f.SeedSequence(int.MaxValue - 1);
        f.ServerWorld.Set(mover, new Value { Number = 3 });
        Assert.Equal(int.MaxValue, f.CaptureNext());
        foreach (int slot in new[] { 0, 1 })
        {
            LegacyDeltaHeader header = LegacyDeltaWire.ReadHeader(f.Serve(slot, Interest));
            Assert.Equal(lastLowSequence, header.Baseline);
            Assert.Equal(int.MaxValue, header.Snapshot);
        }
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void FinalSignedSequenceStopsCaptureAndForgetCannotReset(string kind)
    {
        LegacyWriterFixture fixture = NewFixture(kind, out Entity mover);
        PopulateTwoSlots(fixture, mover);
        Assert.False(fixture.Exhausted);
        ExhaustAndServeBothSlots(fixture, mover);

        Assert.Equal(int.MaxValue, fixture.CurrentSeq);
        Assert.True(fixture.Exhausted);
        Assert.Throws<InvalidOperationException>(() => fixture.CaptureNext());
        fixture.Forget(0);
        Assert.True(fixture.Exhausted);
        Assert.Equal(int.MaxValue, fixture.CurrentSeq);

        // Forget cleared only its own slot, and the refused capture did not touch the retained history.
        Assert.Equal(1, fixture.SlotCount);
        Assert.Equal(1, fixture.HistoryCount);
        Assert.Throws<InvalidOperationException>(() => fixture.CaptureNext());
        Assert.Equal(int.MaxValue, fixture.CurrentSeq);
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void ResetBeforeExhaustionIsRejected(string kind)
    {
        LegacyWriterFixture fixture = NewFixture(kind, out Entity mover);
        Assert.Throws<InvalidOperationException>(() => fixture.ResetAfterExhaustion());

        PopulateTwoSlots(fixture, mover);
        Assert.Throws<InvalidOperationException>(() => fixture.ResetAfterExhaustion());

        Assert.False(fixture.Exhausted);
        Assert.Equal(2, fixture.CurrentSeq);
        Assert.Equal(2, fixture.SlotCount);
        Assert.Equal(1, fixture.HistoryCount);
    }

    // The documented lifecycle precondition is that every connection served by the writer has ended before the reset,
    // so the fresh receiver below stands for a new connection. Replication cannot verify or end transports itself.
    [Theory]
    [MemberData(nameof(Writers))]
    public void GlobalResetClearsEverySlotAndSharedState(string kind)
    {
        LegacyWriterFixture fixture = NewFixture(kind, out Entity mover);
        PopulateTwoSlots(fixture, mover);
        ExhaustAndServeBothSlots(fixture, mover);
        Assert.Equal(2, fixture.SlotCount);
        Assert.Equal(1, fixture.HistoryCount);

        fixture.ResetAfterExhaustion();

        Assert.False(fixture.Exhausted);
        Assert.Equal(0, fixture.CurrentSeq);
        Assert.Equal(0, fixture.SlotCount);
        Assert.Equal(0, fixture.HistoryCount);

        fixture.NewReceiver();
        Assert.Equal(1, fixture.CaptureNext());
        foreach (int slot in new[] { 1, 0 })
        {
            byte[] full = fixture.Serve(slot, Interest);
            LegacyDeltaHeader header = LegacyDeltaWire.ReadHeader(full);
            Assert.Equal(-1, header.Baseline);
            Assert.Equal(1, header.Snapshot);
            Assert.Empty(header.RemovedNetIds);
            Assert.Equal(2, header.ChangedCount);
            if (slot == 0) fixture.Apply(full);
        }

        fixture.ServerWorld.Set(mover, new Value { Number = 4 });
        Assert.Equal(2, fixture.CaptureNext());
        byte[] next = fixture.Serve(0, Interest);
        Assert.Equal(1, LegacyDeltaWire.ReadHeader(next).Baseline);
        fixture.Apply(next);
        Assert.True(fixture.View.TryGetEntity(1, out Entity client1));
        Assert.True(fixture.View.TryGetEntity(2, out Entity client2));
        Assert.Equal(4, fixture.ClientWorld.Get<Value>(client1).Number);
        Assert.Equal(20, fixture.ClientWorld.Get<Value>(client2).Number);
    }

    // Format 2 state lives beside the legacy slots. The global reset clears every slot's candidate, acknowledged
    // baseline, pins and committed history, but remembers no epoch: the stream owner's server-lifetime allocator must
    // supply a greater one, so Replication accepts any nonzero epoch for a slot it no longer holds.
    [Theory]
    [MemberData(nameof(Writers))]
    public void GlobalResetClearsFormat2SlotState(string kind)
    {
        RebuildWriterAdapter writer = RebuildWriterAdapter.Create(kind, RebuildWriterAdapter.NewRegistry());
        Entity mover = writer.World.Spawn();
        writer.World.Set(mover, new NetId(1));
        writer.World.Set(mover, new RebuildValue { Number = 1 });
        writer.Start(0, 7);
        writer.Start(1, 8);

        ReplicationDeltaPacket acknowledged = writer.CaptureAndSend(0, Interest);
        writer.Ack(0, acknowledged.Id);
        writer.World.Set(mover, new RebuildValue { Number = 2 });
        writer.Capture();
        writer.WriteLegacy(2, Interest);
        ReplicationDeltaPacket committed = writer.Build(0, Interest);
        writer.Sent(0, committed.Id);
        ReplicationDeltaPacket unsent = writer.Build(1, Interest);
        Assert.Equal(2, writer.Usage(0).RetainedCount);

        writer.SeedLegacySequence(int.MaxValue - 1);
        Assert.Equal(int.MaxValue, writer.Capture());
        Assert.True(writer.Exhausted);
        writer.ResetAfterExhaustion();

        foreach (int slot in new[] { 0, 1 })
        {
            Assert.Equal(default(RebuildUsage), writer.Usage(slot));
            Assert.False(writer.NeedsRepair(slot));
        }
        Assert.False(writer.TryGetRetained(0, acknowledged.Id, out _));
        Assert.False(writer.TryGetRetained(0, committed.Id, out _));
        Assert.Equal(1, writer.Capture());
        Assert.Throws<InvalidOperationException>(() => writer.Build(0, Interest));
        Assert.Throws<InvalidOperationException>(() => writer.Sent(1, unsent.Id));
        writer.Ack(0, committed.Id);

        writer.Start(0, 7);
        ReplicationDeltaPacket fresh = writer.Build(0, Interest);
        Assert.True(fresh.IsKeyframe);
        Assert.Equal(new ReplicationPacketId(7, 1), fresh.Id);
        Assert.Equal(1, writer.CurrentSeq);
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void ForgetClearsFormat2StateButNeverTheCounter(string kind)
    {
        RebuildWriterAdapter writer = RebuildWriterAdapter.Create(kind, RebuildWriterAdapter.NewRegistry());
        Entity mover = writer.World.Spawn();
        writer.World.Set(mover, new NetId(1));
        writer.World.Set(mover, new RebuildValue { Number = 1 });
        writer.Start(0, 3);
        writer.Start(1, 4);
        ReplicationDeltaPacket keyframe = writer.CaptureAndSend(0, Interest);
        writer.Ack(0, keyframe.Id);
        writer.World.Set(mover, new RebuildValue { Number = 2 });
        ReplicationDeltaPacket delta = writer.CaptureAndSend(0, Interest);
        ReplicationDeltaPacket other = writer.CaptureAndSend(1, Interest);
        int sequence = writer.CurrentSeq;

        writer.Forget(0);

        Assert.Equal(default(RebuildUsage), writer.Usage(0));
        Assert.False(writer.NeedsRepair(0));
        writer.Ack(0, delta.Id);
        Assert.Throws<InvalidOperationException>(() => writer.Build(0, Interest));
        Assert.Equal(sequence, writer.CurrentSeq);
        Assert.True(writer.TryGetRetained(1, other.Id, out _));
        Assert.Equal(1, writer.Usage(1).NewSentCount);

        writer.Start(0, 3);
        Assert.Equal(new ReplicationPacketId(3, 1), writer.Build(0, Interest).Id);
    }
}
