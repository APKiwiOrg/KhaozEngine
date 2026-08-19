using System;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

public class ReplicationTests
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

    /// <summary>
    /// A duplicate type id is rejected at registration, and the registry it was rejected on still writes exactly one
    /// frame for that component.
    /// <para>
    /// The throw is not the interesting half. What an ACCEPTED duplicate would do is: the codec list the snapshot
    /// writer iterates is in registration order, so a second entry for one id emits that component's frame twice per
    /// entity, and the cell-blob walk retires every candidate wire generation on its no-repeat rule, which
    /// quarantines every blob the server ever wrote. The rejection therefore has to land before the codec joins that
    /// list, which is what the second half of this test pins.
    /// </para>
    /// </summary>
    [Fact]
    public void Register_DuplicateTypeId_Throws_AndLeavesTheRegistryWritingOneFrame()
    {
        ReplicationRegistry registry = NewRegistry();

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Register<Pos>(
            typeId: 1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() }));
        Assert.Equal("Type id 1 already registered.", ex.Message);

        var world = new World();
        Entity e = world.Spawn();
        world.Set(e, new NetId(7));
        world.Set(e, new Pos { X = 1, Y = 2 });
        Assert.Equal(SnapshotWriter.Write(world, NewRegistry()), SnapshotWriter.Write(world, registry));
    }

    [Fact]
    public void Snapshot_RoundTrips_TwoEntities_IntoClientWorld()
    {
        var registry = NewRegistry();
        var server = new World();
        Entity e1 = server.Spawn(); server.Set(e1, new NetId(100)); server.Set(e1, new Pos { X = 1, Y = 2 });
        Entity e2 = server.Spawn(); server.Set(e2, new NetId(200)); server.Set(e2, new Pos { X = 3, Y = 4 });

        byte[] snap = SnapshotWriter.Write(server, registry);

        var client = new World();
        var view = new ClientReplicationView(registry);
        view.Apply(client, snap);

        Assert.True(view.TryGetEntity(100, out Entity c1));
        Assert.True(view.TryGetEntity(200, out Entity c2));
        Assert.Equal(1f, client.Get<Pos>(c1).X);
        Assert.Equal(2f, client.Get<Pos>(c1).Y);
        Assert.Equal(3f, client.Get<Pos>(c2).X);
        Assert.Equal(4f, client.Get<Pos>(c2).Y);
    }

    [Fact]
    public void Apply_DespawnsEntities_AbsentFromSnapshot()
    {
        var registry = NewRegistry();
        var client = new World();
        var view = new ClientReplicationView(registry);

        // First snapshot: two entities.
        var server = new World();
        Entity a = server.Spawn(); server.Set(a, new NetId(1)); server.Set(a, new Pos { X = 0, Y = 0 });
        Entity b = server.Spawn(); server.Set(b, new NetId(2)); server.Set(b, new Pos { X = 0, Y = 0 });
        view.Apply(client, SnapshotWriter.Write(server, registry));
        Assert.True(view.TryGetEntity(2, out Entity clientB));

        // Second snapshot: entity 2 gone on the server.
        server.Despawn(b);
        view.Apply(client, SnapshotWriter.Write(server, registry));

        Assert.True(view.TryGetEntity(1, out _));
        Assert.False(view.TryGetEntity(2, out _));     // despawned on the client
        Assert.False(client.IsAlive(clientB));         // the local entity is actually gone
    }

    [Fact]
    public void Interpolate_AtHalf_YieldsMidpoint()
    {
        var registry = NewRegistry();
        var client = new World();
        var view = new ClientReplicationView(registry);
        var server = new World();
        Entity s = server.Spawn(); server.Set(s, new NetId(7));

        // Snapshot A at (0,0).
        server.Set(s, new Pos { X = 0, Y = 0 });
        view.Apply(client, SnapshotWriter.Write(server, registry));
        // Snapshot B at (10,20).
        server.Set(s, new Pos { X = 10, Y = 20 });
        view.Apply(client, SnapshotWriter.Write(server, registry));

        view.Interpolate(client, 0.5f);

        Assert.True(view.TryGetEntity(7, out Entity c));
        Assert.Equal(5f, client.Get<Pos>(c).X, 4);
        Assert.Equal(10f, client.Get<Pos>(c).Y, 4);
    }
}
