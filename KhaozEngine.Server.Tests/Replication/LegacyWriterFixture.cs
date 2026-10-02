using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// One server world, one legacy writer and one legacy receiver, so a test can drive the whole-world
/// <see cref="ServerReplicator"/> and the area-of-interest <see cref="AoiDeltaReplicator"/> through the same steps.
/// <see cref="Apply"/> uses the unchanged legacy <see cref="ClientReplicationView.ApplyDelta"/>.
/// </summary>
internal abstract class LegacyWriterFixture
{
    protected LegacyWriterFixture(ReplicationRegistry registry)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ClientWorld = new World();
        View = new ClientReplicationView(registry);
    }

    public ReplicationRegistry Registry { get; }

    public World ServerWorld { get; } = new();

    public World ClientWorld { get; private set; }

    public ClientReplicationView View { get; private set; }

    public abstract int CaptureNext();

    public abstract byte[] Serve(int slot, IReadOnlySet<long>? interest = null, long? owner = null);

    public void Apply(byte[] payload) => View.ApplyDelta(ClientWorld, payload);

    /// <summary>Replaces the receiver with a fresh world and view, as a new connection would.</summary>
    public void NewReceiver()
    {
        ClientWorld = new World();
        View = new ClientReplicationView(Registry);
    }

    public abstract void Ack(int slot, int sequence);

    public abstract void Forget(int slot);

    public abstract void SeedSequence(int sequence);

    public abstract bool Exhausted { get; }

    public abstract int CurrentSeq { get; }

    public abstract int SlotCount { get; }

    public abstract int HistoryCount { get; }

    public abstract void ResetAfterExhaustion();

    public static LegacyWriterFixture Create(string kind, ReplicationRegistry registry) => kind switch
    {
        nameof(WholeWorld) => new WholeWorld(registry),
        nameof(Aoi) => new Aoi(registry),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown writer kind"),
    };

    public sealed class WholeWorld : LegacyWriterFixture
    {
        private readonly ServerReplicator writer;

        public WholeWorld(ReplicationRegistry registry) : base(registry) => writer = new ServerReplicator(registry);

        public override int CaptureNext() => writer.Capture(ServerWorld);

        public override byte[] Serve(int slot, IReadOnlySet<long>? interest = null, long? owner = null) =>
            writer.WriteFor(slot, owner);

        public override void Ack(int slot, int sequence) => writer.Acknowledge(slot, sequence);

        public override void Forget(int slot) => writer.Forget(slot);

        public override void SeedSequence(int sequence) => writer.SeedLegacySequenceForTest(sequence);

        public override bool Exhausted => writer.LegacySequenceExhausted;

        public override int CurrentSeq => writer.CurrentSeq;

        public override int SlotCount => writer.LegacySlotCount;

        public override int HistoryCount => writer.LegacyHistoryCount;

        public override void ResetAfterExhaustion() => writer.ResetAfterLegacySequenceExhaustion();
    }

    public sealed class Aoi : LegacyWriterFixture
    {
        private readonly AoiDeltaReplicator writer;

        public Aoi(ReplicationRegistry registry) : base(registry) => writer = new AoiDeltaReplicator(registry);

        public override int CaptureNext() => writer.BeginTick();

        public override byte[] Serve(int slot, IReadOnlySet<long>? interest = null, long? owner = null) =>
            writer.WriteFor(slot, ServerWorld, interest ?? throw new ArgumentNullException(nameof(interest)), owner);

        public override void Ack(int slot, int sequence) => writer.Acknowledge(slot, sequence);

        public override void Forget(int slot) => writer.Forget(slot);

        public override void SeedSequence(int sequence) => writer.SeedLegacySequenceForTest(sequence);

        public override bool Exhausted => writer.LegacySequenceExhausted;

        public override int CurrentSeq => writer.CurrentSeq;

        public override int SlotCount => writer.LegacySlotCount;

        public override int HistoryCount => writer.LegacyHistoryCount;

        public override void ResetAfterExhaustion() => writer.ResetAfterLegacySequenceExhaustion();
    }
}
