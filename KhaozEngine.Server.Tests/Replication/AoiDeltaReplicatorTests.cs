using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Per-client, AoI-scoped, NetId-keyed delta encoder: entered -> full, stayed+changed -> component delta,
/// left -> despawn, unchanged in-AoI -> nothing. Emits the same wire format ClientReplicationView.ApplyDelta reads.
/// </summary>
public class AoiDeltaReplicatorTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(
            typeId: 1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() },
            lerp: (a, b, t) => new Pos { X = a.X + (b.X - a.X) * t, Y = a.Y + (b.Y - a.Y) * t });
        return r;
    }

    private static (int baseSeq, int snapSeq, int removed, int changed) Header(byte[] d)
    {
        LegacyDeltaHeader h = LegacyDeltaWire.ReadHeader(d);
        return (h.Baseline, h.Snapshot, h.RemovedNetIds.Length, h.ChangedCount);
    }

    private static Entity Spawn(World w, int netId, float x, float y)
    {
        Entity e = w.Spawn();
        w.Set(e, new NetId(netId));
        w.Set(e, new Pos { X = x, Y = y });
        return e;
    }

    private static HashSet<long> Aoi(params long[] ids) => new(ids);

    [Fact]
    public void Enter_sends_full_for_each_in_aoi_entity()
    {
        var registry = NewRegistry();
        var world = new World();
        Spawn(world, 1, 1, 2);
        Spawn(world, 2, 3, 4);

        var repl = new AoiDeltaReplicator(registry);
        repl.BeginTick();
        byte[] d = repl.WriteFor(slot: 0, world, Aoi(1, 2));

        (int baseSeq, _, int removed, int changed) = Header(d);
        Assert.Equal(-1, baseSeq);   // no baseline -> full
        Assert.Equal(0, removed);
        Assert.Equal(2, changed);    // both entered

        var client = new World();
        var view = new ClientReplicationView(registry);
        view.ApplyDelta(client, d);
        Assert.True(view.TryGetEntity(1, out Entity c1));
        Assert.True(view.TryGetEntity(2, out Entity c2));
        Assert.Equal(1f, client.Get<Pos>(c1).X);
        Assert.Equal(4f, client.Get<Pos>(c2).Y);
    }

    [Fact]
    public void Unchanged_in_aoi_after_ack_sends_nothing()
    {
        var registry = NewRegistry();
        var world = new World();
        Spawn(world, 1, 1, 1);
        Spawn(world, 2, 2, 2);

        var repl = new AoiDeltaReplicator(registry);
        int seq1 = repl.BeginTick();
        repl.WriteFor(0, world, Aoi(1, 2));
        repl.Acknowledge(0, seq1);

        // Nothing moved, same AoI.
        repl.BeginTick();
        byte[] d2 = repl.WriteFor(0, world, Aoi(1, 2));

        (int baseSeq, _, int removed, int changed) = Header(d2);
        Assert.Equal(seq1, baseSeq);
        Assert.Equal(0, removed);
        Assert.Equal(0, changed);    // idle -> empty delta
    }

    [Fact]
    public void Changed_sends_only_the_changed_entity()
    {
        var registry = NewRegistry();
        var world = new World();
        Entity e1 = Spawn(world, 1, 1, 1);
        Spawn(world, 2, 2, 2);

        var repl = new AoiDeltaReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);

        int seq1 = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(1, 2)));
        repl.Acknowledge(0, seq1);

        world.Set(e1, new Pos { X = 9, Y = 9 });   // only entity 1 moves
        repl.BeginTick();
        byte[] d2 = repl.WriteFor(0, world, Aoi(1, 2));

        (_, _, int removed, int changed) = Header(d2);
        Assert.Equal(0, removed);
        Assert.Equal(1, changed);    // only entity 1

        view.ApplyDelta(client, d2);
        Assert.Equal(9f, client.Get<Pos>(view.Entities[1]).X);
        Assert.Equal(2f, client.Get<Pos>(view.Entities[2]).X);   // untouched
    }

    [Fact]
    public void Leaving_aoi_despawns_on_client()
    {
        var registry = NewRegistry();
        var world = new World();
        Spawn(world, 1, 1, 1);
        Spawn(world, 2, 50, 50);

        var repl = new AoiDeltaReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);

        int seq1 = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(1, 2)));
        repl.Acknowledge(0, seq1);
        Assert.True(view.TryGetEntity(2, out _));

        // Entity 2 drifted out of this client's AoI (still alive in the world).
        repl.BeginTick();
        byte[] d2 = repl.WriteFor(0, world, Aoi(1));

        (_, _, int removed, _) = Header(d2);
        Assert.Equal(1, removed);   // entity 2 leaves -> despawn

        view.ApplyDelta(client, d2);
        Assert.True(view.TryGetEntity(1, out _));
        Assert.False(view.TryGetEntity(2, out _));
    }

    [Fact]
    public void Idle_entities_produce_a_near_empty_delta_after_the_first()
    {
        var registry = NewRegistry();
        var world = new World();
        var all = new HashSet<long>();
        for (int i = 1; i <= 50; i++) { Spawn(world, i, i, i); all.Add(i); }

        var repl = new AoiDeltaReplicator(registry);
        int seq1 = repl.BeginTick();
        byte[] first = repl.WriteFor(0, world, all);   // full: all 50 entities
        repl.Acknowledge(0, seq1);

        repl.BeginTick();
        byte[] idle = repl.WriteFor(0, world, all);    // nothing moved

        // The idle delta is just the header (baseSeq, snapSeq, removedCount=0, changedCount=0) = 16 bytes,
        // independent of the 50 entities in interest, while the first (full) snapshot is far larger.
        Assert.Equal(16, idle.Length);
        Assert.True(first.Length > 10 * idle.Length, $"first {first.Length} vs idle {idle.Length}");
    }

    [Fact]
    public void Same_netid_in_a_new_world_is_a_component_delta_not_a_respawn()
    {
        // Handoff transparency at the encoder level: an entity that stays in the client's AoI while its owning
        // World changes (a cell handoff serves from a different World) must read as a component delta keyed by
        // NetId, never despawn+respawn.
        var registry = NewRegistry();
        var worldA = new World();
        Spawn(worldA, 7, 1, 1);

        var repl = new AoiDeltaReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);

        int seq1 = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, worldA, Aoi(7)));
        repl.Acknowledge(0, seq1);
        Assert.True(view.TryGetEntity(7, out Entity before));

        // The entity is now served from a DIFFERENT World (as after a cell handoff), same NetId, new position.
        var worldB = new World();
        Spawn(worldB, 7, 5, 6);

        repl.BeginTick();
        byte[] d2 = repl.WriteFor(0, worldB, Aoi(7));
        (_, _, int removed, int changed) = Header(d2);
        Assert.Equal(0, removed);    // NOT despawned
        Assert.Equal(1, changed);    // component delta

        view.ApplyDelta(client, d2);
        Assert.True(view.TryGetEntity(7, out Entity after));
        Assert.Equal(before, after);                     // same client entity, no respawn
        Assert.Equal(5f, client.Get<Pos>(after).X);      // moved
    }

    [Fact]
    public void Deltas_interpolate_a_changed_component_to_the_midpoint()
    {
        var registry = NewRegistry();
        var world = new World();
        Entity e = Spawn(world, 5, 0, 0);

        var repl = new AoiDeltaReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);

        int seq1 = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(5)));
        repl.Acknowledge(0, seq1);

        world.Set(e, new Pos { X = 10, Y = 20 });
        repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(5)));

        view.Interpolate(client, 0.5f);
        Assert.Equal(5f, client.Get<Pos>(view.Entities[5]).X, 4);
        Assert.Equal(10f, client.Get<Pos>(view.Entities[5]).Y, 4);
    }

    [Fact]
    public void Skipped_ack_still_diffs_from_the_last_sent_projection()
    {
        var registry = NewRegistry();
        var world = new World();
        Entity e1 = Spawn(world, 1, 1, 1);
        Spawn(world, 2, 2, 2);

        var repl = new AoiDeltaReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);
        int seq1 = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(1, 2)));
        repl.Acknowledge(0, seq1);

        world.Set(e1, new Pos { X = 5, Y = 5 });
        int seq2 = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(1, 2)));   // seq2: applied, its ack never arrives

        int seq3 = repl.BeginTick();
        byte[] d3 = repl.WriteFor(0, world, Aoi(1, 2));

        // The reliable receiver already holds seq2, so seq3 names seq2 and carries nothing, whatever the ack state.
        (int baseSeq, int snapSeq, int removed, int changed) = Header(d3);
        Assert.Equal(seq2, baseSeq);
        Assert.Equal(seq3, snapSeq);
        Assert.Equal(0, removed);
        Assert.Equal(0, changed);

        view.ApplyDelta(client, d3);
        Assert.Equal(5f, client.Get<Pos>(view.Entities[1]).X);
        Assert.Equal(2f, client.Get<Pos>(view.Entities[2]).X);
    }

    private struct Hp : IComponent { public int Value; }

    // Pos plus a second component, so a re-entry that arrives partial (Pos only) is distinguishable from a whole one.
    private static ReplicationRegistry NewTwoComponentRegistry()
    {
        ReplicationRegistry r = NewRegistry();
        r.Register<Hp>(typeId: 2, write: (h, bw) => bw.Write(h.Value), read: br => new Hp { Value = br.ReadInt32() });
        return r;
    }

    private static Entity SpawnWithHp(World w, int netId, float x, int hp)
    {
        Entity e = Spawn(w, netId, x, x);
        w.Set(e, new Hp { Value = hp });
        return e;
    }

    // Reads the removed ids and, per changed entity, its id and isNew flag, skipping each entity's body. Pos (8 bytes)
    // and Hp (4 bytes) are the only registered components and both are built-ins (unframed).
    private static (List<long> removed, List<(long netId, bool isNew)> changed) Entries(byte[] d)
    {
        using var br = new BinaryReader(new MemoryStream(d));
        br.ReadInt32();
        br.ReadInt32();
        var removed = new List<long>();
        int removedCount = br.ReadInt32();
        for (int i = 0; i < removedCount; i++) removed.Add(br.ReadInt64());
        var changed = new List<(long, bool)>();
        int changedCount = br.ReadInt32();
        for (int i = 0; i < changedCount; i++)
        {
            long netId = br.ReadInt64();
            bool isNew = br.ReadByte() == 1;
            int removedComps = br.ReadInt32();
            for (int r = 0; r < removedComps; r++) br.ReadUInt16();
            for (ushort tid = br.ReadUInt16(); tid != 0; tid = br.ReadUInt16()) br.ReadBytes(tid == 1 ? 8 : 4);
            changed.Add((netId, isNew));
        }
        return (removed, changed);
    }

    [Fact]
    public void An_aoi_reentry_inside_the_ack_window_with_a_change_arrives_whole() =>
        AssertReentryInsideTheAckWindowArrivesWhole(moveWhileOut: true);

    [Fact]
    public void An_aoi_reentry_inside_the_ack_window_without_a_change_still_comes_back() =>
        AssertReentryInsideTheAckWindowArrivesWhole(moveWhileOut: false);

    private static void AssertReentryInsideTheAckWindowArrivesWhole(bool moveWhileOut)
    {
        ReplicationRegistry registry = NewTwoComponentRegistry();
        var world = new World();
        SpawnWithHp(world, 1, 1, 10);
        Entity x = SpawnWithHp(world, 2, 2, 20);

        var repl = new AoiDeltaReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);

        int seq1 = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(1, 2)));
        repl.Acknowledge(0, seq1);

        // T: X leaves the interest set. The removal is sent, and its ack has not reached the server.
        repl.BeginTick();
        byte[] dT = repl.WriteFor(0, world, Aoi(1));
        Assert.Equal(new List<long> { 2 }, Entries(dT).removed);
        if (moveWhileOut) world.Set(x, new Pos { X = 7, Y = 7 });

        // T+1: X is back. The last sent projection (T) lacks X, so it is written whole, whatever the ack state.
        int seqT1 = repl.BeginTick();
        byte[] dT1 = repl.WriteFor(0, world, Aoi(1, 2));
        Assert.Equal(new List<(long, bool)> { (2, true) }, Entries(dT1).changed);

        view.ApplyDelta(client, dT);
        Assert.False(view.TryGetEntity(2, out _));
        view.ApplyDelta(client, dT1);
        repl.Acknowledge(0, seqT1);

        Assert.True(view.TryGetEntity(2, out Entity cx));
        Assert.True(client.TryGet(cx, out Pos pos));
        Assert.Equal(moveWhileOut ? 7f : 2f, pos.X);
        Assert.True(client.TryGet(cx, out Hp hp));
        Assert.Equal(20, hp.Value);

        // The next idle tick is empty: the last sent projection holds X.
        repl.BeginTick();
        (List<long> removed, List<(long, bool)> changed) = Entries(repl.WriteFor(0, world, Aoi(1, 2)));
        Assert.Empty(removed);
        Assert.Empty(changed);
    }

    [Fact]
    public void A_readd_after_a_delivered_removal_arrives_whole_then_diffs_normally()
    {
        ReplicationRegistry registry = NewTwoComponentRegistry();
        var world = new World();
        Entity x = SpawnWithHp(world, 2, 2, 20);

        var repl = new AoiDeltaReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);
        int seq1 = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(2)));
        repl.Acknowledge(0, seq1);

        int seq2 = repl.BeginTick();
        byte[] removal = repl.WriteFor(0, world, Aoi());   // X removed at seq2
        Assert.Equal(new List<long> { 2 }, Entries(removal).removed);
        view.ApplyDelta(client, removal);
        Assert.False(view.TryGetEntity(2, out _));

        // seq3: the delivered predecessor (seq2) lacks X, so it returns whole.
        repl.BeginTick();
        byte[] readd = repl.WriteFor(0, world, Aoi(2));
        Assert.Equal(new List<(long, bool)> { (2, true) }, Entries(readd).changed);
        view.ApplyDelta(client, readd);
        repl.Acknowledge(0, seq2);                          // a late ack changes nothing
        Assert.True(view.TryGetEntity(2, out Entity cx));
        Assert.Equal(20, client.Get<Hp>(cx).Value);

        // seq4: the predecessor (seq3) holds X unchanged, so nothing is sent.
        repl.BeginTick();
        byte[] idle = repl.WriteFor(0, world, Aoi(2));
        Assert.Empty(Entries(idle).removed);
        Assert.Empty(Entries(idle).changed);
        view.ApplyDelta(client, idle);

        // A change is a component delta, not a whole entity.
        world.Set(x, new Pos { X = 9, Y = 9 });
        repl.BeginTick();
        byte[] moved = repl.WriteFor(0, world, Aoi(2));
        Assert.Equal(new List<(long, bool)> { (2, false) }, Entries(moved).changed);
        view.ApplyDelta(client, moved);
        Assert.Equal(9f, client.Get<Pos>(cx).X);
        Assert.Equal(20, client.Get<Hp>(cx).Value);
    }

    [Fact]
    public void Forget_clears_the_slot_and_the_next_serve_is_full()
    {
        ReplicationRegistry registry = NewTwoComponentRegistry();
        var world = new World();
        SpawnWithHp(world, 2, 2, 20);

        var repl = new AoiDeltaReplicator(registry);
        int seq1 = repl.BeginTick();
        repl.WriteFor(0, world, Aoi(2));
        repl.Acknowledge(0, seq1);
        repl.BeginTick();
        repl.WriteFor(0, world, Aoi());
        repl.WriteFor(1, world, Aoi(2));
        Assert.Equal(2, repl.LegacySlotCount);

        repl.Forget(0);
        Assert.Equal(1, repl.LegacySlotCount);

        // A recycled slot starts from a full snapshot, applied by a fresh receiver.
        int seq3 = repl.BeginTick();
        byte[] d = repl.WriteFor(0, world, Aoi(2));
        Assert.Equal(-1, Header(d).baseSeq);
        Assert.Equal(seq3, Header(d).snapSeq);
        var client = new World();
        var view = new ClientReplicationView(registry);
        view.ApplyDelta(client, d);
        Assert.True(view.TryGetEntity(2, out Entity cx));
        Assert.Equal(20, client.Get<Hp>(cx).Value);
    }

    // seq1: only entity 1, sent, applied and acked, so the receiver never holds X (net id 2).
    private static (AoiDeltaReplicator repl, World world, Entity x, World client, ClientReplicationView view)
        NewBrushRig()
    {
        ReplicationRegistry registry = NewTwoComponentRegistry();
        var world = new World();
        SpawnWithHp(world, 1, 1, 10);
        Entity x = SpawnWithHp(world, 2, 2, 20);
        var repl = new AoiDeltaReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);
        int seq1 = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(1)));
        repl.Acknowledge(0, seq1);
        return (repl, world, x, client, view);
    }

    [Fact]
    public void An_entity_that_arrives_and_leaves_inside_the_ack_window_is_removed_once()
    {
        (AoiDeltaReplicator repl, World world, _, World client, ClientReplicationView view) = NewBrushRig();

        repl.BeginTick();
        byte[] dT = repl.WriteFor(0, world, Aoi(1, 2));          // T: X arrives whole
        Assert.Equal(new List<(long, bool)> { (2, true) }, Entries(dT).changed);
        repl.BeginTick();
        byte[] dT1 = repl.WriteFor(0, world, Aoi(1));            // T+1: X leaves, T unacked
        Assert.Equal(new List<long> { 2 }, Entries(dT1).removed);
        // T+2, still unacked: the delivered predecessor (T+1) already lacks X, so nothing repeats.
        repl.BeginTick();
        byte[] dT2 = repl.WriteFor(0, world, Aoi(1));
        Assert.Empty(Entries(dT2).removed);
        Assert.Empty(Entries(dT2).changed);

        view.ApplyDelta(client, dT);
        Assert.True(view.TryGetEntity(2, out _));
        view.ApplyDelta(client, dT1);
        Assert.False(view.TryGetEntity(2, out _));
        view.ApplyDelta(client, dT2);
        Assert.False(view.TryGetEntity(2, out _));
        Assert.True(view.TryGetEntity(1, out _));
    }

    [Fact]
    public void An_entity_that_arrives_leaves_and_returns_inside_the_ack_window_returns_whole()
    {
        (AoiDeltaReplicator repl, World world, Entity x, World client, ClientReplicationView view) = NewBrushRig();

        repl.BeginTick();
        byte[] dT = repl.WriteFor(0, world, Aoi(1, 2));          // T: arrives
        repl.BeginTick();
        byte[] dT1 = repl.WriteFor(0, world, Aoi(1));            // T+1: leaves
        world.Set(x, new Pos { X = 7, Y = 7 });
        repl.BeginTick();
        byte[] dT2 = repl.WriteFor(0, world, Aoi(1, 2));         // T+2: back, still unacked
        Assert.Equal(new List<(long, bool)> { (2, true) }, Entries(dT2).changed);

        view.ApplyDelta(client, dT);
        view.ApplyDelta(client, dT1);
        Assert.False(view.TryGetEntity(2, out _));
        view.ApplyDelta(client, dT2);

        Assert.True(view.TryGetEntity(2, out Entity cx));
        Assert.Equal(7f, client.Get<Pos>(cx).X);
        Assert.Equal(20, client.Get<Hp>(cx).Value);
        repl.BeginTick();
        (List<long> removed, List<(long, bool)> changed) = Entries(repl.WriteFor(0, world, Aoi(1, 2)));
        Assert.Empty(removed);
        Assert.Empty(changed);
    }

    [Fact]
    public void A_leave_after_the_arrival_is_acked_is_removed_once()
    {
        (AoiDeltaReplicator repl, World world, _, World client, ClientReplicationView view) = NewBrushRig();

        int seqT = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(1, 2)));
        repl.Acknowledge(0, seqT);

        repl.BeginTick();
        byte[] leave = repl.WriteFor(0, world, Aoi(1));
        Assert.Equal(new List<long> { 2 }, Entries(leave).removed);
        // Unacked: the delivered predecessor already lacks X, so the removal is not repeated.
        repl.BeginTick();
        byte[] after = repl.WriteFor(0, world, Aoi(1));
        Assert.Empty(Entries(after).removed);

        view.ApplyDelta(client, leave);
        view.ApplyDelta(client, after);
        Assert.False(view.TryGetEntity(2, out _));
        Assert.True(view.TryGetEntity(1, out _));
    }
}
