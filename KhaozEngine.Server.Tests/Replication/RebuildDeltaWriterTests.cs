using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Format 2 writer declarations, preconditions, encoding and capacity on both writer shapes. The body after the
/// 18-byte format 2 header must keep the legacy entity and component sections byte for byte, so each encoding case
/// compares against the legacy writer serving the same capture from the same basis.
/// </summary>
public class RebuildDeltaWriterTests
{
    public static TheoryData<string> Kinds => RebuildWriterAdapter.Kinds;

    private static readonly HashSet<long> All = new() { 1, 2, 3 };

    private const int LegacyHeaderBytes = 8;

    private static RebuildWriterAdapter Writer(string kind) =>
        RebuildWriterAdapter.Create(kind, RebuildWriterAdapter.NewRegistry());

    private static Entity Spawn(World world, long netId, int value)
    {
        Entity e = world.Spawn();
        world.Set(e, new NetId(netId));
        world.Set(e, new RebuildValue { Number = value });
        return e;
    }

    private static void AssertSameSections(byte[] legacy, ReplicationDeltaPacket packet) =>
        Assert.Equal(legacy.AsSpan(LegacyHeaderBytes).ToArray(), packet.Bytes.Span[RebuildWire.HeaderBytes..].ToArray());

    [Theory]
    [MemberData(nameof(Kinds))]
    public void BuildRequiresCaptureStartedEpochAndStableOwner(string kind)
    {
        RebuildWriterAdapter w = Writer(kind);
        Spawn(w.World, 1, 1);
        w.Start(0, 5);
        Assert.Throws<InvalidOperationException>(() => w.Build(0, All, owner: 1));   // nothing captured yet

        w.Capture();
        Assert.Throws<InvalidOperationException>(() => w.Build(1, All, owner: 1));   // slot 1 never started
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Start(1, 0));
        Assert.Throws<ArgumentNullException>(() => w.Start(1, 1, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => w.Start(1, 1, new DeltaRebuildOptions { MaxRetainedProjections = 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => w.Start(1, 1, new DeltaRebuildOptions { MaxKeyframeBytes = 1000, MaxRetainedPayloadBytes = 3000 }));
        Assert.Equal(default(RebuildUsage), w.Usage(1));

        ReplicationDeltaPacket first = w.Build(0, All, owner: 1);
        Assert.Equal(new ReplicationPacketId(5, 1), first.Id);
        Assert.Throws<InvalidOperationException>(() => w.Build(0, All, owner: 2));
        Assert.Throws<InvalidOperationException>(() => w.Build(0, All, owner: null));
        Assert.Equal(first.Id, w.Usage(0).CandidateId);
        Assert.Equal(1, w.Usage(0).RetainedCount);

        Assert.Throws<ArgumentOutOfRangeException>(() => w.Start(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Start(0, 4));
        Assert.Equal(first.Id, w.Usage(0).CandidateId);

        int legacySequence = w.CurrentSeq;
        w.Start(0, 6);
        Assert.Equal(legacySequence, w.CurrentSeq);
        Assert.Equal(default(RebuildUsage), w.Usage(0));
        ReplicationDeltaPacket replacement = w.Build(0, All, owner: 2);
        Assert.Equal(new ReplicationPacketId(6, 1), replacement.Id);
        Assert.True(replacement.IsKeyframe);
        Assert.Null(replacement.Baseline);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void FirstPacketAndExplicitKeyframeStartFromEmpty(string kind)
    {
        RebuildWriterAdapter w = Writer(kind);
        Entity one = Spawn(w.World, 1, 7);
        w.World.Set(one, new RebuildTag());
        w.World.Set(one, new RebuildExt { V = 9 });
        Entity two = Spawn(w.World, 2, 20);

        w.Start(0, 3);
        w.Capture();
        byte[] legacyFull = w.WriteLegacy(9, All);
        ReplicationDeltaPacket first = w.Build(0, All);

        Assert.True(first.IsKeyframe);
        Assert.Null(first.Baseline);
        Assert.Equal(new ReplicationPacketId(3, 1), first.Id);
        Assert.Equal(new RebuildWireHeader(2, 1, 3, 1, 0, 0, 2), RebuildWire.ReadHeader(first.Bytes.Span));
        AssertSameSections(legacyFull, first);

        w.Sent(0, first.Id);
        w.Ack(0, first.Id);
        w.World.Set(one, new RebuildValue { Number = 8 });
        w.World.Despawn(two);
        w.Capture();
        byte[] freshLegacyFull = w.WriteLegacy(10, All);
        ReplicationDeltaPacket keyframe = w.Build(0, All, keyframe: true);

        Assert.True(keyframe.IsKeyframe);
        Assert.Null(keyframe.Baseline);
        Assert.Equal(new RebuildWireHeader(2, 1, 3, 2, 0, 0, 1), RebuildWire.ReadHeader(keyframe.Bytes.Span));
        AssertSameSections(freshLegacyFull, keyframe);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void DeltaKeepsLegacySectionsAndNamesAcknowledgedBaseline(string kind)
    {
        RebuildWriterAdapter w = Writer(kind);
        Entity one = Spawn(w.World, 1, 7);
        w.World.Set(one, new RebuildTag());
        Entity two = Spawn(w.World, 2, 20);

        w.Start(0, 4);
        w.Capture();
        w.WriteLegacy(9, All);
        ReplicationDeltaPacket keyframe = w.Build(0, All);
        w.Sent(0, keyframe.Id);
        w.Ack(0, keyframe.Id);

        w.World.Set(one, new RebuildValue { Number = 8 });
        w.World.Remove<RebuildTag>(one);
        w.World.Despawn(two);
        Spawn(w.World, 3, 30);
        w.Capture();
        byte[] legacyDelta = w.WriteLegacy(9, All);
        ReplicationDeltaPacket delta = w.Build(0, All);

        Assert.False(delta.IsKeyframe);
        Assert.Equal(keyframe.Id, delta.Baseline);
        Assert.Equal(new ReplicationPacketId(4, 2), delta.Id);
        Assert.Equal(new RebuildWireHeader(2, 0, 4, 2, 1, 1, 2), RebuildWire.ReadHeader(delta.Bytes.Span));
        Assert.Equal(new long[] { 2 }, RebuildWire.ReadRemovedNetIds(delta.Bytes.Span));
        AssertSameSections(legacyDelta, delta);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void KeyframeBudgetIncludesEnvelopeBytes(string kind)
    {
        RebuildWriterAdapter w = Writer(kind);
        Spawn(w.World, 1, 1);
        Entity two = Spawn(w.World, 2, 2);
        w.Capture();
        w.Start(0, 1);
        int size = w.Build(0, All).Bytes.Length;

        var exact = new DeltaRebuildOptions { MaxKeyframeBytes = size + 12, EnvelopeBytes = 12 };
        w.Start(0, 2, exact);
        Assert.Equal(size, w.Build(0, All).Bytes.Length);

        w.Start(0, 3, new DeltaRebuildOptions { MaxKeyframeBytes = size + 11, EnvelopeBytes = 12 });
        DeltaRebuildException tooBig = Assert.Throws<DeltaRebuildException>(() => w.Build(0, All));
        Assert.Equal(DeltaRebuildFailure.CapacityExceeded, tooBig.Failure);
        Assert.Equal(default(RebuildUsage), w.Usage(0));

        // The same cap without the envelope charge fits.
        w.Start(0, 4, new DeltaRebuildOptions { MaxKeyframeBytes = size + 11 });
        Assert.Equal(size, w.Build(0, All).Bytes.Length);

        // A routine delta is held to the complete keyframe size of its projection, however small the delta is.
        w.Start(0, 5, exact);
        ReplicationDeltaPacket keyframe = w.Build(0, All);
        w.Sent(0, keyframe.Id);
        w.Ack(0, keyframe.Id);
        w.World.Set(two, new RebuildTag());
        w.Capture();
        DeltaRebuildException deltaTooBig = Assert.Throws<DeltaRebuildException>(() => w.Build(0, All));
        Assert.Equal(DeltaRebuildFailure.CapacityExceeded, deltaTooBig.Failure);
        RebuildUsage usage = w.Usage(0);
        Assert.Null(usage.CandidateId);
        Assert.Equal(keyframe.Id, usage.AcknowledgedId);
        Assert.Equal(1, usage.RetainedCount);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ProjectionLimitsAreCapacityNotPressure(string kind)
    {
        RebuildWriterAdapter w = Writer(kind);
        Entity one = Spawn(w.World, 1, 1);
        w.World.Set(one, new RebuildTag());
        Spawn(w.World, 2, 2);
        w.Capture();

        DeltaRebuildOptions[] impossible =
        {
            new() { MaxEntities = 1 },
            new() { MaxComponents = 2 },
            new() { MaxRetainedPayloadBytes = 7, MaxKeyframeBytes = 1 },
        };
        ulong epoch = 1;
        foreach (DeltaRebuildOptions options in impossible)
        {
            w.Start(0, epoch++, options);
            DeltaRebuildException failure = Assert.Throws<DeltaRebuildException>(() => w.Build(0, All));
            Assert.Equal(DeltaRebuildFailure.CapacityExceeded, failure.Failure);
            Assert.Equal(default(RebuildUsage), w.Usage(0));
        }

        w.Start(0, epoch, new DeltaRebuildOptions { MaxEntities = 2, MaxComponents = 3 });
        Assert.True(w.Build(0, All).IsKeyframe);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void HalfRangeSequenceReportsAmbiguityAndNeedsNewEpoch(string kind)
    {
        RebuildWriterAdapter w = Writer(kind);
        Entity one = Spawn(w.World, 1, 1);
        w.Start(0, 1);
        ReplicationDeltaPacket keyframe = w.CaptureAndSend(0, All);
        w.Ack(0, keyframe.Id);
        w.World.Set(one, new RebuildValue { Number = 2 });
        w.Capture();

        foreach (uint seeded in new[] { keyframe.Id.Sequence + 0x80000000u, keyframe.Id.Sequence })
        {
            w.SeedSequence(0, seeded);
            DeltaRebuildException failure = Assert.Throws<DeltaRebuildException>(() => w.Build(0, All));
            Assert.Equal(DeltaRebuildFailure.SequenceAmbiguous, failure.Failure);
            RebuildUsage usage = w.Usage(0);
            Assert.Null(usage.CandidateId);
            Assert.Equal(keyframe.Id, usage.AcknowledgedId);
            Assert.Equal(1, usage.RetainedCount);
        }

        w.SeedSequence(0, keyframe.Id.Sequence + 0x7fffffffu);
        Assert.Equal(keyframe.Id, w.Build(0, All).Baseline);

        w.Start(0, 2);
        ReplicationDeltaPacket repaired = w.Build(0, All, keyframe: true);
        Assert.Equal(new ReplicationPacketId(2, 1), repaired.Id);
    }
}

internal struct RebuildValue : IComponent { public int Number; }

internal struct RebuildTag : IComponent { }

internal struct RebuildExt : IComponent { public int V; }

internal struct RebuildBlob : IComponent { public int Size; }

/// <summary>
/// One server world and one writer of either shape, with both legacy and format 2 operations, so a test drives the
/// whole-world <see cref="ServerReplicator"/> and the area-of-interest <see cref="AoiDeltaReplicator"/> through the same
/// steps. The whole-world writer ignores interest sets.
/// </summary>
internal abstract class RebuildWriterAdapter
{
    public const ushort ExtId = ReplicationRegistry.FirstExtensionTypeId + 1;
    public const ushort BlobId = ReplicationRegistry.FirstExtensionTypeId + 3;

    public static TheoryData<string> Kinds => new() { nameof(WholeWorld), nameof(Aoi) };

    public World World { get; } = new();

    /// <summary>Value at 1, a zero-byte tag at 2, a framed extension and a variable size framed blob.</summary>
    public static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<RebuildValue>(1, (v, bw) => bw.Write(v.Number), br => new RebuildValue { Number = br.ReadInt32() });
        r.Register<RebuildTag>(2, (_, _) => { }, _ => default);
        r.Register<RebuildExt>(ExtId, (e, bw) => bw.Write(e.V), br => new RebuildExt { V = br.ReadInt32() });
        r.Register<RebuildBlob>(BlobId, (b, bw) => bw.Write(new byte[b.Size]), _ => default);
        return r;
    }

    public static RebuildWriterAdapter Create(string kind, ReplicationRegistry registry) => kind switch
    {
        nameof(WholeWorld) => new WholeWorld(registry),
        nameof(Aoi) => new Aoi(registry),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown writer kind"),
    };

    public abstract int Capture();

    public abstract byte[] WriteLegacy(int slot, IReadOnlySet<long> interest, long? owner = null);

    public abstract ReplicationDeltaPacket Build(int slot, IReadOnlySet<long> interest, long? owner = null,
        bool keyframe = false);

    public abstract void Start(int slot, ulong epoch, DeltaRebuildOptions options);

    public void Start(int slot, ulong epoch) => Start(slot, epoch, new DeltaRebuildOptions());

    public abstract void Sent(int slot, ReplicationPacketId id);

    public abstract void Ack(int slot, ReplicationPacketId id);

    public abstract bool NeedsRepair(int slot);

    public abstract RebuildUsage Usage(int slot);

    public abstract bool TryGetRetained(int slot, ReplicationPacketId id, out ReplicationProjection projection);

    public abstract void SeedSequence(int slot, uint sequence);

    public abstract void Forget(int slot);

    public abstract void SeedLegacySequence(int sequence);

    public abstract void ResetAfterExhaustion();

    public abstract bool Exhausted { get; }

    public abstract int CurrentSeq { get; }

    /// <summary>Captures, builds and commits one packet.</summary>
    public ReplicationDeltaPacket CaptureAndSend(int slot, IReadOnlySet<long> interest, long? owner = null,
        bool keyframe = false)
    {
        Capture();
        ReplicationDeltaPacket packet = Build(slot, interest, owner, keyframe);
        Sent(slot, packet.Id);
        return packet;
    }

    public ReplicationProjection Retained(int slot, ReplicationPacketId id)
    {
        Assert.True(TryGetRetained(slot, id, out ReplicationProjection projection), $"{id} is not retained.");
        return projection;
    }

    /// <summary>The retained projections for <paramref name="ids"/>, each required to be retained.</summary>
    public IEnumerable<ReplicationProjection> RetainedOf(int slot, params ReplicationPacketId[] ids)
    {
        var list = new List<ReplicationProjection>();
        foreach (ReplicationPacketId id in ids) list.Add(Retained(slot, id));
        return list;
    }

    public sealed class WholeWorld : RebuildWriterAdapter
    {
        private readonly ServerReplicator writer;

        public WholeWorld(ReplicationRegistry registry) => writer = new ServerReplicator(registry);

        public override int Capture() => writer.Capture(World);

        public override byte[] WriteLegacy(int slot, IReadOnlySet<long> interest, long? owner = null) =>
            writer.WriteFor(slot, owner);

        public override ReplicationDeltaPacket Build(int slot, IReadOnlySet<long> interest, long? owner = null,
            bool keyframe = false) => writer.BuildRebuildFor(slot, owner, keyframe);

        public override void Start(int slot, ulong epoch, DeltaRebuildOptions options) =>
            writer.StartRebuild(slot, epoch, options);

        public override void Sent(int slot, ReplicationPacketId id) => writer.RecordRebuildSent(slot, id);

        public override void Ack(int slot, ReplicationPacketId id) => writer.AcknowledgeRebuild(slot, id);

        public override bool NeedsRepair(int slot) => writer.RebuildNeedsRepair(slot);

        public override RebuildUsage Usage(int slot) => writer.RebuildUsageForTest(slot);

        public override bool TryGetRetained(int slot, ReplicationPacketId id, out ReplicationProjection projection) =>
            writer.TryGetRetainedProjectionForTest(slot, id, out projection);

        public override void SeedSequence(int slot, uint sequence) => writer.SeedRebuildSequenceForTest(slot, sequence);

        public override void Forget(int slot) => writer.Forget(slot);

        public override void SeedLegacySequence(int sequence) => writer.SeedLegacySequenceForTest(sequence);

        public override void ResetAfterExhaustion() => writer.ResetAfterLegacySequenceExhaustion();

        public override bool Exhausted => writer.LegacySequenceExhausted;

        public override int CurrentSeq => writer.CurrentSeq;
    }

    public sealed class Aoi : RebuildWriterAdapter
    {
        private readonly AoiDeltaReplicator writer;

        public Aoi(ReplicationRegistry registry) => writer = new AoiDeltaReplicator(registry);

        public override int Capture() => writer.BeginTick();

        public override byte[] WriteLegacy(int slot, IReadOnlySet<long> interest, long? owner = null) =>
            writer.WriteFor(slot, World, interest, owner);

        public override ReplicationDeltaPacket Build(int slot, IReadOnlySet<long> interest, long? owner = null,
            bool keyframe = false) => writer.BuildRebuildFor(slot, World, interest, owner, keyframe);

        public override void Start(int slot, ulong epoch, DeltaRebuildOptions options) =>
            writer.StartRebuild(slot, epoch, options);

        public override void Sent(int slot, ReplicationPacketId id) => writer.RecordRebuildSent(slot, id);

        public override void Ack(int slot, ReplicationPacketId id) => writer.AcknowledgeRebuild(slot, id);

        public override bool NeedsRepair(int slot) => writer.RebuildNeedsRepair(slot);

        public override RebuildUsage Usage(int slot) => writer.RebuildUsageForTest(slot);

        public override bool TryGetRetained(int slot, ReplicationPacketId id, out ReplicationProjection projection) =>
            writer.TryGetRetainedProjectionForTest(slot, id, out projection);

        public override void SeedSequence(int slot, uint sequence) => writer.SeedRebuildSequenceForTest(slot, sequence);

        public override void Forget(int slot) => writer.Forget(slot);

        public override void SeedLegacySequence(int sequence) => writer.SeedLegacySequenceForTest(sequence);

        public override void ResetAfterExhaustion() => writer.ResetAfterLegacySequenceExhaustion();

        public override bool Exhausted => writer.LegacySequenceExhausted;

        public override int CurrentSeq => writer.CurrentSeq;
    }
}
