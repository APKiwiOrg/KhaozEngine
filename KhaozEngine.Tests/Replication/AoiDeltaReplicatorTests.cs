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
        using var br = new BinaryReader(new MemoryStream(d));
        int b = br.ReadInt32();
        int s = br.ReadInt32();
        int removed = br.ReadInt32();
        for (int i = 0; i < removed; i++) br.ReadInt32();
        int changed = br.ReadInt32();
        return (b, s, removed, changed);
    }

    private static Entity Spawn(World w, int netId, float x, float y)
    {
        Entity e = w.Spawn();
        w.Set(e, new NetId(netId));
        w.Set(e, new Pos { X = x, Y = y });
        return e;
    }

    private static HashSet<int> Aoi(params int[] ids) => new(ids);

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
        var all = new HashSet<int>();
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
        Assert.Equal(before, after);                     // same client entity — no respawn
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
    public void Skipped_ack_keeps_diffing_from_the_last_acked_baseline()
    {
        var registry = NewRegistry();
        var world = new World();
        Entity e1 = Spawn(world, 1, 1, 1);
        Spawn(world, 2, 2, 2);

        var repl = new AoiDeltaReplicator(registry);
        int seq1 = repl.BeginTick();
        repl.WriteFor(0, world, Aoi(1, 2));
        repl.Acknowledge(0, seq1);

        world.Set(e1, new Pos { X = 5, Y = 5 });
        repl.BeginTick();
        repl.WriteFor(0, world, Aoi(1, 2));   // seq2 delta — its ack is dropped (never Acknowledged)

        world.Set(e1, new Pos { X = 9, Y = 9 });
        int seq3 = repl.BeginTick();
        byte[] d3 = repl.WriteFor(0, world, Aoi(1, 2));

        // With seq2's ack lost, the server still diffs from seq1 (the last acked baseline), re-sending entity 1.
        (int baseSeq, int snapSeq, int removed, int changed) = Header(d3);
        Assert.Equal(seq1, baseSeq);
        Assert.Equal(seq3, snapSeq);
        Assert.Equal(0, removed);
        Assert.Equal(1, changed);   // entity 1 still carried (its change since seq1 is unacked)
    }
}
