using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// The format 2 sent history on both writer shapes: one unsent candidate, commits only for the exact candidate,
/// acknowledgement only of a committed, retained, strictly newer id of the slot's own epoch, insertion-order pruning
/// across the unsigned wrap, and the repair signal before pins block bounded retention.
/// </summary>
public class RebuildSentHistoryTests
{
    public static TheoryData<string> Kinds => RebuildWriterAdapter.Kinds;

    private static readonly HashSet<long> All = new() { 1, 2 };

    private static RebuildWriterAdapter Writer(string kind, out Entity mover)
    {
        RebuildWriterAdapter w = RebuildWriterAdapter.Create(kind, RebuildWriterAdapter.NewRegistry());
        mover = w.World.Spawn();
        w.World.Set(mover, new NetId(1));
        w.World.Set(mover, new RebuildValue { Number = 1 });
        Entity other = w.World.Spawn();
        w.World.Set(other, new NetId(2));
        w.World.Set(other, new RebuildValue { Number = 20 });
        return w;
    }

    private static void Set(RebuildWriterAdapter w, Entity e, int value) =>
        w.World.Set(e, new RebuildValue { Number = value });

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EmptyAckRelativeReversionPacketNamesOriginalBaseline(string kind)
    {
        RebuildWriterAdapter writer = Writer(kind, out Entity mover);
        const int slot = 0;
        writer.Start(slot, 1);

        ReplicationDeltaPacket initial = writer.CaptureAndSend(slot, All);
        Assert.True(initial.IsKeyframe);
        writer.Ack(slot, initial.Id);

        Set(writer, mover, 2);
        ReplicationDeltaPacket intermediate = writer.CaptureAndSend(slot, All);
        Assert.Equal(initial.Id, intermediate.Baseline);
        Assert.Equal(1, RebuildWire.ReadHeader(intermediate.Bytes.Span).ChangedCount);

        Set(writer, mover, 1);
        writer.Capture();
        ReplicationDeltaPacket reverted = writer.Build(slot, All);

        Assert.Equal(initial.Id, reverted.Baseline);
        Assert.Equal(0, RebuildWire.ReadHeader(reverted.Bytes.Span).ChangedCount);
        writer.Ack(slot, reverted.Id);
        RebuildUsage usage = writer.Usage(slot);
        Assert.Equal(initial.Id, usage.AcknowledgedId);
        Assert.Equal(reverted.Id, usage.CandidateId);
        Assert.Equal(3, usage.RetainedCount);
        Assert.Equal(1, usage.NewSentCount);
        Assert.Equal(ProjectionDump.DistinctBackingBytes(RetainedOf(writer, slot, initial.Id, intermediate.Id, reverted.Id)), usage.RetainedBytes);

        // An empty body is the 18-byte header plus both counts, 40 bytes once framed with session, kind and envelope.
        Assert.Equal(26, reverted.Bytes.Length);
    }

    private static IEnumerable<ReplicationProjection> RetainedOf(RebuildWriterAdapter writer, int slot,
        params ReplicationPacketId[] ids) => writer.RetainedOf(slot, ids);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void UnsentCandidateCannotBeAcknowledged(string kind)
    {
        RebuildWriterAdapter w = Writer(kind, out Entity mover);
        w.Start(0, 1);
        w.Capture();
        ReplicationDeltaPacket keyframe = w.Build(0, All);

        w.Ack(0, keyframe.Id);
        RebuildUsage usage = w.Usage(0);
        Assert.Null(usage.AcknowledgedId);
        Assert.Equal(keyframe.Id, usage.CandidateId);
        Assert.Equal(0, usage.NewSentCount);

        Assert.Throws<InvalidOperationException>(() => w.Sent(0, new ReplicationPacketId(1, 99)));
        Assert.Throws<InvalidOperationException>(() => w.Sent(0, new ReplicationPacketId(2, keyframe.Id.Sequence)));
        Assert.Throws<InvalidOperationException>(() => w.Sent(7, keyframe.Id));
        Assert.Equal(keyframe.Id, w.Usage(0).CandidateId);

        w.Sent(0, keyframe.Id);
        Assert.Throws<InvalidOperationException>(() => w.Sent(0, keyframe.Id));
        usage = w.Usage(0);
        Assert.Null(usage.CandidateId);
        Assert.Equal(1, usage.NewSentCount);

        w.Ack(0, keyframe.Id);
        Assert.Equal(keyframe.Id, w.Usage(0).AcknowledgedId);
        Assert.Equal(0, w.Usage(0).NewSentCount);

        Set(w, mover, 2);
        w.Capture();
        ReplicationDeltaPacket delta = w.Build(0, All);
        w.Ack(0, delta.Id);
        usage = w.Usage(0);
        Assert.Equal(keyframe.Id, usage.AcknowledgedId);
        Assert.Equal(delta.Id, usage.CandidateId);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void OnlyNewestUnsentCandidateIsRetained(string kind)
    {
        RebuildWriterAdapter w = Writer(kind, out Entity mover);
        w.Start(0, 1, new DeltaRebuildOptions { MaxRetainedProjections = 4 });
        ReplicationDeltaPacket keyframe = w.CaptureAndSend(0, All);
        w.Ack(0, keyframe.Id);
        Set(w, mover, 2);
        ReplicationDeltaPacket sent1 = w.CaptureAndSend(0, All);
        Set(w, mover, 3);
        ReplicationDeltaPacket sent2 = w.CaptureAndSend(0, All);

        Set(w, mover, 4);
        w.Capture();
        ReplicationDeltaPacket candidate1 = w.Build(0, All);
        Assert.Equal(4, w.Usage(0).RetainedCount);

        // The full table drops the superseded candidate, never a committed send the receiver may still acknowledge.
        Set(w, mover, 5);
        w.Capture();
        ReplicationDeltaPacket candidate2 = w.Build(0, All);
        Assert.Equal(candidate1.Id.Sequence + 1, candidate2.Id.Sequence);
        Assert.False(w.TryGetRetained(0, candidate1.Id, out _));
        RebuildUsage usage = w.Usage(0);
        Assert.Equal(candidate2.Id, usage.CandidateId);
        Assert.Equal(4, usage.RetainedCount);
        Assert.Equal(2, usage.NewSentCount);
        Assert.Equal(ProjectionDump.DistinctBackingBytes(
            w.RetainedOf(0, keyframe.Id, sent1.Id, sent2.Id, candidate2.Id)), usage.RetainedBytes);

        Assert.Throws<InvalidOperationException>(() => w.Sent(0, candidate1.Id));
        Assert.False(w.NeedsRepair(0));
        w.Sent(0, candidate2.Id);
        Assert.Equal(3, w.Usage(0).NewSentCount);
        Assert.True(w.NeedsRepair(0));   // effective window min(31, 4 - 1) is consumed

        w.Ack(0, sent1.Id);
        usage = w.Usage(0);
        Assert.Equal(sent1.Id, usage.AcknowledgedId);
        Assert.Equal(2, usage.NewSentCount);
        Assert.False(w.NeedsRepair(0));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void AckMustBeSentToThisSlotAndStillRetained(string kind)
    {
        RebuildWriterAdapter w = Writer(kind, out Entity mover);
        var options = new DeltaRebuildOptions { MaxRetainedProjections = 4 };
        w.Start(0, 1, options);
        w.Start(1, 1, options);

        // Both slots serve the same shared capture under equal ids, but slot 1 never sends its candidate, so slot 0's
        // send cannot acknowledge anything on slot 1.
        w.Capture();
        ReplicationDeltaPacket keyframe = w.Build(0, All);
        ReplicationDeltaPacket otherCandidate = w.Build(1, All);
        Assert.Equal(keyframe.Id, otherCandidate.Id);
        w.Sent(0, keyframe.Id);
        w.Ack(1, keyframe.Id);
        Assert.Null(w.Usage(1).AcknowledgedId);
        w.Ack(0, keyframe.Id);
        Assert.Equal(keyframe.Id, w.Usage(0).AcknowledgedId);

        Set(w, mover, 2);
        ReplicationDeltaPacket sent2 = w.CaptureAndSend(0, All);
        Set(w, mover, 3);
        ReplicationDeltaPacket sent3 = w.CaptureAndSend(0, All);
        w.Ack(1, sent3.Id);   // never sent to slot 1
        Assert.Null(w.Usage(1).AcknowledgedId);
        w.Ack(0, sent3.Id);
        Assert.Equal(sent3.Id, w.Usage(0).AcknowledgedId);
        Assert.Equal(0, w.Usage(0).NewSentCount);

        w.Ack(0, sent2.Id);                                          // stale
        w.Ack(0, sent3.Id);                                          // duplicate
        w.Ack(0, new ReplicationPacketId(1, sent3.Id.Sequence + 40)); // future
        Assert.Equal(sent3.Id, w.Usage(0).AcknowledgedId);
        Assert.Equal(0, w.Usage(0).NewSentCount);

        var later = new List<ReplicationDeltaPacket>();
        for (int value = 4; value <= 8; value++)
        {
            Set(w, mover, value);
            later.Add(w.CaptureAndSend(0, All));
        }
        Assert.False(w.TryGetRetained(0, later[0].Id, out _));
        w.Ack(0, later[0].Id);                                       // pruned
        Assert.Equal(sent3.Id, w.Usage(0).AcknowledgedId);
        Assert.Equal(5, w.Usage(0).NewSentCount);

        w.Start(0, 2, options);
        ReplicationDeltaPacket replacement = w.CaptureAndSend(0, All);
        w.Ack(0, later[^1].Id);                                      // retired epoch
        w.Ack(0, new ReplicationPacketId(2, replacement.Id.Sequence + 1));
        Assert.Null(w.Usage(0).AcknowledgedId);
        w.Ack(5, replacement.Id);                                    // a slot without a stream ignores it
        w.Ack(0, replacement.Id);
        Assert.Equal(replacement.Id, w.Usage(0).AcknowledgedId);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void AckOrderCannotRegressAcrossUnsignedWrap(string kind)
    {
        RebuildWriterAdapter w = Writer(kind, out Entity mover);
        w.Start(0, 1, new DeltaRebuildOptions { MaxRetainedProjections = 4 });
        w.SeedSequence(0, uint.MaxValue - 1);
        ReplicationDeltaPacket keyframe = w.CaptureAndSend(0, All);
        Assert.Equal(uint.MaxValue - 1, keyframe.Id.Sequence);
        w.Ack(0, keyframe.Id);

        Set(w, mover, 2);
        ReplicationDeltaPacket a = w.CaptureAndSend(0, All);
        Set(w, mover, 3);
        ReplicationDeltaPacket b = w.CaptureAndSend(0, All);
        Set(w, mover, 4);
        ReplicationDeltaPacket c = w.CaptureAndSend(0, All);
        Assert.Equal(uint.MaxValue, a.Id.Sequence);
        Assert.Equal(0u, b.Id.Sequence);
        Assert.Equal(1u, c.Id.Sequence);
        Assert.Equal(new RebuildWireHeader(2, 0, 1, 0, uint.MaxValue - 1, 0, 1), RebuildWire.ReadHeader(b.Bytes.Span));

        // The next candidate prunes the oldest insertion (uint.MaxValue), not the numerically smallest sequence.
        Set(w, mover, 5);
        ReplicationDeltaPacket d = w.CaptureAndSend(0, All);
        Assert.False(w.TryGetRetained(0, a.Id, out _));
        Assert.True(w.TryGetRetained(0, b.Id, out _));
        Assert.True(w.TryGetRetained(0, c.Id, out _));
        Assert.True(w.TryGetRetained(0, keyframe.Id, out _));

        w.Ack(0, b.Id);
        Assert.Equal(b.Id, w.Usage(0).AcknowledgedId);
        Assert.Equal(2, w.Usage(0).NewSentCount);
        w.Ack(0, a.Id);
        w.Ack(0, keyframe.Id);
        Assert.Equal(b.Id, w.Usage(0).AcknowledgedId);
        w.Ack(0, d.Id);
        w.Ack(0, c.Id);
        Assert.Equal(d.Id, w.Usage(0).AcknowledgedId);
        Assert.Equal(0, w.Usage(0).NewSentCount);

        Set(w, mover, 6);
        w.Capture();
        ReplicationDeltaPacket next = w.Build(0, All);
        Assert.Equal(d.Id, next.Baseline);
        Assert.Equal(new RebuildWireHeader(2, 0, 1, 3, 2, 0, 1), RebuildWire.ReadHeader(next.Bytes.Span));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void PinnedBaselineStartsRepairBeforeSend32(string kind)
    {
        RebuildWriterAdapter w = Writer(kind, out Entity mover);
        w.Start(0, 1);
        ReplicationDeltaPacket keyframe = w.CaptureAndSend(0, All);
        w.Ack(0, keyframe.Id);

        var sends = new List<ReplicationDeltaPacket>();
        for (int i = 1; i <= 31; i++)
        {
            Assert.False(w.NeedsRepair(0));
            Set(w, mover, i + 1);
            sends.Add(w.CaptureAndSend(0, All));
            Assert.Equal(keyframe.Id, sends[^1].Baseline);
        }

        Assert.True(w.NeedsRepair(0));
        RebuildUsage usage = w.Usage(0);
        Assert.Equal(31, usage.NewSentCount);
        Assert.Equal(32, usage.RetainedCount);
        Assert.Equal(keyframe.Id, usage.AcknowledgedId);
        foreach (ReplicationDeltaPacket sent in sends) Assert.True(w.TryGetRetained(0, sent.Id, out _));

        // A late ack for the oldest send still promotes, which reopens the window.
        w.Ack(0, sends[0].Id);
        Assert.Equal(30, w.Usage(0).NewSentCount);
        Assert.False(w.NeedsRepair(0));

        // A smaller table or a smaller configured window shortens the effective window.
        foreach ((ulong epoch, DeltaRebuildOptions options, int window) in new[]
        {
            (2UL, new DeltaRebuildOptions { MaxRetainedProjections = 6 }, 5),
            (3UL, new DeltaRebuildOptions { NoAckSendWindow = 2 }, 2),
        })
        {
            w.Start(0, epoch, options);
            Assert.False(w.NeedsRepair(0));
            ReplicationDeltaPacket baseline = w.CaptureAndSend(0, All);
            w.Ack(0, baseline.Id);
            for (int i = 0; i < window; i++)
            {
                Assert.False(w.NeedsRepair(0));
                Set(w, mover, 100 + i);
                w.CaptureAndSend(0, All);
            }
            Assert.True(w.NeedsRepair(0));
        }

        w.Start(0, 4);
        Assert.False(w.NeedsRepair(0));
        Assert.Equal(default(RebuildUsage), w.Usage(0));
    }

    // Ruling D2.9: options require a byte budget of three complete keyframes, and every retained writer projection is
    // smaller than its keyframe. The acknowledged baseline, a pending keyframe and a new candidate therefore always fit
    // together, so payload growth can never force a repair or a pressure failure. It reaches the capacity failure first.
    [Theory]
    [MemberData(nameof(Kinds))]
    public void PayloadPressureRequestsRepairBeforeCapacity(string kind)
    {
        RebuildWriterAdapter w = RebuildWriterAdapter.Create(kind, RebuildWriterAdapter.NewRegistry());
        Entity blob = w.World.Spawn();
        w.World.Set(blob, new NetId(1));
        var interest = new HashSet<long> { 1 };

        // One entity with a 100 byte framed blob: 26 header and count bytes, 15 entity bytes, 2 + 1 + 100 frame bytes.
        const int keyframeBytes = 144;
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Start(0, 1,
            new DeltaRebuildOptions { MaxKeyframeBytes = keyframeBytes, MaxRetainedPayloadBytes = 2 * keyframeBytes }));
        var options = new DeltaRebuildOptions { MaxKeyframeBytes = keyframeBytes, MaxRetainedPayloadBytes = 3 * keyframeBytes };
        w.Start(0, 2, options);

        w.World.Set(blob, new RebuildBlob { Size = 30 });
        ReplicationDeltaPacket first = w.CaptureAndSend(0, interest);
        w.Ack(0, first.Id);
        Assert.False(w.NeedsRepair(0));

        w.World.Set(blob, new RebuildBlob { Size = 100 });
        ReplicationDeltaPacket grown = w.CaptureAndSend(0, interest);
        Assert.False(w.NeedsRepair(0));
        w.Ack(0, grown.Id);
        Assert.False(w.NeedsRepair(0));

        // A largest-size pending keyframe beside the largest-size baseline, then a largest-size candidate beside both.
        ReplicationDeltaPacket pending = w.CaptureAndSend(0, interest, keyframe: true);
        Assert.Equal(keyframeBytes, pending.Bytes.Length);
        Assert.False(w.NeedsRepair(0));
        w.Capture();
        ReplicationDeltaPacket candidate = w.Build(0, interest);
        Assert.Equal(grown.Id, candidate.Baseline);
        Assert.False(w.NeedsRepair(0));
        RebuildUsage usage = w.Usage(0);
        Assert.Equal(ProjectionDump.DistinctBackingBytes(w.RetainedOf(0, grown.Id, pending.Id, candidate.Id)),
            usage.RetainedBytes);
        Assert.Equal(300, usage.RetainedBytes);

        // One more byte makes the projection's keyframe too large: capacity, never pressure, and the slot is unchanged.
        w.World.Set(blob, new RebuildBlob { Size = 101 });
        w.Capture();
        DeltaRebuildException capacity = Assert.Throws<DeltaRebuildException>(() => w.Build(0, interest));
        Assert.Equal(DeltaRebuildFailure.CapacityExceeded, capacity.Failure);
        Assert.Equal(candidate.Id, w.Usage(0).CandidateId);
        Assert.Equal(grown.Id, w.Usage(0).AcknowledgedId);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void KeyframeStaysPinnedUntilItsExactAck(string kind)
    {
        RebuildWriterAdapter w = Writer(kind, out Entity mover);
        w.Start(0, 1, new DeltaRebuildOptions { MaxRetainedProjections = 4 });
        ReplicationDeltaPacket first = w.CaptureAndSend(0, All);
        w.Ack(0, first.Id);

        Set(w, mover, 2);
        ReplicationDeltaPacket keyframe = w.CaptureAndSend(0, All, keyframe: true);
        Assert.True(keyframe.IsKeyframe);
        var deltas = new List<ReplicationDeltaPacket>();
        for (int value = 3; value <= 7; value++)
        {
            Set(w, mover, value);
            deltas.Add(w.CaptureAndSend(0, All));
            Assert.Equal(first.Id, deltas[^1].Baseline);
        }

        Assert.True(w.TryGetRetained(0, keyframe.Id, out _));
        Assert.False(w.TryGetRetained(0, deltas[0].Id, out _));
        Assert.Equal(4, w.Usage(0).RetainedCount);

        w.Ack(0, keyframe.Id);
        Assert.Equal(keyframe.Id, w.Usage(0).AcknowledgedId);
        Assert.Equal(5, w.Usage(0).NewSentCount);
        w.Capture();
        Assert.Equal(keyframe.Id, w.Build(0, All).Baseline);
    }
}
