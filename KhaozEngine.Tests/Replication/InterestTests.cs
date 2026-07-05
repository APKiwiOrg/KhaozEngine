using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

public class InterestTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(
            typeId: 1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        return r;
    }

    [Fact]
    public void InterestGrid_Query_ReturnsOnlyWithinRadius()
    {
        var grid = new InterestGrid(cellSize: 10f);
        grid.Insert(1, 0, 0);
        grid.Insert(2, 5, 0);    // distance 5
        grid.Insert(3, 100, 0);  // far

        HashSet<long> set = grid.Query(0, 0, radius: 8f);

        Assert.Contains(1, set);
        Assert.Contains(2, set);
        Assert.DoesNotContain(3, set);
    }

    [Fact]
    public void Aoi_FilteredSnapshot_SpawnsInRange_DespawnsOnLeave()
    {
        var registry = NewRegistry();
        var server = new World();
        Entity e1 = server.Spawn(); server.Set(e1, new NetId(1)); server.Set(e1, new Pos { X = 0, Y = 0 });
        Entity e2 = server.Spawn(); server.Set(e2, new NetId(2)); server.Set(e2, new Pos { X = 5, Y = 0 });
        Entity e3 = server.Spawn(); server.Set(e3, new NetId(3)); server.Set(e3, new Pos { X = 100, Y = 0 });

        var grid = new InterestGrid(10f);
        grid.Clear();
        server.ForEach<NetId, Pos>((Entity e, ref NetId id, ref Pos p) => grid.Insert(id.Value, p.X, p.Y));

        var client = new World();
        var view = new ClientReplicationView(registry);

        // Viewpoint at origin: entities 1 and 2 are in range; 3 is not.
        HashSet<long> nearOrigin = grid.Query(0, 0, 8f);
        view.Apply(client, SnapshotWriter.WriteFiltered(server, registry, nearOrigin));
        Assert.True(view.TryGetEntity(1, out _));
        Assert.True(view.TryGetEntity(2, out _));
        Assert.False(view.TryGetEntity(3, out _));   // out of interest -> never spawned

        // Viewpoint jumps to entity 3: 1 and 2 leave interest, 3 enters.
        HashSet<long> nearThree = grid.Query(100, 0, 8f);
        view.Apply(client, SnapshotWriter.WriteFiltered(server, registry, nearThree));
        Assert.False(view.TryGetEntity(1, out _));   // left interest -> despawned
        Assert.False(view.TryGetEntity(2, out _));
        Assert.True(view.TryGetEntity(3, out _));    // entered interest -> spawned
    }
}
