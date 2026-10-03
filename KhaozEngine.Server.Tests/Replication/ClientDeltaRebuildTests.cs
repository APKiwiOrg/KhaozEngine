using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

internal struct RebuildPos : IComponent { public float X; }

internal struct RebuildOpaque : IComponent { public int V; }

internal struct RebuildLocal : IComponent { public int Note; }

internal struct RebuildFill : IComponent { public int Size; public byte Fill; }

/// <summary>
/// One format 2 server writer of either shape linked to one client receiver. The server registers a framed extension
/// the client lacks, so retained client projections carry it as opaque bytes. Packets are delivered by hand, so a test
/// drops, reorders and duplicates them deterministically.
/// </summary>
internal sealed class RebuildLink
{
    public const int Slot = 0;
    public const ulong Epoch = 7;
    public const ushort ValueId = 1, TagId = 2, PosId = 3;
    public const ushort ExtId = ReplicationRegistry.FirstExtensionTypeId + 1;
    public const ushort FillId = ReplicationRegistry.FirstExtensionTypeId + 2;
    public const ushort OpaqueId = ReplicationRegistry.FirstExtensionTypeId + 5;

    public RebuildLink(string kind = nameof(RebuildWriterAdapter.Aoi), DeltaRebuildOptions? server = null,
        DeltaRebuildOptions? client = null, ReplicationRegistry? serverRegistry = null,
        ReplicationRegistry? clientRegistry = null, bool expect = true)
    {
        ServerRegistry = serverRegistry ?? NewRegistry(includeOpaque: true);
        ClientRegistry = clientRegistry ?? NewRegistry(includeOpaque: false);
        Server = RebuildWriterAdapter.Create(kind, ServerRegistry);
        Server.Start(Slot, Epoch, server ?? new DeltaRebuildOptions());
        View = new ClientReplicationView(ClientRegistry);
        Client = new ClientDeltaRebuild(ClientRegistry, View, client ?? new DeltaRebuildOptions());
        if (expect) Client.ExpectEpoch(Epoch);
    }

    public ReplicationRegistry ServerRegistry { get; }

    public ReplicationRegistry ClientRegistry { get; }

    public RebuildWriterAdapter Server { get; }

    public World ServerWorld => Server.World;

    public World Live { get; } = new();

    public ClientReplicationView View { get; }

    public ClientDeltaRebuild Client { get; }

    public HashSet<long> Interest { get; } = new();

    public ReplicationPacketId LastAccepted { get; private set; }

    public ReplicationPacketId? LastMissing { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Value 1, zero-byte tag 2, interpolated position 3, framed extension, variable framed fill, and on the
    /// server only a framed extension the client never registers.</summary>
    public static ReplicationRegistry NewRegistry(bool includeOpaque)
    {
        var r = new ReplicationRegistry();
        r.Register<RebuildValue>(ValueId, (v, bw) => bw.Write(v.Number), br => new RebuildValue { Number = br.ReadInt32() });
        r.Register<RebuildTag>(TagId, (_, _) => { }, _ => default);
        r.Register<RebuildPos>(PosId, (p, bw) => bw.Write(p.X), br => new RebuildPos { X = br.ReadSingle() },
            lerp: (a, b, t) => new RebuildPos { X = a.X + ((b.X - a.X) * t) });
        r.Register<RebuildExt>(ExtId, (e, bw) => bw.Write(e.V), br => new RebuildExt { V = br.ReadInt32() });
        r.Register<RebuildFill>(FillId, (f, bw) =>
        {
            for (int i = 0; i < f.Size; i++) bw.Write(f.Fill);
        }, br =>
        {
            byte[] bytes = br.ReadBytes((int)(br.BaseStream.Length - br.BaseStream.Position));
            return new RebuildFill { Size = bytes.Length, Fill = bytes.Length > 0 ? bytes[0] : (byte)0 };
        });
        if (includeOpaque)
            r.Register<RebuildOpaque>(OpaqueId, (o, bw) => bw.Write(o.V), br => new RebuildOpaque { V = br.ReadInt32() });
        return r;
    }

    public Entity Spawn(long netId, int value)
    {
        Entity e = ServerWorld.Spawn();
        ServerWorld.Set(e, new NetId(netId));
        ServerWorld.Set(e, new RebuildValue { Number = value });
        Interest.Add(netId);
        return e;
    }

    /// <summary>Captures, builds and commits one packet without delivering it.</summary>
    public ReplicationDeltaPacket Send(bool keyframe = false)
    {
        Server.Capture();
        ReplicationDeltaPacket packet = Server.Build(Slot, Interest, null, keyframe);
        Server.Sent(Slot, packet.Id);
        return packet;
    }

    public DeltaRebuildResult Deliver(ReplicationDeltaPacket packet) => Deliver(packet.Bytes);

    public DeltaRebuildResult Deliver(ReadOnlyMemory<byte> body)
    {
        DeltaRebuildResult result = Client.TryApply(Live, body, out ReplicationPacketId accepted,
            out ReplicationPacketId? missing, out string? error);
        LastAccepted = accepted;
        LastMissing = missing;
        LastError = error;
        return result;
    }

    public void Ack(ReplicationDeltaPacket packet) => Server.Ack(Slot, packet.Id);

    /// <summary>Sends and delivers one packet, requiring acceptance.</summary>
    public ReplicationDeltaPacket SendDeliver(bool keyframe = false)
    {
        ReplicationDeltaPacket packet = Send(keyframe);
        Assert.Equal(DeltaRebuildResult.Accepted, Deliver(packet));
        Assert.Equal(packet.Id, LastAccepted);
        return packet;
    }

    public ReplicationDeltaPacket SendDeliverAck(bool keyframe = false)
    {
        ReplicationDeltaPacket packet = SendDeliver(keyframe);
        Ack(packet);
        return packet;
    }

    public Entity LiveEntity(long netId)
    {
        Assert.True(View.TryGetEntity(netId, out Entity e), $"net id {netId} is not live");
        return e;
    }

    public ReplicationProjection ClientRetained(ReplicationPacketId id)
    {
        Assert.True(Client.TryGetRetainedForTest(id, out ReplicationProjection projection), $"{id} is not retained");
        return projection;
    }

    /// <summary>The client's retained projection equals the server's, opaque and zero-byte frames included.</summary>
    public void AssertRetainedMatchesServer(ReplicationPacketId id)
    {
        IReadOnlyList<ProjectionEntry> serverProjectionBytes = ProjectionDump.Of(Server.Retained(Slot, id));
        IReadOnlyList<ProjectionEntry> retainedProjectionBytes = ProjectionDump.Of(ClientRetained(id));
        ProjectionDump.AssertEqual(serverProjectionBytes, retainedProjectionBytes);
    }

    /// <summary>The body with its snapshot, baseline or epoch field rewritten, for header-only cases.</summary>
    public static byte[] Rewrite(ReplicationDeltaPacket packet, ulong? epoch = null, uint? snapshot = null,
        uint? baseline = null)
    {
        byte[] body = packet.Bytes.ToArray();
        if (epoch is ulong e) BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(2), e);
        if (snapshot is uint s) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(10), s);
        if (baseline is uint b) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(14), b);
        return body;
    }
}

/// <summary>
/// Format 2 reconstruction: every accepted packet rebuilds from its exact named baseline, never from the live world,
/// and the five presence rows of the specification hold whether the intermediate packet arrived or not.
/// </summary>
public class ClientDeltaRebuildTests
{
    public static TheoryData<string> Kinds => RebuildWriterAdapter.Kinds;

    public static TheoryData<string, string, bool> PresenceRows
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (string kind in new[] { nameof(RebuildWriterAdapter.WholeWorld), nameof(RebuildWriterAdapter.Aoi) })
                foreach (string row in new[] { "ValueReverts", "ComponentReadded", "AddedComponentRemoved",
                             "EntityLeaveRestored", "EnteredEntityRemoved" })
                {
                    data.Add(kind, row, true);
                    data.Add(kind, row, false);
                }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EmptyDeltaRestoresExactNamedBaseline(string kind)
    {
        var link = new RebuildLink(kind);
        Entity e = link.Spawn(1, 1);
        link.ServerWorld.Set(e, new RebuildTag());
        link.ServerWorld.Set(e, new RebuildOpaque { V = 44 });

        ReplicationDeltaPacket initial = link.SendDeliverAck();
        link.ServerWorld.Set(e, new RebuildValue { Number = 2 });
        ReplicationDeltaPacket intermediate = link.SendDeliver();
        Entity entity = link.LiveEntity(1);
        Assert.Equal(2, link.Live.Get<RebuildValue>(entity).Number);

        link.ServerWorld.Set(e, new RebuildValue { Number = 1 });
        ReplicationDeltaPacket reverted = link.Send();
        Assert.Equal(initial.Id, reverted.Baseline);
        Assert.Equal(0, RebuildWire.ReadHeader(reverted.Bytes.Span).ChangedCount);

        DeltaRebuildResult result = link.Deliver(reverted);
        ClientReplicationView view = link.View;
        World live = link.Live;
        long[] expectedNetIds = { 1 };

        Assert.Equal(DeltaRebuildResult.Accepted, result);
        Assert.Equal(1, live.Get<RebuildValue>(entity).Number);
        Assert.True(live.TryGet<RebuildTag>(entity, out _));
        link.AssertRetainedMatchesServer(reverted.Id);
        link.AssertRetainedMatchesServer(intermediate.Id);
        Assert.Equal(expectedNetIds, view.Entities.Keys.Order());
        Assert.Equal(reverted.Id, link.Client.LatestAcceptedId);
        Assert.Equal(reverted.Id, link.Client.AckTarget);
        Assert.Equal(DeltaRebuildFailure.None, link.Client.LastFailure);
    }

    [Theory]
    [MemberData(nameof(PresenceRows))]
    public void RebuildPresenceMatrix(string kind, string row, bool deliverIntermediate)
    {
        var link = new RebuildLink(kind);
        bool aoi = kind == nameof(RebuildWriterAdapter.Aoi);
        Entity one = link.Spawn(1, 1);
        link.ServerWorld.Set(one, new RebuildTag());
        link.ServerWorld.Set(one, new RebuildExt { V = 3 });
        link.ServerWorld.Set(one, new RebuildOpaque { V = 5 });
        Entity two = link.Spawn(2, 20);
        link.ServerWorld.Set(two, new RebuildPos { X = 2.5f });

        ReplicationDeltaPacket initial = link.SendDeliverAck();
        var expectedNetIds = new long[] { 1, 2 };
        Entity three = default;

        // Intermediate state.
        switch (row)
        {
            case "ValueReverts":
                link.ServerWorld.Set(one, new RebuildValue { Number = 2 });
                break;
            case "ComponentReadded":
                link.ServerWorld.Remove<RebuildExt>(one);
                break;
            case "AddedComponentRemoved":
                link.ServerWorld.Set(two, new RebuildExt { V = 9 });
                break;
            case "EntityLeaveRestored":
                if (aoi) link.Interest.Remove(2);
                else link.ServerWorld.Despawn(two);
                break;
            case "EnteredEntityRemoved":
                three = link.Spawn(3, 30);
                break;
        }
        ReplicationDeltaPacket intermediate = link.Send();
        if (deliverIntermediate) Assert.Equal(DeltaRebuildResult.Accepted, link.Deliver(intermediate));

        // Current state, equal to the initial projection.
        switch (row)
        {
            case "ValueReverts":
                link.ServerWorld.Set(one, new RebuildValue { Number = 1 });
                break;
            case "ComponentReadded":
                link.ServerWorld.Set(one, new RebuildExt { V = 3 });
                break;
            case "AddedComponentRemoved":
                link.ServerWorld.Remove<RebuildExt>(two);
                break;
            case "EntityLeaveRestored":
                if (aoi) link.Interest.Add(2);
                else
                {
                    two = link.Spawn(2, 20);
                    link.ServerWorld.Set(two, new RebuildPos { X = 2.5f });
                }
                break;
            case "EnteredEntityRemoved":
                if (aoi) link.Interest.Remove(3);
                else link.ServerWorld.Despawn(three);
                break;
        }
        ReplicationDeltaPacket current = link.Send();
        Assert.Equal(initial.Id, current.Baseline);
        RebuildWireHeader header = RebuildWire.ReadHeader(current.Bytes.Span);
        Assert.Equal(0, header.RemovedCount);
        Assert.Equal(0, header.ChangedCount);

        Assert.Equal(DeltaRebuildResult.Accepted, link.Deliver(current));
        link.AssertRetainedMatchesServer(current.Id);
        ProjectionDump.AssertEqual(ProjectionDump.Of(link.ClientRetained(initial.Id)),
            ProjectionDump.Of(link.ClientRetained(current.Id)));
        Assert.Equal(expectedNetIds, link.View.Entities.Keys.Order());

        Entity liveOne = link.LiveEntity(1);
        Entity liveTwo = link.LiveEntity(2);
        Assert.Equal(1, link.Live.Get<RebuildValue>(liveOne).Number);
        Assert.True(link.Live.TryGet<RebuildTag>(liveOne, out _));
        Assert.Equal(3, link.Live.Get<RebuildExt>(liveOne).V);
        Assert.Equal(20, link.Live.Get<RebuildValue>(liveTwo).Number);
        Assert.Equal(2.5f, link.Live.Get<RebuildPos>(liveTwo).X);
        Assert.False(link.Live.TryGet<RebuildExt>(liveTwo, out _));
        Assert.False(link.View.TryGetEntity(3, out _));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void FullEntryReplacesEntireRegisteredSet(string kind)
    {
        var link = new RebuildLink(kind);
        link.Spawn(1, 1);
        link.SendDeliverAck();

        Entity two = link.Spawn(2, 5);
        link.ServerWorld.Set(two, new RebuildTag());
        link.ServerWorld.Set(two, new RebuildExt { V = 7 });
        link.ServerWorld.Set(two, new RebuildPos { X = 1f });
        link.SendDeliver();
        Entity liveTwo = link.LiveEntity(2);
        link.Live.Set(liveTwo, new RebuildLocal { Note = 77 });

        link.ServerWorld.Set(two, new RebuildValue { Number = 6 });
        link.ServerWorld.Remove<RebuildTag>(two);
        link.ServerWorld.Remove<RebuildExt>(two);
        link.ServerWorld.Remove<RebuildPos>(two);
        ReplicationDeltaPacket current = link.Send();
        (long netId, byte isNew) = FirstChangedEntry(current.Bytes.Span);
        Assert.Equal((2L, (byte)1), (netId, isNew));

        Assert.Equal(DeltaRebuildResult.Accepted, link.Deliver(current));
        link.AssertRetainedMatchesServer(current.Id);
        Assert.Equal(liveTwo, link.LiveEntity(2));
        Assert.Equal(6, link.Live.Get<RebuildValue>(liveTwo).Number);
        Assert.False(link.Live.TryGet<RebuildTag>(liveTwo, out _));
        Assert.False(link.Live.TryGet<RebuildExt>(liveTwo, out _));
        Assert.False(link.Live.TryGet<RebuildPos>(liveTwo, out _));
        Assert.Equal(77, link.Live.Get<RebuildLocal>(liveTwo).Note);
    }

    [Fact]
    public void InterpolatedLiveWorldIsNeverTheBaseline()
    {
        var link = new RebuildLink();
        Entity e = link.Spawn(1, 1);
        link.ServerWorld.Set(e, new RebuildPos { X = 0f });
        link.SendDeliverAck();
        link.View.RecordInterpolationSample(0.0);

        link.ServerWorld.Set(e, new RebuildPos { X = 10f });
        link.SendDeliver();
        link.View.RecordInterpolationSample(0.1);
        Entity live = link.LiveEntity(1);
        link.View.InterpolateAt(link.Live, 0.05);
        link.Live.Set(live, new RebuildValue { Number = 99 });
        Assert.Equal(5f, link.Live.Get<RebuildPos>(live).X, 3);

        link.ServerWorld.Set(e, new RebuildPos { X = 20f });
        ReplicationDeltaPacket current = link.Send();
        Assert.Equal(1, RebuildWire.ReadHeader(current.Bytes.Span).ChangedCount);
        Assert.Equal(DeltaRebuildResult.Accepted, link.Deliver(current));
        link.AssertRetainedMatchesServer(current.Id);
        Assert.Equal(1, link.Live.Get<RebuildValue>(live).Number);
        Assert.Equal(20f, link.Live.Get<RebuildPos>(live).X);

        // Acceptance recorded no interpolation sample: the newest history sample is still the 0.1 one, until the
        // caller's ingest path stamps the accepted projection.
        link.View.InterpolateAt(link.Live, 0.2);
        Assert.Equal(10f, link.Live.Get<RebuildPos>(live).X);
        link.View.RecordInterpolationSample(0.2);
        link.View.InterpolateAt(link.Live, 0.2);
        Assert.Equal(20f, link.Live.Get<RebuildPos>(live).X);
    }

    [Fact]
    public void MissingBaselineLeavesEverythingUnchanged()
    {
        var link = new RebuildLink(client: new DeltaRebuildOptions { MaxRetainedProjections = 4 });
        Entity one = link.Spawn(1, 1);
        Entity two = link.Spawn(2, 2);
        link.ServerWorld.Set(two, new RebuildPos { X = 1f });
        ReplicationDeltaPacket initial = link.SendDeliverAck();
        link.View.RecordInterpolationSample(0.0);

        var sent = new List<ReplicationDeltaPacket>();
        for (int value = 2; value <= 5; value++)
        {
            link.ServerWorld.Set(one, new RebuildValue { Number = value });
            link.ServerWorld.Set(two, new RebuildPos { X = value });
            sent.Add(link.SendDeliver());
            link.View.RecordInterpolationSample(value * 0.1);
        }
        ReplicationDeltaPacket pruned = sent[0];
        Assert.False(link.Client.TryGetRetainedForTest(pruned.Id, out _));
        Assert.True(link.Client.TryGetRetainedForTest(initial.Id, out _));

        link.Ack(pruned);   // a delayed ack for a projection the client has since pruned
        link.ServerWorld.Set(one, new RebuildValue { Number = 6 });
        ReplicationDeltaPacket current = link.Send();
        Assert.Equal(pruned.Id, current.Baseline);

        Entity liveOne = link.LiveEntity(1), liveTwo = link.LiveEntity(2);
        long[] netIds = link.View.Entities.Keys.Order().ToArray();
        byte[][] presentation = link.View.PresentationArraysForTest().ToArray();
        var pins = new HashSet<ReplicationPacketId>(link.Client.PinnedIdsForTest);
        ReplicationPacketId? latest = link.Client.LatestAcceptedId, ackTarget = link.Client.AckTarget;
        int count = link.Client.RetainedCountForTest, bytes = link.Client.RetainedBytesForTest;
        int publications = link.Client.PublicationCountForTest;

        Assert.Equal(DeltaRebuildResult.MissingBaseline, link.Deliver(current));
        Assert.Equal(pruned.Id, link.LastMissing);
        Assert.Equal(default, link.LastAccepted);
        Assert.Equal(DeltaRebuildFailure.None, link.Client.LastFailure);

        Assert.Equal(liveOne, link.LiveEntity(1));
        Assert.Equal(liveTwo, link.LiveEntity(2));
        Assert.Equal(5, link.Live.Get<RebuildValue>(liveOne).Number);
        Assert.Equal(5f, link.Live.Get<RebuildPos>(liveTwo).X);
        Assert.Equal(netIds, link.View.Entities.Keys.Order());
        Assert.Equal<byte[]>(presentation, link.View.PresentationArraysForTest().ToArray(), ReferenceEqualityComparer.Instance);
        Assert.True(pins.SetEquals(link.Client.PinnedIdsForTest));
        Assert.Equal(latest, link.Client.LatestAcceptedId);
        Assert.Equal(ackTarget, link.Client.AckTarget);
        Assert.Equal(count, link.Client.RetainedCountForTest);
        Assert.Equal(bytes, link.Client.RetainedBytesForTest);
        Assert.Equal(publications, link.Client.PublicationCountForTest);
    }

    [Fact]
    public void StaleBodyIsNotDecoded()
    {
        var serverCodec = new CountingCodec();
        var clientCodec = new CountingCodec();
        var link = new RebuildLink(serverRegistry: serverCodec.Registry, clientRegistry: clientCodec.Registry);
        Entity e = SpawnCounted(link, 1, 1, 2);
        ReplicationDeltaPacket initial = link.SendDeliverAck();
        link.ServerWorld.Set(e, new CountedBuiltin { N = 5 });
        ReplicationDeltaPacket second = link.SendDeliver();
        int reads = clientCodec.Reads;
        int publications = link.Client.PublicationCountForTest;

        Assert.Equal(DeltaRebuildResult.DuplicateOrStale, link.Deliver(second));
        Assert.Equal(DeltaRebuildResult.DuplicateOrStale, link.Deliver(initial));
        byte[] corruptStale = second.Bytes.ToArray();
        corruptStale.AsSpan(RebuildWire.HeaderBytes).Fill(0xFF);
        Assert.Equal(DeltaRebuildResult.DuplicateOrStale, link.Deliver(corruptStale));
        Assert.Equal(DeltaRebuildFailure.None, link.Client.LastFailure);
        Assert.Equal(reads, clientCodec.Reads);
        Assert.Equal(publications, link.Client.PublicationCountForTest);
        Assert.Equal(second.Id, link.Client.LatestAcceptedId);

        link.ServerWorld.Set(e, new CountedBuiltin { N = 6 });
        ReplicationDeltaPacket third = link.Send();
        byte[] trailing = third.Bytes.ToArray().Append((byte)0).ToArray();
        Assert.Equal(DeltaRebuildResult.Invalid, link.Deliver(trailing));
        Assert.Equal(DeltaRebuildFailure.MalformedPacket, link.Client.LastFailure);
        Assert.NotNull(link.LastError);
        Assert.Equal(publications, link.Client.PublicationCountForTest);
        Assert.Equal(5, link.Live.Get<CountedBuiltin>(link.LiveEntity(1)).N);

        Assert.Equal(DeltaRebuildResult.Accepted, link.Deliver(third));
        Assert.Equal(6, link.Live.Get<CountedBuiltin>(link.LiveEntity(1)).N);
    }

    [Fact]
    public void OldEpochCannotRestartPublication()
    {
        var link = new RebuildLink();
        Entity e = link.Spawn(1, 1);
        link.SendDeliverAck();
        link.ServerWorld.Set(e, new RebuildValue { Number = 2 });
        ReplicationDeltaPacket oldDelta = link.Send();
        ReplicationDeltaPacket oldKeyframe = link.Send(keyframe: true);
        Entity live = link.LiveEntity(1);
        int publications = link.Client.PublicationCountForTest;

        link.Client.ExpectEpoch(8);
        Assert.Null(link.Client.LatestAcceptedId);
        Assert.Null(link.Client.AckTarget);
        Assert.Equal(0, link.Client.RetainedCountForTest);
        Assert.Equal(1, link.Live.Get<RebuildValue>(live).Number);

        Assert.Equal(DeltaRebuildResult.DuplicateOrStale, link.Deliver(oldDelta));
        Assert.Equal(DeltaRebuildResult.DuplicateOrStale, link.Deliver(oldKeyframe));
        Assert.Equal(publications, link.Client.PublicationCountForTest);
        Assert.Equal(1, link.Live.Get<RebuildValue>(live).Number);
        Assert.Throws<ArgumentOutOfRangeException>(() => link.Client.ExpectEpoch(7));
        link.Client.ExpectEpoch(8);   // the same pending offer again

        link.Server.Start(RebuildLink.Slot, 8);
        link.ServerWorld.Set(e, new RebuildValue { Number = 3 });
        ReplicationDeltaPacket fresh = link.SendDeliver();
        Assert.Equal(new ReplicationPacketId(8, 1), fresh.Id);
        Assert.Equal(live, link.LiveEntity(1));
        Assert.Equal(3, link.Live.Get<RebuildValue>(live).Number);
        Assert.Throws<ArgumentOutOfRangeException>(() => link.Client.ExpectEpoch(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => link.Client.ExpectEpoch(0));
        Assert.Equal(DeltaRebuildResult.DuplicateOrStale, link.Deliver(oldKeyframe));
    }

    [Fact]
    public void WrapAcceptsZeroAndRejectsHalfRange()
    {
        var link = new RebuildLink();
        Entity e = link.Spawn(1, 1);
        link.Server.SeedSequence(RebuildLink.Slot, uint.MaxValue);
        ReplicationDeltaPacket last = link.SendDeliverAck();
        Assert.Equal(uint.MaxValue, last.Id.Sequence);

        link.ServerWorld.Set(e, new RebuildValue { Number = 2 });
        ReplicationDeltaPacket zero = link.SendDeliver();
        Assert.Equal(new ReplicationPacketId(RebuildLink.Epoch, 0), zero.Id);
        Assert.Equal(last.Id, zero.Baseline);
        Assert.Equal(zero.Id, link.Client.AckTarget);
        Assert.Equal(DeltaRebuildResult.DuplicateOrStale, link.Deliver(last));

        link.ServerWorld.Set(e, new RebuildValue { Number = 3 });
        ReplicationDeltaPacket one = link.Send();
        int publications = link.Client.PublicationCountForTest;
        Assert.Equal(DeltaRebuildResult.Invalid, link.Deliver(RebuildLink.Rewrite(last, snapshot: 0x80000000u)));
        Assert.Equal(DeltaRebuildFailure.SequenceAmbiguous, link.Client.LastFailure);
        Assert.Equal(DeltaRebuildResult.Invalid, link.Deliver(RebuildLink.Rewrite(one, baseline: 0x80000001u)));
        Assert.Equal(DeltaRebuildFailure.SequenceAmbiguous, link.Client.LastFailure);
        Assert.Equal(publications, link.Client.PublicationCountForTest);
        Assert.Equal(zero.Id, link.Client.LatestAcceptedId);

        Assert.Equal(DeltaRebuildResult.Accepted, link.Deliver(one));
        Assert.Equal(3, link.Live.Get<RebuildValue>(link.LiveEntity(1)).Number);
    }

    [Fact]
    public void UnknownEpochDatagramCannotEstablishStream()
    {
        var link = new RebuildLink(expect: false);
        link.Spawn(1, 1);
        ReplicationDeltaPacket keyframe = link.Send();

        Assert.Equal(DeltaRebuildResult.DuplicateOrStale, link.Deliver(keyframe));
        Assert.Empty(link.View.Entities);
        Assert.Null(link.Client.LatestAcceptedId);
        Assert.Equal(0, link.Client.PublicationCountForTest);
        Assert.Equal(0, link.Client.RetainedCountForTest);

        link.Client.ExpectEpoch(RebuildLink.Epoch);
        Assert.Equal(DeltaRebuildResult.DuplicateOrStale, link.Deliver(RebuildLink.Rewrite(keyframe, epoch: 9)));
        Assert.Empty(link.View.Entities);

        Assert.Equal(DeltaRebuildResult.Accepted, link.Deliver(keyframe));
        Assert.Equal(new long[] { 1 }, link.View.Entities.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void FirstKeyframeOverLegacyWorldRemovesLegacyOnlyState(string kind)
    {
        var link = new RebuildLink(kind);
        Entity one = link.Spawn(1, 1);
        link.ServerWorld.Set(one, new RebuildTag());
        link.ServerWorld.Set(one, new RebuildExt { V = 3 });
        Entity two = link.Spawn(2, 2);
        link.Server.Capture();
        link.View.ApplyDelta(link.Live, link.Server.WriteLegacy(9, link.Interest));
        Entity liveOne = link.LiveEntity(1);
        link.Live.Set(liveOne, new RebuildLocal { Note = 8 });
        Assert.True(link.View.TryGetEntity(2, out _));

        link.ServerWorld.Remove<RebuildTag>(one);
        if (kind == nameof(RebuildWriterAdapter.Aoi)) link.Interest.Remove(2);
        else link.ServerWorld.Despawn(two);
        ReplicationDeltaPacket keyframe = link.SendDeliver();

        Assert.True(keyframe.IsKeyframe);
        Assert.Equal(new long[] { 1 }, link.View.Entities.Keys.Order());
        Assert.Equal(liveOne, link.LiveEntity(1));
        Assert.False(link.Live.TryGet<RebuildTag>(liveOne, out _));
        Assert.Equal(3, link.Live.Get<RebuildExt>(liveOne).V);
        Assert.Equal(8, link.Live.Get<RebuildLocal>(liveOne).Note);
    }

    internal static Entity SpawnCounted(RebuildLink link, long netId, int builtin, int? extension)
    {
        Entity e = link.ServerWorld.Spawn();
        link.ServerWorld.Set(e, new NetId(netId));
        link.ServerWorld.Set(e, new CountedBuiltin { N = builtin });
        if (extension is int x) link.ServerWorld.Set(e, new CountedExtension { N = x });
        link.Interest.Add(netId);
        return e;
    }

    // The net id and full-entry flag of the first changed entity, read by hand after the removed section.
    private static (long NetId, byte IsNew) FirstChangedEntry(ReadOnlySpan<byte> body)
    {
        RebuildWireHeader header = RebuildWire.ReadHeader(body);
        int at = RebuildWire.HeaderBytes + 4 + (header.RemovedCount * 8) + 4;
        return (BinaryPrimitives.ReadInt64LittleEndian(body[at..]), body[at + 8]);
    }
}
