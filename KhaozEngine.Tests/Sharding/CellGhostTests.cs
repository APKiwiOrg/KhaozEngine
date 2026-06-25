using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class CellGhostTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry PosRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(1,
            (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        return r;
    }

    private static byte[] SnapshotOf(ReplicationRegistry registry, params (int netId, float x, float y)[] ents)
    {
        var src = new World();
        foreach ((int netId, float x, float y) in ents)
        {
            Entity e = src.Spawn();
            src.Set(e, new NetId(netId));
            src.Set(e, new Pos { X = x, Y = y });
        }
        return SnapshotWriter.Write(src, registry);
    }

    [Fact]
    public void ApplyGhostSnapshot_SpawnsTaggedGhost_WithMirroredValues()
    {
        ReplicationRegistry registry = PosRegistry();
        var cell = new CellSim(new CellCoord(1, 0), 0.1f, registry, 100f);

        cell.ApplyGhostSnapshot(new CellCoord(0, 0), SnapshotOf(registry, (7, 95f, 50f)));

        Assert.Equal(1, cell.GhostCount);
        Assert.True(cell.TryGetGhost(7, out Entity g));
        Assert.Equal(95f, cell.World.Get<Pos>(g).X);
        Assert.Equal(50f, cell.World.Get<Pos>(g).Y);
        Assert.True(cell.World.Has<Ghost>(g));                       // tagged read-only
        Assert.Equal(new CellCoord(0, 0), cell.World.Get<Ghost>(g).Source);
    }

    [Fact]
    public void ApplyGhostSnapshot_Resync_UpdatesPresent_AndDespawnsAbsent()
    {
        ReplicationRegistry registry = PosRegistry();
        var cell = new CellSim(new CellCoord(1, 0), 0.1f, registry, 100f);
        var source = new CellCoord(0, 0);

        cell.ApplyGhostSnapshot(source, SnapshotOf(registry, (7, 95f, 50f), (8, 96f, 60f)));
        Assert.Equal(2, cell.GhostCount);

        // Next sync: 7 moved, 8 left the border.
        cell.ApplyGhostSnapshot(source, SnapshotOf(registry, (7, 98f, 55f)));

        Assert.Equal(1, cell.GhostCount);
        Assert.True(cell.TryGetGhost(7, out Entity g));
        Assert.Equal(98f, cell.World.Get<Pos>(g).X);
        Assert.False(cell.TryGetGhost(8, out _));                    // despawned
    }

    [Fact]
    public void ClearGhostsFrom_DespawnsThatSourcesGhosts()
    {
        ReplicationRegistry registry = PosRegistry();
        var cell = new CellSim(new CellCoord(1, 0), 0.1f, registry, 100f);
        var source = new CellCoord(0, 0);
        cell.ApplyGhostSnapshot(source, SnapshotOf(registry, (7, 95f, 50f)));
        Assert.True(cell.TryGetGhost(7, out Entity g));

        cell.ClearGhostsFrom(source);

        Assert.Equal(0, cell.GhostCount);
        Assert.False(cell.TryGetGhost(7, out _));
        Assert.False(cell.World.IsAlive(g));
        Assert.Contains(source, cell.GhostSources);                 // view retained, just emptied
    }

    [Fact]
    public void Ghosts_FromDifferentSources_Coexist()
    {
        ReplicationRegistry registry = PosRegistry();
        var cell = new CellSim(new CellCoord(1, 1), 0.1f, registry, 100f);

        cell.ApplyGhostSnapshot(new CellCoord(0, 1), SnapshotOf(registry, (7, 95f, 150f)));
        cell.ApplyGhostSnapshot(new CellCoord(1, 0), SnapshotOf(registry, (8, 150f, 95f)));

        Assert.Equal(2, cell.GhostCount);
        Assert.True(cell.TryGetGhost(7, out _));
        Assert.True(cell.TryGetGhost(8, out _));
    }
}
