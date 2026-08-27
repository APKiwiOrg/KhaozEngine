using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileActorSpawnTests
{
    // TileHealth reaches a client and TileCombatState must not, which is why the actor's per-viewer cost has no line
    // for it. Asserted end to end through the registry rather than by reading a flag, because the flag is only worth
    // anything if the writer honours it.
    [Fact]
    public void TileHealth_replicates_and_TileCombatState_rides_the_migrate_channel_only()
    {
        ReplicationRegistry registry = TileProtocol.CreateRegistry();
        var world = new World();
        Entity e = world.Spawn();
        world.Set(e, new NetId(7L));
        world.Set(e, TileMoveState.At(new TileCoord(1, 2, 0), TileDirection.N));
        world.Set(e, new TileHealth { Current = 12, Max = 30 });
        world.Set(e, new TileCombatState { AttackTicks = 10, CooldownRemaining = 3, LastDamagedBy = 9L, LastDamagedTick = 44L });
        var interest = new HashSet<long> { 7L };

        var replicated = new World();
        Entity r = OnlyEntity(replicated, registry, world, interest, ReplicationChannels.Replicate);
        Assert.True(replicated.TryGet(r, out TileHealth rhp));
        Assert.Equal(12, rhp.Current);
        Assert.Equal(30, rhp.Max);
        Assert.False(replicated.Has<TileCombatState>(r));

        var migrated = new World();
        Entity m = OnlyEntity(migrated, registry, world, interest, ReplicationChannels.Migrate);
        Assert.True(migrated.TryGet(m, out TileCombatState mc));
        Assert.Equal(10, mc.AttackTicks);
        Assert.Equal(3, mc.CooldownRemaining);
        Assert.Equal(9L, mc.LastDamagedBy);
        Assert.Equal(44L, mc.LastDamagedTick);
        Assert.True(migrated.Has<TileHealth>(m));
    }

    // Every byte of a ushort pair is meaningful, so the reader CLAMPS rather than refusing: there is no malformed
    // value here, only an inconsistent one, and a Current above Max draws a health bar past its own track.
    [Fact]
    public void A_health_frame_claiming_more_current_than_max_is_clamped_on_the_way_in()
    {
        ReplicationRegistry registry = TileProtocol.CreateRegistry();
        var world = new World();
        Entity e = world.Spawn();
        world.Set(e, new NetId(7L));
        world.Set(e, new TileHealth { Current = 900, Max = 30 });

        var back = new World();
        Entity r = OnlyEntity(back, registry, world, new HashSet<long> { 7L }, ReplicationChannels.Replicate);
        Assert.True(back.TryGet(r, out TileHealth hp));
        Assert.Equal(30, hp.Max);
        Assert.Equal(30, hp.Current);
    }

    static Entity OnlyEntity(World into, ReplicationRegistry registry, World from, HashSet<long> interest,
        ReplicationChannels channel)
    {
        var view = new ClientReplicationView(registry);
        view.Apply(into, SnapshotWriter.WriteFiltered(from, registry, interest, channel, null));
        Assert.True(view.TryGetEntity(7L, out Entity e));
        return e;
    }
}
