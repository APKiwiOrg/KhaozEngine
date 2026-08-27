using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileActorHostTests
{
    const float Dt = 0.25f;

    static readonly TileActorDefinition Rat = new()
    {
        Id = "rat",
        MaxHealth = 30,
        AttackTicks = 10,
        WanderRadius = 4,
        LeashRadius = 10,
        RespawnDelayTicks = 6,
    };

    // matchedOptions gives the actor simulator the PLAYER's options, which is how the byte-identical test isolates
    // "an actor runs the same stepper" from "an actor is tuned differently". Every other test leaves the default.
    static TileWorldServer Server(TileWorldDocument doc, INetTransport transport, TileCoord spawn,
        bool matchedOptions = false)
    {
        TileWorldServerConfig config = TileWorldServerTickTests.Config(spawn);
        if (matchedOptions) config = config with { ActorMove = new TileMoveOptions() };
        return new TileWorldServer(transport, config, TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
    }

    static TileWorldDocument TwoRegions() =>
        TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));

    [Fact]
    public void A_spawner_builds_its_actor_on_the_first_tick_at_its_home_tile()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(20, 20, 0));
        Assert.Equal(TileActorSpawnerState.Empty, spawner.State);

        s.Tick(Dt);

        Assert.Equal(TileActorSpawnerState.Alive, spawner.State);
        Assert.True(spawner.ActorNetId > 0);
        Assert.Equal(1, s.ActorCount);
        Assert.True(s.TryGetActorState(spawner.ActorNetId, out TileMoveState st));
        Assert.Equal(new TileCoord(20, 20, 0), st.Tile);
    }

    // Spawner order is the order they were ADDED, never a dictionary enumeration, because spawn order decides net id
    // assignment and net id decides the combat roll order. A hash layout must never reach a decision.
    [Fact]
    public void Spawners_fire_in_the_order_they_were_added()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        TileActorSpawner a = s.Actors.Add(Rat, new TileCoord(20, 20, 0));
        TileActorSpawner b = s.Actors.Add(Rat, new TileCoord(30, 20, 0));
        TileActorSpawner c = s.Actors.Add(Rat, new TileCoord(40, 20, 0));

        s.Tick(Dt);

        Assert.True(a.ActorNetId < b.ActorNetId);
        Assert.True(b.ActorNetId < c.ActorNetId);
        Assert.Equal(new[] { a.ActorNetId, b.ActorNetId, c.ActorNetId }, s.ActorNetIds);
        Assert.Equal(new[] { a, b, c }, s.Actors.Spawners);
    }

    // The spawner asks the WORLD whether its actor is still there rather than listening for a death, so anything that
    // removes the entity (a kill, a despawn, an eviction) starts the respawn with no second wiring.
    [Fact]
    public void A_spawner_whose_actor_is_gone_waits_exactly_the_delay_and_respawns_a_new_net_id_at_full_health()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(20, 20, 0));
        s.Tick(Dt);
        long first = spawner.ActorNetId;

        Assert.True(s.DespawnActor(first));
        s.Tick(Dt);                                   // notices it is gone, arms Waiting(6)
        Assert.Equal(TileActorSpawnerState.Waiting, spawner.State);
        Assert.Equal(6, spawner.TicksUntilRespawn);
        for (int i = 0; i < 5; i++) s.Tick(Dt);       // 5 of the 6
        Assert.Equal(TileActorSpawnerState.Waiting, spawner.State);
        Assert.Equal(0, s.ActorCount);

        s.Tick(Dt);                                   // the sixth
        Assert.Equal(TileActorSpawnerState.Alive, spawner.State);
        Assert.NotEqual(first, spawner.ActorNetId);
        Assert.True(s.Host.TryGetOwner(spawner.ActorNetId, out CellSim cell, out Entity e));
        Assert.True(cell.World.TryGet(e, out TileHealth hp));
        Assert.Equal(30, hp.Current);
        Assert.Equal(new TileCoord(20, 20, 0), s.TryGetActorState(spawner.ActorNetId, out TileMoveState st)
            ? st.Tile : default);
    }

    // THE regression test for section 5.3. Neither the tag nor the command is on a replication channel, so a Migrate
    // capture drops both and the actor falls out of TileMovementSystem's three-component query. Step 1b writes both
    // back unconditionally every tick, which is the same immunity step 1 gives every player.
    [Fact]
    public void The_actor_host_rewrites_the_tag_and_the_command_every_tick()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(20, 20, 0));
        s.Tick(Dt);

        Assert.True(s.Host.TryGetOwner(spawner.ActorNetId, out CellSim cell, out Entity e));
        cell.World.Remove<TileActor>(e);
        cell.World.Remove<PendingTileCommand>(e);
        Assert.False(cell.World.Has<TileActor>(e));

        s.Tick(Dt);

        Assert.True(s.Host.TryGetOwner(spawner.ActorNetId, out cell, out e));
        Assert.True(cell.World.Has<TileActor>(e));
        Assert.True(cell.World.Has<PendingTileCommand>(e));
    }

    // The handoff trap itself, end to end: an actor walks out of region (0,0) into region (1,0) and keeps walking to
    // a goal on the far side. Before step 1b existed this stopped dead on the crossing tick.
    [Fact]
    public void An_actor_that_walks_over_a_region_boundary_keeps_walking()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TwoRegions(), hub.Server, new TileCoord(5, 5, 0));
        long netId = s.SpawnActor(new TileCoord(60, 10, 0), new TileActorSpawn(30, 10, TileDirection.S));
        s.Actors.Command(netId, TileCommand.WalkTo(new TileCoord(70, 10, 0), TileMoveMode.Run));

        for (int i = 0; i < 40; i++) s.Tick(Dt);

        Assert.True(s.TryGetActorState(netId, out TileMoveState st));
        Assert.Equal(new TileCoord(70, 10, 0), st.Tile);
        Assert.True(s.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
        Assert.Equal(new CellCoord(1, 0), cell.Coord);
        Assert.True(cell.World.Has<TileActor>(e));
    }

    // The test that would go red if actors ever grew a movement rule of their own. Options matched on purpose (see
    // the harness), so what is being pinned is the STEPPER rather than the tuning.
    [Fact]
    public void An_actor_and_a_player_walking_the_same_route_produce_identical_state_sequences()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0),
            matchedOptions: true);
        s.SpawnPlayer(0, "a", "Ari");
        long actor = s.SpawnActor(new TileCoord(10, 10, 0), new TileActorSpawn(30, 10, TileDirection.S));

        s.Enqueue(0, seq: 0, TileCommand.WalkTo(new TileCoord(18, 14, 0), TileMoveMode.Run));
        s.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(18, 14, 0), TileMoveMode.Run));

        for (int i = 0; i < 30; i++)
        {
            s.Tick(Dt);
            Assert.True(s.TryGetPlayerState(0, out TileMoveState p));
            Assert.True(s.TryGetActorState(actor, out TileMoveState a));
            Assert.Equal(p, a);
        }
        Assert.True(s.TryGetActorState(actor, out TileMoveState end));
        Assert.Equal(new TileCoord(18, 14, 0), end.Tile);
    }

    // The two simulators are actually two, and the OBSERVABLE is the path radius: a goal 40 tiles away is inside the
    // player's default window of 64 and outside the actor's 12, where the pathfinder's nearest-reachable fallback
    // walks toward it instead of to it. That is the 26-fold scratch saving section 5.4 sizes the cap against.
    [Fact]
    public void An_actor_paths_at_the_actor_radius_and_a_player_at_the_player_radius()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        long actor = s.SpawnActor(new TileCoord(20, 10, 0), new TileActorSpawn(30, 10, TileDirection.S));

        s.Enqueue(0, seq: 0, TileCommand.WalkTo(new TileCoord(10, 50, 0), TileMoveMode.Run));
        s.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(20, 50, 0), TileMoveMode.Run));

        for (int i = 0; i < 120; i++) s.Tick(Dt);

        Assert.True(s.TryGetPlayerState(0, out TileMoveState p));
        Assert.Equal(new TileCoord(10, 50, 0), p.Tile);
        Assert.True(s.TryGetActorState(actor, out TileMoveState a));
        Assert.True(a.Tile.Z > 10, "the actor walked toward the goal");
        Assert.True(a.Tile.Z < 50, "the actor could not path the whole way at the actor radius");
    }

    // A latched command is LATEST WINS and spent ONCE, exactly as TileWorldClient.Queue is. The tick after it is
    // spent falls back to Continue at the mode the step left the actor in, never TileCommand.None, because None is a
    // run toggled off and would quietly drop a running actor to a walk.
    [Fact]
    public void A_latched_command_is_spent_once_and_the_mode_carries_on_after_it()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        long actor = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(30, 10, TileDirection.S));

        s.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(20, 24, 0), TileMoveMode.Run));
        s.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(24, 20, 0), TileMoveMode.Run));   // replaces it
        for (int i = 0; i < 12; i++) s.Tick(Dt);

        Assert.True(s.TryGetActorState(actor, out TileMoveState st));
        Assert.Equal(new TileCoord(24, 20, 0), st.Tile);
        Assert.Equal(TileMoveMode.Run, st.Mode);
        Assert.True(st.Route.IsIdle);
        // SPENT once, observably: a latch that survived its own application would still be counted here, which
        // is exactly what a TryGetValue in place of the Remove produces, and what this line turns red.
        Assert.Equal(0, s.Actors.PendingCommandCount);
    }

    // The definition's cadence is LIVE FROM THE FIRST TICK, which is what the latch TrySpawn leaves behind buys. A
    // spawn writes TileMoveState.At, whose mode is Walk, and the fallback command is Continue at whatever mode the
    // state already holds, so without the latch a running actor would walk until something else commanded it and a
    // definition's StepMode would be a field nothing ever read.
    [Fact]
    public void A_spawned_actor_steps_at_its_definitions_mode_from_the_first_tick()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat with { StepMode = TileMoveMode.Run }, new TileCoord(20, 20, 0));

        s.Tick(Dt);

        Assert.True(s.TryGetActorState(spawner.ActorNetId, out TileMoveState st));
        Assert.Equal(TileMoveMode.Run, st.Mode);
        // Spent by the very tick that spawned it, exactly as any other latch is: the spawner pass runs ahead of the
        // actor pass inside one Tick, so the fresh latch is consumed rather than left waiting for the next one.
        Assert.Equal(0, s.Actors.PendingCommandCount);
    }

    [Fact]
    public void Despawning_an_actor_prunes_its_unspent_latch()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        long actor = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(30, 10, TileDirection.S));

        // Latched and never ticked, the shape combat's deaths make routine: the actor dies at step 4b with a
        // command already latched for a next tick it will not see. Net ids are never recycled, so without the
        // despawn prune this entry would live for the rest of the server.
        s.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(20, 24, 0), TileMoveMode.Run));
        Assert.Equal(1, s.Actors.PendingCommandCount);
        Assert.True(s.DespawnActor(actor));
        Assert.Equal(0, s.Actors.PendingCommandCount);
    }
}
