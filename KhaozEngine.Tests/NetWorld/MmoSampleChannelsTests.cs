using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using MmoServerSample;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Locks in the <see cref="MmoServerSample"/> demonstration of per-registration channels: the NPC's hidden
/// <see cref="AggroCounter"/> (Persist|Migrate, never replicated) and the player's <see cref="PrivateStats"/>
/// (Default|OwnerOnly). Verifies the shapes through the reference server's real registry.
/// </summary>
public class MmoSampleChannelsTests
{
    private static (World client, ClientReplicationView view) Apply(ReplicationRegistry r, byte[] snap)
    {
        var client = new World();
        var view = new ClientReplicationView(r);
        view.Apply(client, snap);
        return (client, view);
    }

    private static Entity SpawnNpc(World w, int netId, int aggro)
    {
        Entity e = w.Spawn();
        w.Set(e, new NetId(netId));
        w.Set(e, new Position { X = 1f, Y = 2f });
        w.Set(e, new Creature { Kind = 3 });
        w.Set(e, new AggroCounter { Value = aggro });
        return e;
    }

    [Fact]
    public void Aggro_IsPersistedAndMigrated_ButNeverReplicated()
    {
        ReplicationRegistry r = MmoProtocol.CreateRegistry();
        var w = new World();
        SpawnNpc(w, 50, aggro: 7);
        var ids = new HashSet<long> { 50 };

        // Replicate: the client sees the Creature kind (public) but NEVER the aggro counter.
        (World rc, ClientReplicationView rv) = Apply(r, SnapshotWriter.WriteFiltered(w, r, ids, ReplicationChannels.Replicate, null));
        Assert.True(rv.TryGetEntity(50, out Entity re));
        Assert.True(rc.Has<Creature>(re));
        Assert.False(rc.Has<AggroCounter>(re));

        // Persist + Migrate: the aggro IS captured for the blob and the handoff.
        (World pc, ClientReplicationView pv) = Apply(r, SnapshotWriter.WriteFiltered(w, r, ids, ReplicationChannels.Persist, null));
        Assert.True(pv.TryGetEntity(50, out Entity pe));
        Assert.Equal(7, pc.Get<AggroCounter>(pe).Value);

        (World mc, ClientReplicationView mv) = Apply(r, SnapshotWriter.WriteFiltered(w, r, ids, ReplicationChannels.Migrate, null));
        Assert.True(mv.TryGetEntity(50, out Entity me));
        Assert.Equal(7, mc.Get<AggroCounter>(me).Value);
    }

    [Fact]
    public void PrivateStats_ReachOnlyTheOwningClient()
    {
        ReplicationRegistry r = MmoProtocol.CreateRegistry();
        var w = new World();
        foreach (int id in new[] { 10, 20 })
        {
            Entity e = w.Spawn();
            w.Set(e, new NetId(id));
            w.Set(e, new Position { X = id, Y = id });
            w.Set(e, new PrivateStats { Health = id });
        }
        var aoi = new HashSet<long> { 10, 20 };

        (World c, ClientReplicationView v) = Apply(r, SnapshotWriter.WriteFiltered(w, r, aoi, ReplicationChannels.Replicate, 10));
        Assert.True(v.TryGetEntity(10, out Entity own));
        Assert.True(v.TryGetEntity(20, out Entity other));
        Assert.Equal(10, c.Get<PrivateStats>(own).Health);   // own HP visible
        Assert.False(c.Has<PrivateStats>(other));            // the other player's HP is hidden
    }
}
