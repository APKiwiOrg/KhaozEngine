using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Projection publication reconciles the live view with a complete projection through staged typed copies: absent
/// net ids despawn, surviving ones keep their entity, every authoritative component is reinstalled, registered
/// components absent from the new set are removed with their sample history, and game-local components stay. The
/// presentation buffers hold copies, never retained backing.
/// </summary>
public class ProjectionPublicationTests
{
    private struct Pos : IComponent { public float X; }       // interpolated

    private struct Value : IComponent { public int Number; }

    private struct Tag : IComponent { }

    private struct Ext : IComponent { public int V; }

    private struct Local : IComponent { public int Note; }    // game-local, never registered

    private struct Saved : IComponent { public int N; }       // registered, persisted, never replicated

    private const ushort PosId = 1, ValueId = 2, TagId = 3;
    private const ushort ExtId = ReplicationRegistry.FirstExtensionTypeId;
    private const ushort OpaqueId = ReplicationRegistry.FirstExtensionTypeId + 9;
    private const ushort SavedId = ReplicationRegistry.FirstExtensionTypeId + 1;

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(PosId, (p, bw) => bw.Write(p.X), br => new Pos { X = br.ReadSingle() },
            lerp: (a, b, t) => new Pos { X = a.X + (b.X - a.X) * t });
        r.Register<Value>(ValueId, (v, bw) => bw.Write(v.Number), br => new Value { Number = br.ReadInt32() });
        r.Register<Tag>(TagId, (_, _) => { }, _ => default);
        r.Register<Ext>(ExtId, (x, bw) => bw.Write(x.V), br => new Ext { V = br.ReadInt32() });
        r.Register<Saved>(SavedId, (x, bw) => bw.Write(x.N), br => new Saved { N = br.ReadInt32() },
            channels: ReplicationChannels.Persist);
        return r;
    }

    private static (ushort, byte[]) P(float x)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, x);
        return (PosId, b);
    }

    private static (ushort, byte[]) V(int n) => (ValueId, I(n));

    private static (ushort, byte[]) X(int n) => (ExtId, I(n));

    private static readonly (ushort, byte[]) T = (TagId, Array.Empty<byte>());

    private static byte[] I(int n)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, n);
        return b;
    }

    // Every frame gets its own fresh backing, as a compacted or reconstructed projection would.
    private static ReplicationProjection Proj(params (long NetId, (ushort TypeId, byte[] Bytes)[] Frames)[] entities)
    {
        var list = new List<KeyValuePair<long, ProjectedEntity>>();
        foreach ((long netId, (ushort typeId, byte[] bytes)[] frames) in entities)
        {
            var components = new ProjectedComponent[frames.Length];
            for (int i = 0; i < frames.Length; i++)
                components[i] = new ProjectedComponent(frames[i].typeId, (byte[])frames[i].bytes.Clone(), 0,
                    frames[i].bytes.Length);
            list.Add(new(netId, new ProjectedEntity(components)));
        }
        return ReplicationProjection.Create(list, new DeltaRebuildOptions());
    }

    private sealed class Rig
    {
        public readonly ReplicationRegistry Registry = NewRegistry();
        public readonly World World = new();
        public readonly ClientReplicationView View;
        public readonly ProjectionStaging Staging;

        public Rig()
        {
            View = new ClientReplicationView(Registry);
            Staging = new ProjectionStaging(Registry);
        }

        public void Publish(ReplicationProjection projection)
        {
            Staging.StageAll(projection);
            View.PublishProjection(World, projection, Staging);
        }

        public Entity Live(long netId)
        {
            Assert.True(View.TryGetEntity(netId, out Entity e));
            Assert.True(World.IsAlive(e));
            return e;
        }
    }

    [Fact]
    public void PublicationReinstallsUnchangedAuthoritativeComponents()
    {
        var rig = new Rig();
        rig.Publish(Proj((7, new[] { P(1f), V(5), T, X(40) })));
        Entity e = rig.Live(7);
        Assert.Equal(5, rig.World.Get<Value>(e).Number);

        // Presentation and game code changed the live ECS since the last publication.
        rig.World.Set(e, new Value { Number = 99 });
        rig.World.Set(e, new Pos { X = 42f });
        rig.World.Set(e, new Ext { V = -1 });
        rig.World.Remove<Tag>(e);

        rig.Publish(Proj((7, new[] { P(1f), V(5), T, X(40) })));   // byte-identical to the first
        Assert.Equal(e, rig.Live(7));
        Assert.Equal(5, rig.World.Get<Value>(e).Number);
        Assert.Equal(1f, rig.World.Get<Pos>(e).X);
        Assert.Equal(40, rig.World.Get<Ext>(e).V);
        Assert.True(rig.World.Has<Tag>(e));
    }

    [Fact]
    public void PublicationRemovesOnlyRegisteredAbsentComponents()
    {
        var rig = new Rig();
        rig.Publish(Proj((7, new[] { P(1f), V(5), T, X(40) })));
        Entity e = rig.Live(7);
        rig.World.Set(e, new Local { Note = 3 });

        // A replacement set with one registered component and one opaque frame nobody registered.
        rig.Publish(Proj((7, new[] { V(6), (OpaqueId, new byte[] { 1, 2, 3 }) })));
        Assert.Equal(e, rig.Live(7));
        Assert.Equal(6, rig.World.Get<Value>(e).Number);
        Assert.False(rig.World.Has<Pos>(e));
        Assert.False(rig.World.Has<Tag>(e));
        Assert.False(rig.World.Has<Ext>(e));
        Assert.Equal(3, rig.World.Get<Local>(e).Note);
        Assert.True(rig.World.Has<NetId>(e));
        Assert.Equal(7, rig.World.Get<NetId>(e).Value);
    }

    [Fact]
    public void SurvivingNetIdPreservesEntityHandle()
    {
        var rig = new Rig();
        rig.Publish(Proj((7, new[] { V(1) }), (8, new[] { V(2) })));
        Entity seven = rig.Live(7), eight = rig.Live(8);

        rig.Publish(Proj((7, new[] { V(3), P(2f) })));
        Assert.Equal(seven, rig.Live(7));
        Assert.False(rig.World.IsAlive(eight));
        Assert.Equal(new long[] { 7 }, rig.View.Entities.Keys.Order());
        Assert.Equal(3, rig.World.Get<Value>(seven).Number);

        rig.Publish(Proj((7, new[] { V(3) }), (8, new[] { V(4) })));
        Assert.Equal(seven, rig.Live(7));
        Assert.Equal(4, rig.World.Get<Value>(rig.Live(8)).Number);
        Assert.Equal(new long[] { 7, 8 }, rig.View.Entities.Keys.Order());
    }

    [Fact]
    public void RemovedSampleHistoryCannotResurrectComponent()
    {
        var rig = new Rig();
        rig.Publish(Proj((7, new[] { P(0f), V(1) })));
        rig.View.RecordInterpolationSample(0.0);
        rig.Publish(Proj((7, new[] { P(10f), V(1) })));
        rig.View.RecordInterpolationSample(0.1);
        Entity e = rig.Live(7);
        rig.View.InterpolateAt(rig.World, 0.05);
        Assert.Equal(5f, rig.World.Get<Pos>(e).X, 3);

        rig.Publish(Proj((7, new[] { V(1) })));
        rig.View.RecordInterpolationSample(0.2);
        Assert.False(rig.World.Has<Pos>(e));
        rig.View.InterpolateAt(rig.World, 0.15);
        Assert.False(rig.World.Has<Pos>(e));
        rig.View.Interpolate(rig.World, 0.5f);
        Assert.False(rig.World.Has<Pos>(e));
        Assert.Empty(rig.View.PresentationArraysForTest());
    }

    [Fact]
    public void FirstKeyframeOverLegacyWorldRemovesLegacyOnlyState()
    {
        var rig = new Rig();
        var server = new World();
        Entity s1 = server.Spawn();
        server.Set(s1, new NetId(1));
        server.Set(s1, new Pos { X = 3f });
        server.Set(s1, new Value { Number = 10 });
        server.Set(s1, new Tag());
        Entity s2 = server.Spawn();
        server.Set(s2, new NetId(2));
        server.Set(s2, new Value { Number = 20 });
        server.Set(s2, new Pos { X = 9f });
        var legacy = new ServerReplicator(rig.Registry);
        legacy.Capture(server);
        rig.View.ApplyDelta(rig.World, legacy.WriteFor(0));
        rig.View.RecordInterpolationSample(0.0);

        Entity survivor = rig.Live(1);
        Entity legacyOnly = rig.Live(2);
        Assert.True(rig.World.Has<Tag>(survivor));
        rig.World.Set(survivor, new Local { Note = 77 });
        rig.World.Set(survivor, new Value { Number = -5 });   // presentation drift before the keyframe

        // The keyframe carries entity 1 without Tag, and no entity 2.
        rig.Publish(Proj((1, new[] { P(4f), V(11) })));

        Assert.False(rig.World.IsAlive(legacyOnly));
        Assert.False(rig.View.TryGetEntity(2, out _));
        Assert.Equal(new long[] { 1 }, rig.View.Entities.Keys.Order());
        Assert.Equal(survivor, rig.Live(1));
        Assert.False(rig.World.Has<Tag>(survivor));
        Assert.Equal(77, rig.World.Get<Local>(survivor).Note);
        Assert.Equal(11, rig.World.Get<Value>(survivor).Number);
        Assert.Equal(4f, rig.World.Get<Pos>(survivor).X);

        // Entity 2's sample history went with it, so interpolation cannot respawn or write it.
        rig.View.RecordInterpolationSample(0.1);
        rig.View.InterpolateAt(rig.World, 0.05);
        Assert.False(rig.View.TryGetEntity(2, out _));
        Assert.Equal(3.5f, rig.World.Get<Pos>(survivor).X, 3);
    }

    [Fact]
    public void PersistOnlyRegisteredComponentSurvivesPublication()
    {
        var rig = new Rig();
        rig.Publish(Proj((7, new[] { V(1), T })));
        Entity e = rig.Live(7);
        rig.World.Set(e, new Saved { N = 5 });

        rig.Publish(Proj((7, new[] { V(2) })));
        Assert.Equal(e, rig.Live(7));
        Assert.Equal(2, rig.World.Get<Value>(e).Number);
        Assert.False(rig.World.Has<Tag>(e));
        Assert.Equal(5, rig.World.Get<Saved>(e).N);
    }

    [Fact]
    public void StagingRefusesRegisteredFrameThatNeverReplicates()
    {
        var rig = new Rig();
        var frame = new ProjectedComponent(SavedId, I(5), 0, 4);
        Assert.Equal(DeltaRebuildFailure.MalformedPacket,
            Assert.Throws<DeltaRebuildException>(() => rig.Staging.Decode(7, frame)).Failure);
        Assert.Equal(DeltaRebuildFailure.MalformedPacket, Assert.Throws<DeltaRebuildException>(
            () => rig.Staging.StageAll(Proj((7, new[] { V(1), (SavedId, I(5)) })))).Failure);
        Assert.Null(rig.Staging.StagedFor);
    }

    [Fact]
    public void PublicationRequiresStagingFinishedForThatProjection()
    {
        var rig = new Rig();
        rig.Publish(Proj((7, new[] { P(1f), V(1) }), (8, new[] { V(2) })));
        rig.View.RecordInterpolationSample(0.0);
        Entity seven = rig.Live(7), eight = rig.Live(8);
        var presentationBefore = rig.View.PresentationArraysForTest().ToList();

        void AssertUnchanged()
        {
            Assert.Equal(new long[] { 7, 8 }, rig.View.Entities.Keys.Order());
            Assert.Equal(seven, rig.Live(7));
            Assert.Equal(eight, rig.Live(8));
            Assert.Equal(1f, rig.World.Get<Pos>(seven).X);
            Assert.Equal(1, rig.World.Get<Value>(seven).Number);
            Assert.Equal(2, rig.World.Get<Value>(eight).Number);
            var after = rig.View.PresentationArraysForTest().ToList();
            Assert.Equal(presentationBefore.Count, after.Count);
            for (int i = 0; i < after.Count; i++) Assert.Same(presentationBefore[i], after[i]);
        }

        // Staging finished for another projection, even one with the same net ids, is refused before any change.
        ReplicationProjection stagedFor = Proj((7, new[] { V(3) }));
        ReplicationProjection published = Proj((7, new[] { P(5f), V(3) }));
        rig.Staging.StageAll(stagedFor);
        Assert.Same(stagedFor, rig.Staging.StagedFor);
        Assert.Throws<InvalidOperationException>(() => rig.View.PublishProjection(rig.World, published, rig.Staging));
        AssertUnchanged();

        // Decoding after the finishing step invalidates it.
        rig.Staging.StageAll(published);
        rig.Staging.Decode(7, new ProjectedComponent(ValueId, I(4), 0, 4));
        Assert.Null(rig.Staging.StagedFor);
        Assert.Throws<InvalidOperationException>(() => rig.View.PublishProjection(rig.World, published, rig.Staging));
        AssertUnchanged();

        // The explicit finishing step, as reconstruction will use it, authorizes exactly that projection.
        rig.Staging.Reset();
        rig.Staging.Decode(7, new ProjectedComponent(PosId, (byte[])P(5f).Item2.Clone(), 0, 4));
        rig.Staging.Decode(7, new ProjectedComponent(ValueId, I(3), 0, 4));
        rig.Staging.FinishFor(published);
        rig.View.PublishProjection(rig.World, published, rig.Staging);
        Assert.Equal(new long[] { 7 }, rig.View.Entities.Keys.Order());
        Assert.Equal(5f, rig.World.Get<Pos>(seven).X);
        Assert.Equal(3, rig.World.Get<Value>(seven).Number);
    }

    [Fact]
    public void PublicationSamplesNeverAliasRetainedBacking()
    {
        var rig = new Rig();
        var options = new DeltaRebuildOptions { MaxRetainedProjections = 4 };
        var retention = new ProjectionRetention(options);
        var latest = new HashSet<ReplicationPacketId>();

        ReplicationProjection Retain(uint sequence, ReplicationProjection projection)
        {
            var id = new ReplicationPacketId(1, sequence);
            latest.Clear();
            latest.Add(id);
            Assert.True(retention.TryRetain(id, projection, latest, out _));
            return projection;
        }

        ReplicationProjection first = Retain(1, Proj((7, new[] { P(1f), V(1) }), (8, new[] { P(2f) })));
        rig.Publish(first);
        rig.View.RecordInterpolationSample(0.0);
        ReplicationProjection second = Retain(2, Proj((7, new[] { P(3f), V(1) }), (8, new[] { P(2f) })));
        rig.Publish(second);
        rig.View.RecordInterpolationSample(0.1);

        // Fill retention until the first projection is evicted.
        Retain(3, Proj((9, new[] { V(0) })));
        Retain(4, Proj((9, new[] { V(0) })));
        Retain(5, Proj((9, new[] { V(0) })));
        Assert.False(retention.TryGet(new ReplicationPacketId(1, 1), out _));
        Assert.True(retention.TryGet(new ReplicationPacketId(1, 2), out _));

        var presentation = new HashSet<byte[]>(rig.View.PresentationArraysForTest(), ReferenceEqualityComparer.Instance);
        Assert.NotEmpty(presentation);
        foreach (byte[] backing in retention.BackingArraysForTest())
            Assert.DoesNotContain(backing, presentation);
        foreach (byte[] backing in first.BackingArraysForTest())
            Assert.DoesNotContain(backing, presentation);
        foreach (byte[] backing in second.BackingArraysForTest())
            Assert.DoesNotContain(backing, presentation);

        var retained = new List<ReplicationProjection>();
        for (uint s = 2; s <= 5; s++)
            if (retention.TryGet(new ReplicationPacketId(1, s), out ReplicationProjection p)) retained.Add(p);
        Assert.Equal(4, retained.Count);
        Assert.Equal(ProjectionDump.DistinctLength(retention.BackingArraysForTest()), retention.PayloadBytes);
        Assert.Equal(ProjectionDump.DistinctBackingBytes(retained), retention.PayloadBytes);

        // Presentation still renders from its own copies after eviction.
        rig.View.InterpolateAt(rig.World, 0.05);
        Assert.Equal(2f, rig.World.Get<Pos>(rig.Live(7)).X, 3);
        Assert.Equal(2f, rig.World.Get<Pos>(rig.Live(8)).X, 3);
    }
}
