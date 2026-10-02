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
}
