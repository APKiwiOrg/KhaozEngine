using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileActorSpawnTests
{
    static readonly TileActorSpawn Rat = new(MaxHealth: 30, AttackTicks: 10, Facing: TileDirection.S);

    // TWO regions, because the per-cell cap has to be shown refusing one cell while a NEIGHBOURING cell still takes
    // a spawn, and a tile in a region the collision map never baked is refused at the door before the cap is ever
    // consulted. A cell is a region, so a second cell means a second baked region.
    static TileWorldServer Server(INetTransport transport, TileCoord spawn, int maxActorsPerCell = 64)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        return new TileWorldServer(transport,
            TileWorldServerTickTests.Config(spawn) with { MaxActorsPerCell = maxActorsPerCell },
            TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
    }

    // An actor is a player minus a connection, and this is that sentence as an assertion: the entity exists, a cell
    // owns it, its state is the tile it was spawned on, and the player index never heard of it.
    [Fact]
    public void A_spawned_actor_stands_on_its_tile_and_binds_no_client()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub.Server, new TileCoord(10, 10, 0));
        long netId = s.SpawnActor(new TileCoord(12, 12, 0), Rat);

        Assert.True(netId > 0);
        Assert.Equal(0, s.PlayerCount);
        Assert.Equal(1, s.ActorCount);
        Assert.Equal(new[] { netId }, s.ActorNetIds);
        Assert.False(s.Host.IsClientBound(0));
        Assert.True(s.TryGetActorState(netId, out TileMoveState st));
        Assert.Equal(new TileCoord(12, 12, 0), st.Tile);
        Assert.Equal(new TileCoord(12, 12, 0), st.StepFrom);
        Assert.Equal(TileDirection.S, st.Facing);
        Assert.True(st.Route.IsIdle);
    }

    [Fact]
    public void A_spawned_actor_carries_the_tag_full_health_the_combat_state_and_a_durable_only_transient_mark()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub.Server, new TileCoord(10, 10, 0));
        long netId = s.SpawnActor(new TileCoord(12, 12, 0), Rat);

        Assert.True(s.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
        Assert.True(cell.World.Has<TileActor>(e));
        Assert.True(cell.World.TryGet(e, out TileHealth hp));
        Assert.Equal(30, hp.Max);
        Assert.Equal(30, hp.Current);
        Assert.True(cell.World.TryGet(e, out TileCombatState combat));
        Assert.Equal(10, combat.AttackTicks);
        Assert.Equal(0, combat.CooldownRemaining);
        Assert.Equal(0L, combat.LastDamagedBy);
        Assert.True(cell.World.TryGet(e, out Transient mark));
        Assert.Equal(TransientScope.DurableOnly, mark.Scope);
        // An actor has no owner, so it never carries a name a viewer could read. A monster's name is PROSE and the
        // server owns no catalog (TileServerReason's doc states the rule the whole stack follows).
        Assert.False(cell.World.Has<TileIdentity>(e));
    }

    // Net ids are never recycled (NetIdAllocator), which removes a whole class of bug for free: nobody can be left
    // holding a target that silently re-aims at the corpse's replacement.
    [Fact]
    public void A_respawned_actor_gets_a_new_net_id_and_the_old_one_never_comes_back()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub.Server, new TileCoord(10, 10, 0));
        long first = s.SpawnActor(new TileCoord(12, 12, 0), Rat);
        Assert.True(s.DespawnActor(first));
        long second = s.SpawnActor(new TileCoord(12, 12, 0), Rat);

        Assert.NotEqual(first, second);
        Assert.Equal(1, s.ActorCount);
        Assert.False(s.TryGetActorState(first, out _));
        Assert.True(s.TryGetActorState(second, out _));
    }

    [Fact]
    public void DespawnActor_answers_false_for_an_id_it_does_not_hold()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub.Server, new TileCoord(10, 10, 0));
        long netId = s.SpawnActor(new TileCoord(12, 12, 0), Rat);

        Assert.True(s.DespawnActor(netId));
        Assert.False(s.DespawnActor(netId));
        Assert.False(s.DespawnActor(999_999L));
        Assert.Equal(0, s.ActorCount);
    }

    // The cap is a REFUSAL at the door rather than a silent drop, and it answers 0 rather than throwing because the
    // caller is a spawner inside a server tick: a throw there takes the tick down for every player on the server.
    [Fact]
    public void A_cell_already_holding_MaxActorsPerCell_refuses_the_next_spawn_with_zero()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub.Server, new TileCoord(10, 10, 0), maxActorsPerCell: 2);
        Assert.True(s.SpawnActor(new TileCoord(12, 12, 0), Rat) > 0);
        Assert.True(s.SpawnActor(new TileCoord(13, 12, 0), Rat) > 0);

        Assert.Equal(0L, s.SpawnActor(new TileCoord(14, 12, 0), Rat));
        Assert.Equal(2, s.ActorCount);
        Assert.Equal(1, s.RefusedActorSpawnCount);
        // The cap is PER CELL, so the neighbouring region still takes one. A cell is a region (TileCells).
        Assert.True(s.SpawnActor(new TileCoord(70, 12, 0), Rat) > 0);
    }

    // The same door ValidatePlayerState is: a plane the world does not have, or a region the map never loaded, would
    // otherwise leave an entity nobody can see and that can never step.
    [Fact]
    public void An_actor_on_a_plane_or_a_region_the_world_does_not_have_is_refused_at_the_door()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub.Server, new TileCoord(10, 10, 0));
        Assert.Throws<ArgumentException>(() => s.SpawnActor(new TileCoord(12, 12, 99), Rat));
        Assert.Throws<ArgumentException>(() => s.SpawnActor(new TileCoord(9_000, 9_000, 0), Rat));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => s.SpawnActor(new TileCoord(12, 12, 0), Rat with { MaxHealth = 0 }));
        Assert.Equal(0, s.ActorCount);
        // ActorCount reads actorNetIds alone, and that list is appended AFTER the entity exists, so it stays zero
        // for a refusal that moved BELOW host.SpawnOwned and left a real orphan owned by a cell: an entity nothing
        // indexes, served to every viewer around it forever. The GRID is what says nothing was built, so that is
        // what the door test asserts.
        foreach (CellSim cell in s.Host.Cells) Assert.Equal(0, cell.OwnedCount);
    }

    [Fact]
    public void OnActorSpawned_fires_once_with_the_new_net_id_after_the_entity_exists()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub.Server, new TileCoord(10, 10, 0));
        var seen = new List<long>();
        s.OnActorSpawned += id =>
        {
            seen.Add(id);
            // Raised AFTER the components are set, which is what makes it the point a game may attach its own.
            Assert.True(s.TryGetActorState(id, out _));
        };
        long netId = s.SpawnActor(new TileCoord(12, 12, 0), Rat);
        Assert.Equal(new[] { netId }, seen);
    }

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
        world.Set(e, new TileCombatState
        {
            AttackTicks = 10, CooldownRemaining = 3, LastDamagedBy = 9L, LastDamagedTick = 44L,
            LastCombatTick = 46L, TargetSeen = 5L, TargetSinceTick = 41L,
        });
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
        // The tail of the struct is the survivable asymmetry: a write kept with the read dropped leaves the
        // reader short at the END, the fields above still come back right, and the tail silently zeroes on
        // every cell handoff. LastCombatTick zeroing there is a player escaping the logout window by crossing
        // a cell boundary mid fight, so the tail is pinned field by field.
        Assert.Equal(46L, mc.LastCombatTick);
        Assert.Equal(5L, mc.TargetSeen);
        Assert.Equal(41L, mc.TargetSinceTick);
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
