using System;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class ShardHostServingTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry Registry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(1,
            (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        return r;
    }

    private static bool PosAccessor(World world, Entity e, out float x, out float y)
    {
        if (world.TryGet(e, out Pos p)) { x = p.X; y = p.Y; return true; }
        x = y = 0f;
        return false;
    }

    // cellSize 100, overlap margin 30 (>= interest radius 30), positions from Pos.
    private static ShardHost ServingHost(ReplicationRegistry registry, float margin = 30f) =>
        new(cellSize: 100f, tickSeconds: 0.1f, registry, interestCellSize: 100f,
            overlapMargin: margin, positionAccessor: PosAccessor);

    private static Entity SpawnOwned(ShardHost host, int netId, float x, float y, out CellSim cell)
    {
        Entity e = host.SpawnAt(x, y, out cell);
        cell.World.Set(e, new NetId(netId));
        cell.World.Set(e, new Pos { X = x, Y = y });
        return e;
    }

    [Fact]
    public void BindClient_HomeCell_IsThePlayersOwnerCell()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = ServingHost(registry);
        SpawnOwned(host, 1, 50f, 50f, out CellSim a);
        host.BindClient(slot: 0, playerNetId: 1);

        Assert.True(host.TryGetHomeCell(0, out CellSim home));
        Assert.Equal(a.Coord, home.Coord);
    }

    [Fact]
    public void HomeCell_ServesAcrossBorderGhosts_InInterest()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = ServingHost(registry);
        SpawnOwned(host, 1, 85f, 50f, out _);            // player P in A=(0,0), near east border
        SpawnOwned(host, 2, 110f, 50f, out _);           // companion Q owned by B=(1,0), near the border
        host.SyncGhosts();                               // A now holds Q as a ghost (within overlap)
        host.BindClient(0, 1);

        byte[] snap = host.SnapshotForClient(0, interestRadius: 30f);

        // The client applies it: it sees P (own) and Q (across-border ghost) from its single home cell.
        var clientWorld = new World();
        var view = new ClientReplicationView(registry);
        view.Apply(clientWorld, snap);
        Assert.True(view.TryGetEntity(1, out _));         // the player
        Assert.True(view.TryGetEntity(2, out _));         // across-border entity, served via the home cell's ghost
    }

    [Fact]
    public void PlayerCrossing_RebindsHome_AndSurroundingsAreContinuous()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = ServingHost(registry);
        Entity p = SpawnOwned(host, 1, 85f, 50f, out CellSim a);
        SpawnOwned(host, 2, 110f, 50f, out _);            // companion Q, owned by B
        host.SyncGhosts();
        host.BindClient(0, 1);

        var clientWorld = new World();
        var view = new ClientReplicationView(registry);

        // Serve before the crossing: home is A; Q is in interest (as a ghost in A).
        view.Apply(clientWorld, host.SnapshotForClient(0, 30f));
        Assert.True(host.TryGetHomeCell(0, out CellSim homeBefore));
        Assert.Equal(new CellCoord(0, 0), homeBefore.Coord);
        Assert.True(view.TryGetEntity(2, out Entity qBefore));

        // Player crosses A -> B.
        a.World.Set(p, new Pos { X = 110f, Y = 50f });
        host.ProcessHandoffs();                           // authority P: A -> B
        host.SyncGhosts();

        // Serve after: home re-binds to B; Q is still in interest (now owned by B) - continuous.
        view.Apply(clientWorld, host.SnapshotForClient(0, 30f));
        Assert.True(host.TryGetHomeCell(0, out CellSim homeAfter));
        Assert.Equal(new CellCoord(1, 0), homeAfter.Coord);          // re-bound to B
        Assert.True(view.TryGetEntity(2, out Entity qAfter));         // never disappeared
        Assert.Equal(qBefore, qAfter);                               // same client entity: no despawn/respawn
        Assert.True(view.TryGetEntity(1, out _));                    // the player too
    }

    [Fact]
    public void SnapshotForClient_InterestRadiusAboveOverlapMargin_Throws()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = ServingHost(registry, margin: 30f);
        SpawnOwned(host, 1, 50f, 50f, out _);
        host.BindClient(0, 1);

        Assert.Throws<InvalidOperationException>(() => host.SnapshotForClient(0, interestRadius: 40f));
    }

    [Fact]
    public void SnapshotForClient_UnboundSlot_Throws()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = ServingHost(registry);
        Assert.Throws<InvalidOperationException>(() => host.SnapshotForClient(99, 10f));
    }

    [Fact]
    public void UnbindClient_RemovesBinding()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = ServingHost(registry);
        SpawnOwned(host, 1, 50f, 50f, out _);
        host.BindClient(0, 1);
        Assert.True(host.IsClientBound(0));

        Assert.True(host.UnbindClient(0));
        Assert.False(host.IsClientBound(0));
        Assert.False(host.TryGetHomeCell(0, out _));
    }
}
