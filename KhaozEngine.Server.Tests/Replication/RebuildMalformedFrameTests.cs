using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Hand-built format 2 bodies that a receiver must reject as <see cref="DeltaRebuildResult.Invalid"/> with
/// <see cref="DeltaRebuildFailure.MalformedPacket"/>, changing nothing, plus the exact read-count oracle.
/// </summary>
public class RebuildMalformedFrameTests
{
    private const ulong Epoch = 5;
    private const ushort ValueId = 1, TagId = 2, ExtId = ReplicationRegistry.FirstExtensionTypeId + 1;
    private const ushort UnknownExtId = ReplicationRegistry.FirstExtensionTypeId + 4;

    /// <summary>A little-endian body builder, independent of the production writer and reader.</summary>
    private sealed class Body
    {
        private readonly MemoryStream stream = new();
        private readonly BinaryWriter writer;

        public Body(byte format = 2, byte flags = 0, ulong epoch = Epoch, uint snapshot = 2, uint baseline = 1)
        {
            writer = new BinaryWriter(stream);
            writer.Write(format);
            writer.Write(flags);
            writer.Write(epoch);
            writer.Write(snapshot);
            writer.Write(baseline);
        }

        public static Body Keyframe(uint snapshot) => new(flags: 1, snapshot: snapshot, baseline: 0);

        public Body I32(int v) { writer.Write(v); return this; }

        public Body I64(long v) { writer.Write(v); return this; }

        public Body U8(byte v) { writer.Write(v); return this; }

        public Body U16(ushort v) { writer.Write(v); return this; }

        public Body Raw(params byte[] bytes) { writer.Write(bytes); return this; }

        public Body Len(int v) { writer.Write7BitEncodedInt(v); return this; }

        /// <summary>A partial entry for <paramref name="netId"/> replacing Value with <paramref name="value"/>.</summary>
        public Body ValueEntry(long netId, int value) => I64(netId).U8(0).I32(0).U16(ValueId).I32(value).U16(0);

        public byte[] ToArray()
        {
            writer.Flush();
            return stream.ToArray();
        }
    }

    // Entity 10 {Value 1, Tag, Ext 3, unknown extension AA BB}, entity 11 {Value 2}.
    private static byte[] InitialKeyframe() => Body.Keyframe(1).I32(0).I32(2)
        .I64(10).U8(1).I32(0).U16(ValueId).I32(1).U16(TagId).U16(ExtId).Len(4).I32(3)
        .U16(UnknownExtId).Len(2).Raw(0xAA, 0xBB).U16(0)
        .I64(11).U8(1).I32(0).U16(ValueId).I32(2).U16(0)
        .ToArray();

    private static readonly Dictionary<string, Func<byte[]>> Cases = new()
    {
        ["short header"] = () => new byte[] { 2, 0, 5, 0 },
        ["unknown revision"] = () => new Body(format: 3).I32(0).I32(0).ToArray(),
        ["unknown flag"] = () => new Body(flags: 2).I32(0).I32(0).ToArray(),
        ["keyframe and unknown flag"] = () => new Body(flags: 3, baseline: 0).I32(0).I32(0).ToArray(),
        ["keyframe names a baseline"] = () => new Body(flags: 1, baseline: 1).I32(0).I32(0).ToArray(),
        ["zero epoch"] = () => new Body(epoch: 0).I32(0).I32(0).ToArray(),
        ["baseline equals snapshot"] = () => new Body(snapshot: 2, baseline: 2).I32(0).I32(0).ToArray(),
        ["baseline newer than snapshot"] = () => new Body(snapshot: 2, baseline: 3).I32(0).I32(0).ToArray(),
        ["negative removed count"] = () => new Body().I32(-1).I32(0).ToArray(),
        ["excessive removed count"] = () => new Body().I32(int.MaxValue).I32(0).ToArray(),
        ["removed count beyond baseline"] = () => new Body().I32(3).I64(10).I64(11).I64(12).I32(0).ToArray(),
        ["negative changed count"] = () => new Body().I32(0).I32(-1).ToArray(),
        ["excessive changed count"] = () => new Body().I32(0).I32(1000).ValueEntry(10, 9).ToArray(),
        ["duplicate removed net id"] = () => new Body().I32(2).I64(11).I64(11).I32(0).ToArray(),
        ["removed net id absent from baseline"] = () => new Body().I32(1).I64(99).I32(0).ToArray(),
        ["duplicate changed net id"] = () => new Body().I32(0).I32(2).ValueEntry(10, 9).ValueEntry(10, 8).ToArray(),
        ["removed and changed overlap"] = () => new Body().I32(1).I64(11).I32(1).ValueEntry(11, 5).ToArray(),
        ["bad entry flag"] = () => new Body().I32(0).I32(1).I64(10).U8(2).I32(0).U16(0).ToArray(),
        ["partial entry absent from baseline"] = () => new Body().I32(0).I32(1).ValueEntry(12, 1).ToArray(),
        ["full entry with component removals"] =
            () => new Body().I32(0).I32(1).I64(12).U8(1).I32(1).U16(TagId).U16(ValueId).I32(1).U16(0).ToArray(),
        ["negative component removal count"] = () => new Body().I32(0).I32(1).I64(10).U8(0).I32(-1).U16(0).ToArray(),
        ["excessive component removal count"] =
            () => new Body().I32(0).I32(1).I64(10).U8(0).I32(int.MaxValue).U16(0).ToArray(),
        ["duplicate component removal"] =
            () => new Body().I32(0).I32(1).I64(10).U8(0).I32(2).U16(TagId).U16(TagId).U16(0).ToArray(),
        ["component removal absent from baseline"] =
            () => new Body().I32(0).I32(1).I64(11).U8(0).I32(1).U16(TagId).U16(0).ToArray(),
        ["component removal of type zero"] =
            () => new Body().I32(0).I32(1).I64(10).U8(0).I32(1).U16(0).U16(0).ToArray(),
        ["removed and replacement type overlap"] =
            () => new Body().I32(0).I32(1).I64(10).U8(0).I32(1).U16(ValueId).U16(ValueId).I32(5).U16(0).ToArray(),
        ["duplicate component id"] =
            () => new Body().I32(0).I32(1).I64(10).U8(0).I32(0).U16(ValueId).I32(5).U16(ValueId).I32(6).U16(0).ToArray(),
        ["unknown built-in"] = () => new Body().I32(0).I32(1).I64(10).U8(0).I32(0).U16(5).I32(0).U16(0).ToArray(),
        ["built-in overread"] = () => new Body().I32(0).I32(1).I64(10).U8(0).I32(0).U16(ValueId).Raw(1, 2).ToArray(),
        ["overlong 7-bit length"] =
            () => new Body().I32(0).I32(1).I64(10).U8(0).I32(0).U16(ExtId).Raw(0x84, 0x80, 0x80, 0x80, 0x80, 0x00)
                .I32(1).U16(0).ToArray(),
        ["negative 7-bit length"] =
            () => new Body().I32(0).I32(1).I64(10).U8(0).I32(0).U16(ExtId).Raw(0xFF, 0xFF, 0xFF, 0xFF, 0x0F)
                .I32(1).U16(0).ToArray(),
        ["truncated 7-bit length"] = () => new Body().I32(0).I32(1).I64(10).U8(0).I32(0).U16(ExtId).Raw(0x80).ToArray(),
        ["framed overread"] =
            () => new Body().I32(0).I32(1).I64(10).U8(0).I32(0).U16(ExtId).Len(100).I32(1).U16(0).ToArray(),
        ["framed underread"] =
            () => new Body().I32(0).I32(1).I64(10).U8(0).I32(0).U16(ExtId).Len(6).I32(1).Raw(0, 0).U16(0).ToArray(),
        ["known extension reader past stated length"] =
            () => new Body().I32(0).I32(1).I64(10).U8(0).I32(0).U16(ExtId).Len(2).Raw(1, 2).U16(0).ToArray(),
        ["opaque frame overread"] =
            () => new Body().I32(0).I32(1).I64(10).U8(0).I32(0).U16(UnknownExtId).Len(50).Raw(1, 2).U16(0).ToArray(),
        ["missing entity terminator"] = () => new Body().I32(0).I32(1).I64(10).U8(0).I32(0).U16(ValueId).I32(9).ToArray(),
        ["trailing bytes"] = () => new Body().I32(0).I32(1).ValueEntry(10, 9).U8(0).ToArray(),
        ["keyframe partial entry"] = () => Body.Keyframe(2).I32(0).I32(1).ValueEntry(10, 1).ToArray(),
        ["keyframe removed entities"] = () => Body.Keyframe(2).I32(1).I64(11).I32(0).ToArray(),
        ["keyframe component removals"] =
            () => Body.Keyframe(2).I32(0).I32(1).I64(10).U8(1).I32(1).U16(TagId).U16(ValueId).I32(1).U16(0).ToArray(),
    };

    public static TheoryData<string> CaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in Cases.Keys) data.Add(name);
            return data;
        }
    }

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<RebuildValue>(ValueId, (v, bw) => bw.Write(v.Number), br => new RebuildValue { Number = br.ReadInt32() });
        r.Register<RebuildTag>(TagId, (_, _) => { }, _ => default);
        r.Register<RebuildExt>(ExtId, (e, bw) => bw.Write(e.V), br => new RebuildExt { V = br.ReadInt32() });
        return r;
    }

    private static (World Live, ClientReplicationView View, ClientDeltaRebuild Client) Receiver()
    {
        ReplicationRegistry registry = NewRegistry();
        var view = new ClientReplicationView(registry);
        var client = new ClientDeltaRebuild(registry, view, new DeltaRebuildOptions());
        client.ExpectEpoch(Epoch);
        var live = new World();
        Assert.Equal(DeltaRebuildResult.Accepted, client.TryApply(live, InitialKeyframe(), out _, out _, out _));
        return (live, view, client);
    }

    [Fact]
    public void ValidBodiesFromTheBuilderAreAccepted()
    {
        (World live, ClientReplicationView view, ClientDeltaRebuild client) = Receiver();
        Assert.True(client.TryGetRetainedForTest(new ReplicationPacketId(Epoch, 1), out ReplicationProjection initial));
        Assert.Contains(ProjectionDump.Of(initial),
            entry => entry.TypeId == UnknownExtId && entry.Payload.SequenceEqual(new byte[] { 0xAA, 0xBB }));

        byte[] delta = new Body().I32(1).I64(11).I32(1)
            .I64(10).U8(0).I32(1).U16(TagId).U16(ValueId).I32(9).U16(0).ToArray();
        Assert.Equal(DeltaRebuildResult.Accepted, client.TryApply(live, delta, out _, out _, out _));
        view.TryGetEntity(10, out Entity e);
        Assert.Equal(9, live.Get<RebuildValue>(e).Number);
        Assert.False(live.TryGet<RebuildTag>(e, out _));
        Assert.Equal(3, live.Get<RebuildExt>(e).V);
        Assert.Equal(new long[] { 10 }, view.Entities.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void MalformedBodyIsInvalidAndChangesNothing(string name)
    {
        (World live, ClientReplicationView view, ClientDeltaRebuild client) = Receiver();
        view.TryGetEntity(10, out Entity ten);
        ReplicationPacketId initial = new(Epoch, 1);
        var pins = new HashSet<ReplicationPacketId>(client.PinnedIdsForTest);
        byte[][] presentation = view.PresentationArraysForTest().ToArray();

        DeltaRebuildResult result = client.TryApply(live, Cases[name](), out ReplicationPacketId accepted,
            out ReplicationPacketId? missing, out string? error);

        Assert.Equal(DeltaRebuildResult.Invalid, result);
        Assert.Equal(DeltaRebuildFailure.MalformedPacket, client.LastFailure);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Equal(default, accepted);
        Assert.Null(missing);
        Assert.Equal(initial, client.LatestAcceptedId);
        Assert.Equal(initial, client.AckTarget);
        Assert.Equal(1, client.PublicationCountForTest);
        Assert.Equal(1, client.RetainedCountForTest);
        Assert.True(pins.SetEquals(client.PinnedIdsForTest));
        Assert.Equal<byte[]>(presentation, view.PresentationArraysForTest().ToArray(), ReferenceEqualityComparer.Instance);
        Assert.Equal(new long[] { 10, 11 }, view.Entities.Keys.Order());
        Assert.Equal(1, live.Get<RebuildValue>(ten).Number);
        Assert.True(live.TryGet<RebuildTag>(ten, out _));
        Assert.Equal(3, live.Get<RebuildExt>(ten).V);

        // The receiver stays consistent: the next valid packet still reconstructs from the retained baseline.
        Assert.Equal(DeltaRebuildResult.Accepted,
            client.TryApply(live, new Body(snapshot: 3).I32(0).I32(1).ValueEntry(10, 4).ToArray(), out _, out _, out _));
        Assert.Equal(4, live.Get<RebuildValue>(ten).Number);
        Assert.Equal(DeltaRebuildFailure.None, client.LastFailure);
    }

    [Fact]
    public void PersistOnlyRegisteredFrameIsMalformed()
    {
        // Registration keeps every built-in on the Replicate channel, so only an extension can be Persist-only.
        const ushort persistId = ReplicationRegistry.FirstExtensionTypeId + 6;
        Assert.Throws<ArgumentException>(() => new ReplicationRegistry().Register<RebuildValue>(7,
            (v, bw) => bw.Write(v.Number), br => new RebuildValue { Number = br.ReadInt32() },
            channels: ReplicationChannels.Persist));
        ReplicationRegistry registry = NewRegistry();
        registry.Register<RebuildOpaque>(persistId, (o, bw) => bw.Write(o.V), br => new RebuildOpaque { V = br.ReadInt32() },
            channels: ReplicationChannels.Persist);
        var view = new ClientReplicationView(registry);
        var client = new ClientDeltaRebuild(registry, view, new DeltaRebuildOptions());
        client.ExpectEpoch(Epoch);
        var live = new World();
        Assert.Equal(DeltaRebuildResult.Accepted, client.TryApply(live, InitialKeyframe(), out _, out _, out _));

        byte[] delta = new Body().I32(0).I32(1).I64(10).U8(0).I32(0).U16(persistId).Len(4).I32(1).U16(0).ToArray();
        Assert.Equal(DeltaRebuildResult.Invalid, client.TryApply(live, delta, out _, out _, out string? error));
        Assert.Equal(DeltaRebuildFailure.MalformedPacket, client.LastFailure);
        Assert.Contains("never replicates", error);
        Assert.Equal(new ReplicationPacketId(Epoch, 1), client.LatestAcceptedId);
    }

    [Fact]
    public void FullEntryForBaselineEntityDropsAndReadsNoCarriedFrames()
    {
        var codec = new CountingCodec();
        var view = new ClientReplicationView(codec.Registry);
        var client = new ClientDeltaRebuild(codec.Registry, view, new DeltaRebuildOptions());
        client.ExpectEpoch(Epoch);
        var live = new World();
        byte[] keyframe = Body.Keyframe(1).I32(0).I32(2)
            .I64(10).U8(1).I32(0).U16(CountingCodec.BuiltinId).I32(1).U16(CountingCodec.ExtensionId).Len(4).I32(2).U16(0)
            .I64(11).U8(1).I32(0).U16(CountingCodec.BuiltinId).I32(3).U16(0)
            .ToArray();
        Assert.Equal(DeltaRebuildResult.Accepted, client.TryApply(live, keyframe, out _, out _, out _));
        int builtinReads = codec.BuiltinReads, extensionReads = codec.ExtensionReads;

        // A full entry for net id 10, which the baseline holds with two frames, carrying only the built-in.
        byte[] full = new Body().I32(0).I32(1).I64(10).U8(1).I32(0).U16(CountingCodec.BuiltinId).I32(5).U16(0).ToArray();
        Assert.Equal(DeltaRebuildResult.Accepted, client.TryApply(live, full, out _, out _, out _));

        // One received built-in for 10, one carried built-in for 11, and nothing carried for 10.
        Assert.Equal(builtinReads + 2, codec.BuiltinReads);
        Assert.Equal(extensionReads, codec.ExtensionReads);
        Assert.True(client.TryGetRetainedForTest(new ReplicationPacketId(Epoch, 2), out ReplicationProjection rebuilt));
        Assert.Equal(new[] { (10L, CountingCodec.BuiltinId), (11L, CountingCodec.BuiltinId) },
            ProjectionDump.Of(rebuilt).Select(e => (e.NetId, e.TypeId)));
        view.TryGetEntity(10, out Entity ten);
        Assert.Equal(5, live.Get<CountedBuiltin>(ten).N);
        Assert.False(live.TryGet<CountedExtension>(ten, out _));
    }

    [Fact]
    public void ReadCountsMatchReconstructedProjection()
    {
        var serverCodec = new CountingCodec();
        serverCodec.Registry.Register<RebuildOpaque>(RebuildLink.OpaqueId, (o, bw) => bw.Write(o.V),
            br => new RebuildOpaque { V = br.ReadInt32() });
        var clientCodec = new CountingCodec();
        var link = new RebuildLink(serverRegistry: serverCodec.Registry, clientRegistry: clientCodec.Registry);
        Entity one = ClientDeltaRebuildTests.SpawnCounted(link, 1, 1, 2);
        link.ServerWorld.Set(one, new RebuildOpaque { V = 9 });
        ClientDeltaRebuildTests.SpawnCounted(link, 2, 3, null);
        Entity three = link.ServerWorld.Spawn();
        link.ServerWorld.Set(three, new NetId(3));
        link.ServerWorld.Set(three, new CountedExtension { N = 4 });
        link.Interest.Add(3);

        // Keyframe: e1 {B, E, opaque}, e2 {B}, e3 {E}. Four known components, the opaque frame is never read.
        ReplicationDeltaPacket initial = link.SendDeliverAck();
        Assert.Equal((2, 2), (clientCodec.BuiltinReads, clientCodec.ExtensionReads));
        Assert.Equal(KnownComponents(link, initial.Id, clientCodec.Registry), clientCodec.Reads);

        // One received replacement plus three carried-over known components.
        link.ServerWorld.Set(one, new CountedBuiltin { N = 5 });
        AssertReadsMatch(link, clientCodec, link.Send(), expectedKnown: 4);

        // From the same baseline: e1 changed, e3 left, e4 entered with two components. Five known in the result.
        link.ServerWorld.Despawn(three);
        link.Interest.Remove(3);
        ClientDeltaRebuildTests.SpawnCounted(link, 4, 7, 8);
        ReplicationDeltaPacket third = link.Send();
        Assert.Equal(initial.Id, third.Baseline);
        AssertReadsMatch(link, clientCodec, third, expectedKnown: 5);

        int reads = clientCodec.Reads;
        Assert.Equal(DeltaRebuildResult.DuplicateOrStale, link.Deliver(third));
        Assert.Equal(reads, clientCodec.Reads);

        // A rejected packet reads at most its received known components and publishes nothing.
        link.ServerWorld.Set(one, new CountedBuiltin { N = 6 });
        ReplicationDeltaPacket fourth = link.Send();
        int publications = link.Client.PublicationCountForTest;
        Assert.Equal(DeltaRebuildResult.Invalid, link.Deliver(fourth.Bytes.ToArray().Append((byte)1).ToArray()));
        Assert.InRange(clientCodec.Reads - reads, 0, 3);   // e1 B and the two frames of e4
        Assert.Equal(publications, link.Client.PublicationCountForTest);
        AssertReadsMatch(link, clientCodec, fourth, expectedKnown: 5);
    }

    private static void AssertReadsMatch(RebuildLink link, CountingCodec codec, ReplicationDeltaPacket packet,
        int expectedKnown)
    {
        int before = codec.Reads;
        Assert.Equal(DeltaRebuildResult.Accepted, link.Deliver(packet));
        Assert.Equal(expectedKnown, KnownComponents(link, packet.Id, codec.Registry));
        Assert.Equal(expectedKnown, codec.Reads - before);
    }

    private static int KnownComponents(RebuildLink link, ReplicationPacketId id, ReplicationRegistry registry) =>
        ProjectionDump.Of(link.ClientRetained(id)).Count(entry => registry.IsRegistered(entry.TypeId));
}
