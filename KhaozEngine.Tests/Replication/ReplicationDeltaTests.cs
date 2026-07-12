using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

public class ReplicationDeltaTests
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

    // Reads the delta header (baselineSeq, snapshotSeq, removedCount, changedCount).
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

    [Fact]
    public void NewClient_GetsFullDelta_AndConverges()
    {
        var registry = NewRegistry();
        var server = new World();
        Entity e1 = server.Spawn(); server.Set(e1, new NetId(1)); server.Set(e1, new Pos { X = 1, Y = 2 });
        Entity e2 = server.Spawn(); server.Set(e2, new NetId(2)); server.Set(e2, new Pos { X = 3, Y = 4 });

        var repl = new ServerReplicator(registry);
        int seq1 = repl.Capture(server);
        byte[] d1 = repl.WriteFor(slot: 0);

        (int baseSeq, _, _, int changed) = Header(d1);
        Assert.Equal(-1, baseSeq);   // no baseline -> full
        Assert.Equal(2, changed);    // both entities

        var client = new World();
        var view = new ClientReplicationView(registry);
        view.ApplyDelta(client, d1);

        Assert.Equal(seq1, view.LastAppliedSeq);
        Assert.True(view.TryGetEntity(1, out Entity c1));
        Assert.True(view.TryGetEntity(2, out Entity c2));
        Assert.Equal(1f, client.Get<Pos>(c1).X);
        Assert.Equal(4f, client.Get<Pos>(c2).Y);
    }

    [Fact]
    public void AfterAck_OnlyChangedEntity_IsSent()
    {
        var registry = NewRegistry();
        var server = new World();
        Entity e1 = server.Spawn(); server.Set(e1, new NetId(1)); server.Set(e1, new Pos { X = 1, Y = 1 });
        Entity e2 = server.Spawn(); server.Set(e2, new NetId(2)); server.Set(e2, new Pos { X = 2, Y = 2 });

        var repl = new ServerReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);

        int seq1 = repl.Capture(server);
        view.ApplyDelta(client, repl.WriteFor(0));
        repl.Acknowledge(0, seq1);

        // Only entity 1 moves.
        server.Get<Pos>(e1) = new Pos { X = 9, Y = 9 };
        repl.Capture(server);
        byte[] d2 = repl.WriteFor(0);

        (int baseSeq, _, int removed, int changed) = Header(d2);
        Assert.Equal(seq1, baseSeq);
        Assert.Equal(0, removed);
        Assert.Equal(1, changed);    // only entity 1

        view.ApplyDelta(client, d2);
        Assert.True(view.TryGetEntity(1, out Entity c1));
        Assert.True(view.TryGetEntity(2, out Entity c2));
        Assert.Equal(9f, client.Get<Pos>(c1).X);     // updated
        Assert.Equal(2f, client.Get<Pos>(c2).X);     // untouched
    }

    [Fact]
    public void NoChange_ProducesEmptyDelta()
    {
        var registry = NewRegistry();
        var server = new World();
        Entity e1 = server.Spawn(); server.Set(e1, new NetId(1)); server.Set(e1, new Pos { X = 1, Y = 1 });

        var repl = new ServerReplicator(registry);
        int seq1 = repl.Capture(server);
        repl.Acknowledge(0, seq1);
        repl.Capture(server); // identical state

        byte[] d = repl.WriteFor(0);
        (_, _, int removed, int changed) = Header(d);
        Assert.Equal(0, removed);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Despawn_AppearsInDelta_AndClientDespawns()
    {
        var registry = NewRegistry();
        var server = new World();
        Entity e1 = server.Spawn(); server.Set(e1, new NetId(1)); server.Set(e1, new Pos { X = 1, Y = 1 });
        Entity e2 = server.Spawn(); server.Set(e2, new NetId(2)); server.Set(e2, new Pos { X = 2, Y = 2 });

        var repl = new ServerReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);

        int seq1 = repl.Capture(server);
        view.ApplyDelta(client, repl.WriteFor(0));
        repl.Acknowledge(0, seq1);

        server.Despawn(e2);
        repl.Capture(server);
        byte[] d2 = repl.WriteFor(0);

        (_, _, int removed, _) = Header(d2);
        Assert.Equal(1, removed);

        view.ApplyDelta(client, d2);
        Assert.True(view.TryGetEntity(1, out _));
        Assert.False(view.TryGetEntity(2, out _));
    }

    [Fact]
    public void Delta_Interpolates_ChangedComponent_ToMidpoint()
    {
        var registry = NewRegistry();
        var server = new World();
        Entity e1 = server.Spawn(); server.Set(e1, new NetId(5)); server.Set(e1, new Pos { X = 0, Y = 0 });

        var repl = new ServerReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);

        int seq1 = repl.Capture(server);
        view.ApplyDelta(client, repl.WriteFor(0));
        repl.Acknowledge(0, seq1);

        server.Get<Pos>(e1) = new Pos { X = 10, Y = 20 };
        repl.Capture(server);
        view.ApplyDelta(client, repl.WriteFor(0));

        view.Interpolate(client, 0.5f);
        Assert.True(view.TryGetEntity(5, out Entity c));
        Assert.Equal(5f, client.Get<Pos>(c).X, 4);
        Assert.Equal(10f, client.Get<Pos>(c).Y, 4);
    }

    // A zero-field tag codec (zero payload bytes, presence itself is the state) must round-trip through
    // Capture / WriteFor / ApplyDelta: presence on the tagged entity, absence on the other, and a later tag
    // removal carried by the delta's removed-component list. This is the wire-level guard for the World.TryGet
    // tag fix (the codecs' TrySerialize / CaptureInto call TryGet on every registered component, which used to
    // crash on a tag's missing column).
    [Fact]
    public void Delta_RoundTrips_TagComponent_PresenceAndRemoval()
    {
        var registry = NewRegistry();
        registry.Register<Marked>(
            typeId: 2,
            write: (m, bw) => { },
            read: br => default);

        var server = new World();
        Entity e1 = server.Spawn(); server.Set(e1, new NetId(1)); server.Set(e1, new Pos { X = 1, Y = 1 });
        Entity e2 = server.Spawn(); server.Set(e2, new NetId(2)); server.Set(e2, new Pos { X = 2, Y = 2 });
        server.Set(e1, new Marked());

        var repl = new ServerReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);

        int seq1 = repl.Capture(server);
        view.ApplyDelta(client, repl.WriteFor(0));
        repl.Acknowledge(0, seq1);

        Assert.True(view.TryGetEntity(1, out Entity c1));
        Assert.True(view.TryGetEntity(2, out Entity c2));
        Assert.True(client.Has<Marked>(c1));     // presence round-tripped
        Assert.False(client.Has<Marked>(c2));    // absence round-tripped (no leak)

        // Removing the tag server-side must reach the client via the delta's removed-component list.
        server.Remove<Marked>(e1);
        repl.Capture(server);
        view.ApplyDelta(client, repl.WriteFor(0));
        Assert.False(client.Has<Marked>(c1));
    }

    private struct Marked : IComponent { }   // zero-field tag
}
