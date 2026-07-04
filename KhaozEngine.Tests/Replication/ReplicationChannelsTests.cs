using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Per-registration <see cref="ReplicationChannels"/>: a component declares which of the four consumers see its
/// bytes (client AoI replication, cell persistence, cell handoff) and, as a Replicate modifier, owner-only
/// visibility. This covers the snapshot + delta (client-serving) paths and the registry guards; the persistence /
/// handoff / ghost paths are covered in <c>ReplicationChannelsShardingTests</c>.
/// </summary>
public class ReplicationChannelsTests
{
    private struct Pub : IComponent { public int V; }         // id 1, built-in: Default (all channels)
    private struct OwnerPriv : IComponent { public int V; }   // id 16, ext: Default | OwnerOnly
    private struct ServerOnly : IComponent { public int V; }  // id 17, ext: Persist | Migrate (never replicated)
    private struct RepOnly : IComponent { public int V; }     // id 18, ext: Replicate only
    private struct PersistOnly : IComponent { public int V; } // id 19, ext: Persist only
    private struct MigrateOnly : IComponent { public int V; } // id 20, ext: Migrate only

    private const ushort Ext = ReplicationRegistry.FirstExtensionTypeId;

    // A registry knowing every test component, each with a distinct channel set.
    private static ReplicationRegistry Full()
    {
        var r = new ReplicationRegistry();
        r.Register<Pub>(1, (c, bw) => bw.Write(c.V), br => new Pub { V = br.ReadInt32() });
        r.Register<OwnerPriv>(Ext, (c, bw) => bw.Write(c.V), br => new OwnerPriv { V = br.ReadInt32() },
            channels: ReplicationChannels.Default | ReplicationChannels.OwnerOnly);
        r.Register<ServerOnly>((ushort)(Ext + 1), (c, bw) => bw.Write(c.V), br => new ServerOnly { V = br.ReadInt32() },
            channels: ReplicationChannels.Persist | ReplicationChannels.Migrate);
        r.Register<RepOnly>((ushort)(Ext + 2), (c, bw) => bw.Write(c.V), br => new RepOnly { V = br.ReadInt32() },
            channels: ReplicationChannels.Replicate);
        r.Register<PersistOnly>((ushort)(Ext + 3), (c, bw) => bw.Write(c.V), br => new PersistOnly { V = br.ReadInt32() },
            channels: ReplicationChannels.Persist);
        r.Register<MigrateOnly>((ushort)(Ext + 4), (c, bw) => bw.Write(c.V), br => new MigrateOnly { V = br.ReadInt32() },
            channels: ReplicationChannels.Migrate);
        return r;
    }

    private static Entity Spawn(World w, int netId, Action<World, Entity> set)
    {
        Entity e = w.Spawn();
        w.Set(e, new NetId(netId));
        set(w, e);
        return e;
    }

    private static (World client, ClientReplicationView view) Apply(ReplicationRegistry r, byte[] snapshot)
    {
        var client = new World();
        var view = new ClientReplicationView(r);
        view.Apply(client, snapshot);
        return (client, view);
    }

    // --- registry guards (constraint #3 + OwnerOnly-needs-Replicate) ---

    [Fact]
    public void RegisterBuiltIn_WithNonDefaultChannels_Throws()
    {
        var r = new ReplicationRegistry();
        // id 1 is below the extension floor: its unframed encoding is the core protocol and must keep Default.
        Assert.Throws<ArgumentException>(() => r.Register<Pub>(1,
            (c, bw) => bw.Write(c.V), br => new Pub { V = br.ReadInt32() },
            channels: ReplicationChannels.Replicate));
    }

    [Fact]
    public void RegisterBuiltIn_WithExplicitDefault_Succeeds()
    {
        var r = new ReplicationRegistry();
        // Passing the default value explicitly is not "altering" the wire, so it is allowed.
        r.Register<Pub>(1, (c, bw) => bw.Write(c.V), br => new Pub { V = br.ReadInt32() },
            channels: ReplicationChannels.Default);
    }

    [Fact]
    public void RegisterOwnerOnly_WithoutReplicate_Throws()
    {
        var r = new ReplicationRegistry();
        Assert.Throws<ArgumentException>(() => r.Register<OwnerPriv>(Ext,
            (c, bw) => bw.Write(c.V), br => new OwnerPriv { V = br.ReadInt32() },
            channels: ReplicationChannels.OwnerOnly | ReplicationChannels.Persist));
    }

    // --- Replicate channel filtering (snapshot) ---

    [Fact]
    public void Snapshot_Replicate_IncludesReplicated_ExcludesServerOnly()
    {
        ReplicationRegistry r = Full();
        var w = new World();
        Spawn(w, 100, (world, e) =>
        {
            world.Set(e, new Pub { V = 1 });          // Default -> replicated
            world.Set(e, new RepOnly { V = 2 });      // Replicate -> replicated
            world.Set(e, new ServerOnly { V = 3 });   // Persist|Migrate -> NOT replicated
        });

        byte[] snap = SnapshotWriter.WriteFiltered(w, r, new HashSet<int> { 100 });   // default channel = Replicate
        (World client, ClientReplicationView view) = Apply(r, snap);

        Assert.True(view.TryGetEntity(100, out Entity c));
        Assert.True(client.Has<Pub>(c));
        Assert.True(client.Has<RepOnly>(c));
        Assert.False(client.Has<ServerOnly>(c));      // server-only state never on the replication wire
    }

    [Fact]
    public void Snapshot_OwnerOnly_ReachesOnlyTheOwner()
    {
        ReplicationRegistry r = Full();
        var w = new World();
        Spawn(w, 100, (world, e) => { world.Set(e, new Pub { V = 1 }); world.Set(e, new OwnerPriv { V = 10 }); });
        Spawn(w, 200, (world, e) => { world.Set(e, new Pub { V = 2 }); world.Set(e, new OwnerPriv { V = 20 }); });
        var aoi = new HashSet<int> { 100, 200 };

        // Served to client whose player is netId 100.
        (World cA, ClientReplicationView vA) = Apply(r, SnapshotWriter.WriteFiltered(w, r, aoi, ReplicationChannels.Replicate, 100));
        Assert.True(vA.TryGetEntity(100, out Entity a100));
        Assert.True(vA.TryGetEntity(200, out Entity a200));
        Assert.True(cA.Has<OwnerPriv>(a100));      // own private state present
        Assert.False(cA.Has<OwnerPriv>(a200));     // the OTHER player's private state is NOT leaked
        Assert.True(cA.Has<Pub>(a200));            // ...but its public state is

        // Served to client whose player is netId 200: mirror image.
        (World cB, ClientReplicationView vB) = Apply(r, SnapshotWriter.WriteFiltered(w, r, aoi, ReplicationChannels.Replicate, 200));
        Assert.True(vB.TryGetEntity(100, out Entity b100));
        Assert.True(vB.TryGetEntity(200, out Entity b200));
        Assert.True(cB.Has<OwnerPriv>(b200));
        Assert.False(cB.Has<OwnerPriv>(b100));

        // Served with no owner (e.g. an unowned full snapshot): OwnerOnly reaches nobody.
        (World cN, ClientReplicationView vN) = Apply(r, SnapshotWriter.WriteFiltered(w, r, aoi, ReplicationChannels.Replicate, null));
        Assert.True(vN.TryGetEntity(100, out Entity n100));
        Assert.False(cN.Has<OwnerPriv>(n100));
    }

    // --- Replicate channel filtering (delta) ---

    [Fact]
    public void Delta_OwnerOnly_ReachesOnlyTheOwner()
    {
        ReplicationRegistry r = Full();
        var w = new World();
        Spawn(w, 100, (world, e) => { world.Set(e, new Pub { V = 1 }); world.Set(e, new OwnerPriv { V = 10 }); });
        Spawn(w, 200, (world, e) => { world.Set(e, new Pub { V = 2 }); world.Set(e, new OwnerPriv { V = 20 }); });
        var aoi = new HashSet<int> { 100, 200 };

        var repl = new AoiDeltaReplicator(r);
        repl.BeginTick();
        byte[] toA = repl.WriteFor(slot: 0, w, aoi, ownerNetId: 100);
        byte[] toB = repl.WriteFor(slot: 1, w, aoi, ownerNetId: 200);

        var cA = new World(); var vA = new ClientReplicationView(r); vA.ApplyDelta(cA, toA);
        var cB = new World(); var vB = new ClientReplicationView(r); vB.ApplyDelta(cB, toB);

        Assert.True(cA.Has<OwnerPriv>(vA.Entities[100]));
        Assert.False(cA.Has<OwnerPriv>(vA.Entities[200]));   // observer A never receives B's OwnerOnly component
        Assert.True(cB.Has<OwnerPriv>(vB.Entities[200]));
        Assert.False(cB.Has<OwnerPriv>(vB.Entities[100]));
    }

    [Fact]
    public void Delta_OwnerOnly_StaysHidden_AcrossAWorldChange()
    {
        // The encoder-level shape of a cell handoff: the entity is served from a NEW World with the same NetId.
        // An observer must keep NOT seeing another player's OwnerOnly component across that boundary.
        ReplicationRegistry r = Full();
        var worldA = new World();
        Spawn(worldA, 200, (world, e) => { world.Set(e, new Pub { V = 2 }); world.Set(e, new OwnerPriv { V = 20 }); });
        var aoi = new HashSet<int> { 200 };

        var repl = new AoiDeltaReplicator(r);
        var observer = new World();
        var view = new ClientReplicationView(r);

        int seq1 = repl.BeginTick();
        view.ApplyDelta(observer, repl.WriteFor(slot: 0, worldA, aoi, ownerNetId: 999));  // observer, not the owner
        repl.Acknowledge(0, seq1);
        Assert.True(view.TryGetEntity(200, out Entity before));
        Assert.False(observer.Has<OwnerPriv>(before));

        // Entity 200 is now served from a different World (post-handoff), same NetId.
        var worldB = new World();
        Spawn(worldB, 200, (world, e) => { world.Set(e, new Pub { V = 3 }); world.Set(e, new OwnerPriv { V = 21 }); });
        repl.BeginTick();
        view.ApplyDelta(observer, repl.WriteFor(slot: 0, worldB, aoi, ownerNetId: 999));
        Assert.True(view.TryGetEntity(200, out Entity after));
        Assert.Equal(before, after);                          // component delta, not respawn
        Assert.False(observer.Has<OwnerPriv>(after));         // still hidden across the handoff
        Assert.Equal(3, observer.Get<Pub>(after).V);          // public state did update
    }

    // --- Persist / Migrate channel filtering ---

    [Fact]
    public void PersistChannel_IncludesPersistOnly_ExcludesReplicateOnly()
    {
        ReplicationRegistry r = Full();
        var w = new World();
        Spawn(w, 100, (world, e) =>
        {
            world.Set(e, new Pub { V = 1 });          // Default -> persisted
            world.Set(e, new PersistOnly { V = 2 });  // Persist -> persisted
            world.Set(e, new RepOnly { V = 3 });      // Replicate only -> NOT persisted
        });

        byte[] snap = SnapshotWriter.WriteFiltered(w, r, new HashSet<int> { 100 }, ReplicationChannels.Persist, null);
        (World client, ClientReplicationView view) = Apply(r, snap);

        Assert.True(view.TryGetEntity(100, out Entity c));
        Assert.True(client.Has<Pub>(c));
        Assert.True(client.Has<PersistOnly>(c));
        Assert.False(client.Has<RepOnly>(c));         // a replicate-only component is not written to the blob
    }

    [Fact]
    public void MigrateChannel_IncludesMigrateOnly_ExcludesPersistOnly_AndReplicateOnly()
    {
        ReplicationRegistry r = Full();
        var w = new World();
        Spawn(w, 100, (world, e) =>
        {
            world.Set(e, new Pub { V = 1 });          // Default -> migrated
            world.Set(e, new MigrateOnly { V = 2 });  // Migrate -> migrated
            world.Set(e, new PersistOnly { V = 3 });  // Persist only -> NOT migrated
            world.Set(e, new RepOnly { V = 4 });      // Replicate only -> NOT migrated
        });

        byte[] snap = SnapshotWriter.WriteFiltered(w, r, new HashSet<int> { 100 }, ReplicationChannels.Migrate, null);
        (World client, ClientReplicationView view) = Apply(r, snap);

        Assert.True(view.TryGetEntity(100, out Entity c));
        Assert.True(client.Has<Pub>(c));
        Assert.True(client.Has<MigrateOnly>(c));
        Assert.False(client.Has<PersistOnly>(c));
        Assert.False(client.Has<RepOnly>(c));
    }

    [Fact]
    public void NoneChannel_NeverWritten_OnAnyChannel()
    {
        var r = new ReplicationRegistry();
        r.Register<Pub>(1, (c, bw) => bw.Write(c.V), br => new Pub { V = br.ReadInt32() });
        r.Register<RepOnly>(Ext, (c, bw) => bw.Write(c.V), br => new RepOnly { V = br.ReadInt32() },
            channels: ReplicationChannels.None);       // in no channel: ECS-only, never serialized
        var w = new World();
        Spawn(w, 100, (world, e) => { world.Set(e, new Pub { V = 1 }); world.Set(e, new RepOnly { V = 2 }); });
        var ids = new HashSet<int> { 100 };

        foreach (ReplicationChannels ch in new[] { ReplicationChannels.Replicate, ReplicationChannels.Persist, ReplicationChannels.Migrate })
        {
            (World client, ClientReplicationView view) = Apply(r, SnapshotWriter.WriteFiltered(w, r, ids, ch, null));
            Assert.True(view.TryGetEntity(100, out Entity c));
            Assert.False(client.Has<RepOnly>(c));
        }
    }

    // --- byte-identical wire for a defaults registry (the compatibility guarantee) ---

    private struct Def : IComponent { public int V; }   // id 16, ext, Default

    private static ReplicationRegistry Defaults()
    {
        var r = new ReplicationRegistry();
        r.Register<Pub>(1, (c, bw) => bw.Write(c.V), br => new Pub { V = br.ReadInt32() });               // built-in Default
        r.Register<Def>(Ext, (c, bw) => bw.Write(c.V), br => new Def { V = br.ReadInt32() });             // ext Default
        return r;
    }

    [Fact]
    public void DefaultsRegistry_Snapshot_IsChannelAndOwnerInvariant()
    {
        ReplicationRegistry r = Defaults();
        var w = new World();
        Spawn(w, 100, (world, e) => { world.Set(e, new Pub { V = 7 }); world.Set(e, new Def { V = 8 }); });
        Spawn(w, 200, (world, e) => { world.Set(e, new Pub { V = 9 }); world.Set(e, new Def { V = 10 }); });
        var ids = new HashSet<int> { 100, 200 };

        byte[] rep = SnapshotWriter.WriteFiltered(w, r, ids, ReplicationChannels.Replicate, null);
        byte[] per = SnapshotWriter.WriteFiltered(w, r, ids, ReplicationChannels.Persist, null);
        byte[] mig = SnapshotWriter.WriteFiltered(w, r, ids, ReplicationChannels.Migrate, null);
        byte[] owned = SnapshotWriter.WriteFiltered(w, r, ids, ReplicationChannels.Replicate, 100);

        // A registry using only Default writes every component on every channel regardless of owner -> byte-identical.
        Assert.Equal(rep, per);
        Assert.Equal(rep, mig);
        Assert.Equal(rep, owned);
    }

    [Fact]
    public void DefaultsRegistry_Delta_IsOwnerInvariant()
    {
        ReplicationRegistry r = Defaults();
        var w = new World();
        Spawn(w, 100, (world, e) => { world.Set(e, new Pub { V = 7 }); world.Set(e, new Def { V = 8 }); });
        var ids = new HashSet<int> { 100 };

        var a = new AoiDeltaReplicator(r); a.BeginTick();
        var b = new AoiDeltaReplicator(r); b.BeginTick();
        byte[] noOwner = a.WriteFor(0, w, ids, ownerNetId: null);
        byte[] withOwner = b.WriteFor(0, w, ids, ownerNetId: 5);

        Assert.Equal(noOwner, withOwner);   // no OwnerOnly component -> owner has no effect on the wire
    }

    [Fact]
    public void BuiltIn_IsWritten_OnEveryChannel()
    {
        // A built-in keeps all channels: it must appear on Replicate, Persist AND Migrate.
        ReplicationRegistry r = Full();
        var w = new World();
        Spawn(w, 100, (world, e) => world.Set(e, new Pub { V = 42 }));
        var ids = new HashSet<int> { 100 };

        foreach (ReplicationChannels ch in new[] { ReplicationChannels.Replicate, ReplicationChannels.Persist, ReplicationChannels.Migrate })
        {
            (World client, ClientReplicationView view) = Apply(r, SnapshotWriter.WriteFiltered(w, r, ids, ch, null));
            Assert.True(view.TryGetEntity(100, out Entity c));
            Assert.True(client.Has<Pub>(c));
            Assert.Equal(42, client.Get<Pub>(c).V);
        }
    }

    // --- ServerReplicator (whole-world delta) channel parity ---
    // ServerReplicator is the documented whole-world delta client-serving path (USING "For bandwidth"), so it must
    // honour the same channels as SnapshotWriter / AoiDeltaReplicator: only Replicate components on its wire, and an
    // OwnerOnly component only to the client that owns the entity.

    private static (World client, ClientReplicationView view) ApplyDelta(ReplicationRegistry r, byte[] delta)
    {
        var client = new World();
        var view = new ClientReplicationView(r);
        view.ApplyDelta(client, delta);
        return (client, view);
    }

    [Fact]
    public void ServerReplicator_Delta_Replicate_ExcludesServerOnly_PersistOnly_MigrateOnly()
    {
        ReplicationRegistry r = Full();
        var w = new World();
        Spawn(w, 100, (world, e) =>
        {
            world.Set(e, new Pub { V = 1 });          // Default -> replicated
            world.Set(e, new RepOnly { V = 2 });      // Replicate -> replicated
            world.Set(e, new ServerOnly { V = 3 });   // Persist|Migrate -> NOT replicated
            world.Set(e, new PersistOnly { V = 4 });  // Persist only -> NOT replicated
            world.Set(e, new MigrateOnly { V = 5 });  // Migrate only -> NOT replicated
        });

        var repl = new ServerReplicator(r);
        repl.Capture(w);
        (World client, ClientReplicationView view) = ApplyDelta(r, repl.WriteFor(slot: 0)); // baseline -1 full delta

        Assert.True(view.TryGetEntity(100, out Entity c));
        Assert.True(client.Has<Pub>(c));
        Assert.True(client.Has<RepOnly>(c));
        Assert.False(client.Has<ServerOnly>(c));      // server-only state never on the delta wire
        Assert.False(client.Has<PersistOnly>(c));
        Assert.False(client.Has<MigrateOnly>(c));
    }

    [Fact]
    public void ServerReplicator_Delta_OwnerOnly_ReachesOnlyTheOwner()
    {
        ReplicationRegistry r = Full();
        var w = new World();
        Spawn(w, 100, (world, e) => { world.Set(e, new Pub { V = 1 }); world.Set(e, new OwnerPriv { V = 10 }); });
        Spawn(w, 200, (world, e) => { world.Set(e, new Pub { V = 2 }); world.Set(e, new OwnerPriv { V = 20 }); });

        var repl = new ServerReplicator(r);
        repl.Capture(w);
        // One shared capture, projected per client at WriteFor: A owns 100, B owns 200.
        (World cA, ClientReplicationView vA) = ApplyDelta(r, repl.WriteFor(slot: 0, ownerNetId: 100));
        (World cB, ClientReplicationView vB) = ApplyDelta(r, repl.WriteFor(slot: 1, ownerNetId: 200));

        Assert.True(cA.Has<OwnerPriv>(vA.Entities[100]));    // own private state present
        Assert.False(cA.Has<OwnerPriv>(vA.Entities[200]));   // the OTHER player's private state is NOT leaked
        Assert.True(cA.Has<Pub>(vA.Entities[200]));          // ...but its public state is
        Assert.True(cB.Has<OwnerPriv>(vB.Entities[200]));
        Assert.False(cB.Has<OwnerPriv>(vB.Entities[100]));

        // Served with no owner (an unowned full delta): OwnerOnly reaches nobody.
        (World cN, ClientReplicationView vN) = ApplyDelta(r, repl.WriteFor(slot: 2, ownerNetId: null));
        Assert.False(cN.Has<OwnerPriv>(vN.Entities[100]));
        Assert.False(cN.Has<OwnerPriv>(vN.Entities[200]));
    }

    [Fact]
    public void ServerReplicator_Delta_OtherPlayersOwnerOnlyChange_ProducesNoChurn()
    {
        // The baseline-projection teeth: after the observer holds a baseline, a change to ANOTHER player's
        // OwnerOnly component must not surface in the observer's delta at all (not even as a spurious removal).
        // That only holds if WriteFor projects the acked baseline with the same owner scope as the current snapshot.
        ReplicationRegistry r = Full();
        var w = new World();
        Spawn(w, 100, (world, e) => { world.Set(e, new Pub { V = 1 }); world.Set(e, new OwnerPriv { V = 10 }); });
        Entity e200 = Spawn(w, 200, (world, e) => { world.Set(e, new Pub { V = 2 }); world.Set(e, new OwnerPriv { V = 20 }); });

        var repl = new ServerReplicator(r);
        int seq1 = repl.Capture(w);
        repl.WriteFor(slot: 0, ownerNetId: 100);   // full baseline to observer whose player is 100
        repl.Acknowledge(0, seq1);

        w.Set(e200, new OwnerPriv { V = 99 });      // ONLY the non-owned player's private state changed
        repl.Capture(w);
        byte[] delta = repl.WriteFor(slot: 0, ownerNetId: 100);

        // Decode the delta header ([baselineSeq][snapshotSeq][removedCount][removed...][changedCount]): observer 100
        // can see nothing changed, so both counts are zero.
        using var br = new System.IO.BinaryReader(new System.IO.MemoryStream(delta));
        br.ReadInt32(); br.ReadInt32();             // baselineSeq, snapshotSeq
        int removed = br.ReadInt32();
        for (int i = 0; i < removed; i++) br.ReadInt32();
        int changed = br.ReadInt32();
        Assert.Equal(0, removed);
        Assert.Equal(0, changed);                   // 200 is NOT reported changed -> baseline projected consistently
    }

    [Fact]
    public void ServerReplicator_DefaultsRegistry_Delta_IsOwnerInvariant()
    {
        ReplicationRegistry r = Defaults();
        var w = new World();
        Spawn(w, 100, (world, e) => { world.Set(e, new Pub { V = 7 }); world.Set(e, new Def { V = 8 }); });
        Spawn(w, 200, (world, e) => { world.Set(e, new Pub { V = 9 }); world.Set(e, new Def { V = 10 }); });

        var a = new ServerReplicator(r); a.Capture(w);
        var b = new ServerReplicator(r); b.Capture(w);
        byte[] noOwner = a.WriteFor(0, ownerNetId: null);
        byte[] withOwner = b.WriteFor(0, ownerNetId: 5);

        Assert.Equal(noOwner, withOwner);   // no OwnerOnly component -> owner has no effect on the wire (byte-identical)
    }
}
