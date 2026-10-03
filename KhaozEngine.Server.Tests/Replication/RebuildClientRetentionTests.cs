using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Client retention: the latest publication, the ack target and the newest confirmed server baseline are pinned inside
/// the same count and byte limits as every other projection, others are pruned by insertion ordinal, a pruned server
/// baseline is a recoverable miss, and a projection beyond the limits is a capacity failure.
/// </summary>
public class RebuildClientRetentionTests
{
    private static void AssertAckTargetIsLatest(RebuildLink link, ReplicationDeltaPacket accepted)
    {
        Assert.Equal(accepted.Id, link.Client.LatestAcceptedId);
        Assert.Equal(link.Client.LatestAcceptedId, link.Client.AckTarget);
    }

    private static void AssertRetainedBytesAreDistinctBackings(RebuildLink link, params ReplicationDeltaPacket[] retained)
    {
        Assert.Equal(retained.Length, link.Client.RetainedCountForTest);
        Assert.Equal(ProjectionDump.DistinctBackingBytes(retained.Select(p => link.ClientRetained(p.Id))),
            link.Client.RetainedBytesForTest);
    }

    private static HashSet<ReplicationPacketId> Ids(params ReplicationDeltaPacket[] packets) =>
        packets.Select(p => p.Id).ToHashSet();

    [Fact]
    public void PinsAreLatestPublicationAndNewestConfirmedBaseline()
    {
        var link = new RebuildLink(client: new DeltaRebuildOptions { MaxRetainedProjections = 4 });
        Entity e = link.Spawn(1, 1);
        ReplicationDeltaPacket NextValue(int value)
        {
            link.ServerWorld.Set(e, new RebuildValue { Number = value });
            ReplicationDeltaPacket packet = link.SendDeliver();
            AssertAckTargetIsLatest(link, packet);
            return packet;
        }

        ReplicationDeltaPacket k1 = NextValue(1);
        Assert.True(Ids(k1).SetEquals(link.Client.PinnedIdsForTest));
        link.Ack(k1);

        ReplicationDeltaPacket d2 = NextValue(2);
        Assert.Equal(k1.Id, d2.Baseline);
        Assert.True(Ids(d2, k1).SetEquals(link.Client.PinnedIdsForTest));
        ReplicationDeltaPacket d3 = NextValue(3);
        Assert.True(Ids(d3, k1).SetEquals(link.Client.PinnedIdsForTest));

        link.Ack(d3);
        ReplicationDeltaPacket d4 = NextValue(4);
        Assert.Equal(d3.Id, d4.Baseline);
        Assert.True(Ids(d4, d3).SetEquals(link.Client.PinnedIdsForTest));
        AssertRetainedBytesAreDistinctBackings(link, k1, d2, d3, d4);

        // A fifth projection prunes the oldest unpinned entry by insertion, k1, then d2.
        ReplicationDeltaPacket d5 = NextValue(5);
        Assert.False(link.Client.TryGetRetainedForTest(k1.Id, out _));
        AssertRetainedBytesAreDistinctBackings(link, d2, d3, d4, d5);
        ReplicationDeltaPacket d6 = NextValue(6);
        Assert.False(link.Client.TryGetRetainedForTest(d2.Id, out _));
        AssertRetainedBytesAreDistinctBackings(link, d3, d4, d5, d6);
        Assert.True(Ids(d6, d3).SetEquals(link.Client.PinnedIdsForTest));

        // A keyframe in the same epoch keeps the newest confirmed baseline pinned.
        link.ServerWorld.Set(e, new RebuildValue { Number = 7 });
        ReplicationDeltaPacket k7 = link.SendDeliver(keyframe: true);
        AssertAckTargetIsLatest(link, k7);
        Assert.True(Ids(k7, d3).SetEquals(link.Client.PinnedIdsForTest));
    }

    [Fact]
    public void NewProjectionBesidePinsAlwaysFitsAtTheFloor()
    {
        // The tightest valid options: four projections, a budget of exactly four complete keyframes, and the 12-byte
        // NetWorld envelope inside every keyframe.
        const int keyframeBytes = 200;
        var options = new DeltaRebuildOptions
        {
            MaxRetainedProjections = 4,
            MaxKeyframeBytes = keyframeBytes,
            MaxRetainedPayloadBytes = 4 * keyframeBytes,
            EnvelopeBytes = 12,
        };
        var link = new RebuildLink(server: options, client: options);
        Entity e = link.Spawn(1, 1);
        link.ServerWorld.Remove<RebuildValue>(e);

        // Body 26 + entity 15 + frame header 2 + 7-bit length 2 + 143 payload bytes = 188, plus 12 = 200.
        const int largest = 143;
        byte fill = 0;
        ReplicationDeltaPacket Next()
        {
            link.ServerWorld.Set(e, new RebuildFill { Size = largest, Fill = ++fill });
            ReplicationDeltaPacket packet = link.SendDeliver();
            AssertAckTargetIsLatest(link, packet);
            Assert.InRange(link.Client.RetainedCountForTest, 1, 4);
            Assert.InRange(link.Client.RetainedBytesForTest, 1, options.MaxRetainedPayloadBytes);
            Assert.True(link.ClientRetained(packet.Id).BackingBytes <= keyframeBytes - options.EnvelopeBytes);
            foreach (ReplicationPacketId pin in link.Client.PinnedIdsForTest)
                Assert.True(link.Client.TryGetRetainedForTest(pin, out _));
            return packet;
        }

        link.Ack(Next());
        for (int i = 0; i < 5; i++) Next();   // every ack lost: the keyframe stays the pinned baseline
        ReplicationDeltaPacket acked = Next();
        link.Ack(acked);
        for (int i = 0; i < 5; i++) Next();
        link.Ack(Next());
        Next();
        Assert.Equal(DeltaRebuildFailure.None, link.Client.LastFailure);

        // One more payload byte cannot be built at all, so a legitimate server never sends it.
        link.ServerWorld.Set(e, new RebuildFill { Size = largest + 1, Fill = 1 });
        link.Server.Capture();
        DeltaRebuildException refused = Assert.Throws<DeltaRebuildException>(
            () => link.Server.Build(RebuildLink.Slot, link.Interest));
        Assert.Equal(DeltaRebuildFailure.CapacityExceeded, refused.Failure);
    }

    [Fact]
    public void PrunedServerBaselineIsARecoverableMiss()
    {
        var link = new RebuildLink(client: new DeltaRebuildOptions { MaxRetainedProjections = 4 });
        Entity e = link.Spawn(1, 1);
        link.SendDeliverAck();
        var sent = new List<ReplicationDeltaPacket>();
        for (int value = 2; value <= 5; value++)
        {
            link.ServerWorld.Set(e, new RebuildValue { Number = value });
            sent.Add(link.SendDeliver());
        }
        link.Ack(sent[0]);
        link.ServerWorld.Set(e, new RebuildValue { Number = 6 });
        ReplicationDeltaPacket miss = link.Send();

        Assert.Equal(DeltaRebuildResult.MissingBaseline, link.Deliver(miss));
        Assert.Equal(sent[0].Id, link.LastMissing);
        Assert.Equal(5, link.Live.Get<RebuildValue>(link.LiveEntity(1)).Number);

        // Keyframe repair: a new epoch, its keyframe, and the stream continues.
        link.Client.ExpectEpoch(RebuildLink.Epoch + 1);
        link.Server.Start(RebuildLink.Slot, RebuildLink.Epoch + 1);
        ReplicationDeltaPacket keyframe = link.SendDeliverAck();
        Assert.True(keyframe.IsKeyframe);
        Assert.Equal(6, link.Live.Get<RebuildValue>(link.LiveEntity(1)).Number);
        link.ServerWorld.Set(e, new RebuildValue { Number = 7 });
        ReplicationDeltaPacket next = link.SendDeliver();
        Assert.Equal(keyframe.Id, next.Baseline);
        Assert.Equal(7, link.Live.Get<RebuildValue>(link.LiveEntity(1)).Number);
    }

    public static TheoryData<string, bool> CapacityCases
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (string limit in new[] { nameof(DeltaRebuildOptions.MaxEntities),
                         nameof(DeltaRebuildOptions.MaxComponents), nameof(DeltaRebuildOptions.MaxKeyframeBytes),
                         nameof(DeltaRebuildOptions.EnvelopeBytes) })
            {
                data.Add(limit, true);
                data.Add(limit, false);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CapacityCases))]
    public void ProjectionBeyondLimitsIsCapacityNotMalformed(string limit, bool keyframe)
    {
        // The accepted projection has one entity, two frames and a 49-byte keyframe body. The next reconstruction has
        // two entities, three frames and a 70-byte keyframe body, one past each client limit.
        DeltaRebuildOptions client = limit switch
        {
            nameof(DeltaRebuildOptions.MaxEntities) => new DeltaRebuildOptions { MaxEntities = 1 },
            nameof(DeltaRebuildOptions.MaxComponents) => new DeltaRebuildOptions { MaxComponents = 2 },
            nameof(DeltaRebuildOptions.MaxKeyframeBytes) =>
                new DeltaRebuildOptions { MaxKeyframeBytes = 69, MaxRetainedPayloadBytes = 4 * 69 },
            _ => new DeltaRebuildOptions { MaxKeyframeBytes = 80, MaxRetainedPayloadBytes = 320, EnvelopeBytes = 11 },
        };
        var link = new RebuildLink(client: client);
        Entity one = link.Spawn(1, 1);
        link.ServerWorld.Set(one, new RebuildTag());
        ReplicationDeltaPacket fits = link.SendDeliverAck();
        Assert.Equal(49, fits.Bytes.Length);

        link.Spawn(2, 2);
        ReplicationDeltaPacket tooBig = link.Send(keyframe);
        Assert.Equal(keyframe, tooBig.IsKeyframe);
        if (keyframe) Assert.Equal(70, tooBig.Bytes.Length);
        int publications = link.Client.PublicationCountForTest;
        int retained = link.Client.RetainedCountForTest;

        Assert.Equal(DeltaRebuildResult.Invalid, link.Deliver(tooBig));
        Assert.Equal(DeltaRebuildFailure.CapacityExceeded, link.Client.LastFailure);
        Assert.False(string.IsNullOrEmpty(link.LastError));
        Assert.Equal(publications, link.Client.PublicationCountForTest);
        Assert.Equal(fits.Id, link.Client.LatestAcceptedId);
        Assert.Equal(retained, link.Client.RetainedCountForTest);
        Assert.Equal(new long[] { 1 }, link.View.Entities.Keys.Order());
    }

    [Fact]
    public void PublicationSamplesNeverAliasClientRetention()
    {
        var link = new RebuildLink();
        Entity e = link.Spawn(1, 1);
        link.ServerWorld.Set(e, new RebuildPos { X = 0f });
        var accepted = new List<ReplicationDeltaPacket> { link.SendDeliverAck() };
        link.View.RecordInterpolationSample(0.0);
        for (int i = 1; i <= 3; i++)
        {
            link.ServerWorld.Set(e, new RebuildPos { X = i });
            accepted.Add(link.SendDeliver());
            link.View.RecordInterpolationSample(i * 0.1);
        }

        var retainedBackings = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (ReplicationDeltaPacket packet in accepted)
            foreach (byte[] backing in link.ClientRetained(packet.Id).BackingArraysForTest()) retainedBackings.Add(backing);
        Assert.NotEmpty(retainedBackings);
        foreach (byte[] presentation in link.View.PresentationArraysForTest())
            Assert.DoesNotContain(presentation, retainedBackings);
    }

    [Fact]
    public void ResetClearsStreamStateButNeverRevivesAnEpoch()
    {
        var link = new RebuildLink();
        link.Spawn(1, 1);
        ReplicationDeltaPacket keyframe = link.SendDeliver();
        Entity live = link.LiveEntity(1);

        link.Client.Reset();
        Assert.Null(link.Client.LatestAcceptedId);
        Assert.Null(link.Client.AckTarget);
        Assert.Equal(0, link.Client.RetainedCountForTest);
        Assert.Empty(link.Client.PinnedIdsForTest);
        Assert.Equal(DeltaRebuildFailure.None, link.Client.LastFailure);
        Assert.Equal(live, link.LiveEntity(1));

        Assert.Equal(DeltaRebuildResult.DuplicateOrStale, link.Deliver(keyframe));
        Assert.Throws<ArgumentOutOfRangeException>(() => link.Client.ExpectEpoch(RebuildLink.Epoch));
        link.Client.ExpectEpoch(RebuildLink.Epoch + 1);
        Assert.Equal(DeltaRebuildResult.DuplicateOrStale, link.Deliver(keyframe));
    }

    [Fact]
    public void ConstructionValidatesOptionsRegistryAndEpoch()
    {
        ReplicationRegistry registry = RebuildLink.NewRegistry(includeOpaque: false);
        var view = new ClientReplicationView(registry);
        Assert.Throws<ArgumentNullException>(() => new ClientDeltaRebuild(null!, view, new DeltaRebuildOptions()));
        Assert.Throws<ArgumentNullException>(() => new ClientDeltaRebuild(registry, null!, new DeltaRebuildOptions()));
        Assert.Throws<ArgumentNullException>(() => new ClientDeltaRebuild(registry, view, null!));
        ArgumentOutOfRangeException invalid = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClientDeltaRebuild(registry, view, new DeltaRebuildOptions { MaxRetainedProjections = 3 }));
        Assert.Equal(nameof(DeltaRebuildOptions.MaxRetainedProjections), invalid.ParamName);
        Assert.Throws<ArgumentException>(() => new ClientDeltaRebuild(
            RebuildLink.NewRegistry(includeOpaque: false), view, new DeltaRebuildOptions()));

        var client = new ClientDeltaRebuild(registry, view, new DeltaRebuildOptions());
        Assert.Throws<ArgumentOutOfRangeException>(() => client.ExpectEpoch(0));
        Assert.Throws<ArgumentNullException>(() => client.TryApply(null!, new byte[18], out _, out _, out _));
        client.ExpectEpoch(3);
        client.ExpectEpoch(3);
        Assert.Throws<ArgumentOutOfRangeException>(() => client.ExpectEpoch(2));
    }
}
