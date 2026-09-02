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

    // The same contract on a SPAWNED actor, which is the only case a definition's StepMode is in play on, and with
    // no behaviour wired at all, which is the default configuration. A per-tick write of the definition's cadence
    // overwrote a latched mode at the next step boundary, so a head that latched a run got one tick of it.
    [Fact]
    public void A_latched_commands_mode_outranks_the_spawners_cadence_until_something_replaces_it()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(20, 20, 0));
        Assert.Null(s.Actors.Behaviour);
        s.Tick(Dt);
        long actor = spawner.ActorNetId;
        Assert.True(s.TryGetActorState(actor, out TileMoveState spawned));
        Assert.Equal(TileMoveMode.Walk, spawned.Mode);

        s.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(20, 26, 0), TileMoveMode.Run));
        s.Tick(Dt);
        Assert.True(s.TryGetActorState(actor, out TileMoveState onTheLatch));
        Assert.Equal(TileMoveMode.Run, onTheLatch.Mode);

        // The tick AFTER the latch was spent, and four more. The fallback is Continue at the mode the step left the
        // actor in, so a head wanting one run does not have to re-latch it on every tick to keep it.
        s.Tick(Dt);
        Assert.True(s.TryGetActorState(actor, out TileMoveState next));
        Assert.Equal(TileMoveMode.Run, next.Mode);
        for (int i = 0; i < 4; i++) s.Tick(Dt);
        Assert.True(s.TryGetActorState(actor, out TileMoveState later));
        Assert.Equal(TileMoveMode.Run, later.Mode);
    }

    // The definition's cadence is LIVE FROM THE FIRST TICK, which is what stamping it onto the spawned STATE buys.
    // TileMoveState.At writes Walk, so without that stamp a running actor would walk until something else commanded
    // it and a definition's StepMode would be a field nothing ever read. It costs no latch either, which the last
    // line pins: a latch on the spawn tick would outrank the behaviour on the one tick its actor was born.
    [Fact]
    public void A_spawned_actor_steps_at_its_definitions_mode_from_the_first_tick()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat with { StepMode = TileMoveMode.Run }, new TileCoord(20, 20, 0));

        s.Tick(Dt);

        Assert.True(s.TryGetActorState(spawner.ActorNetId, out TileMoveState st));
        Assert.Equal(TileMoveMode.Run, st.Mode);
        // And it costs no latch to do it: a spawn leaves nothing waiting, so the cadence cannot be one tick of a
        // command that something else then replaces.
        Assert.Equal(0, s.Actors.PendingCommandCount);
    }

    // OnActorSpawned documents itself as the place a game attaches its own components, and the natural way to know
    // WHICH kind to attach is the spawner's definition. The link has to be in place BEFORE the event, or the
    // handler the doc points at reads false and a game's component silently never lands.
    [Fact]
    public void A_spawner_built_actor_is_linked_to_its_spawner_before_OnActorSpawned_fires()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(20, 20, 0));

        string? seenDefinition = null;
        bool seenLink = false;
        TileActorSpawnerState seenState = TileActorSpawnerState.Empty;
        long seenActorNetId = -1L;
        s.OnActorSpawned += netId =>
        {
            seenLink = s.Actors.TryGetSpawnerOf(netId, out TileActorSpawner from);
            if (seenLink) seenDefinition = from.Definition.Id;
            seenState = spawner.State;
            seenActorNetId = spawner.ActorNetId;
        };

        s.Tick(Dt);

        Assert.True(seenLink);
        Assert.Equal("rat", seenDefinition);
        // The spawner's own state is settled by then too, so a handler reading it is not looking at the tick
        // before. Both halves of the link move together or neither is trustworthy from here.
        Assert.Equal(TileActorSpawnerState.Alive, seenState);
        Assert.Equal(spawner.ActorNetId, seenActorNetId);
    }

    // An actor a head built itself still raises the event and still has no spawner, which is the answer that says
    // the link was moved rather than made mandatory.
    [Fact]
    public void A_directly_spawned_actor_still_raises_the_event_and_holds_no_spawner()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        var raised = new List<long>();
        bool linked = true;
        s.OnActorSpawned += netId =>
        {
            raised.Add(netId);
            linked = s.Actors.TryGetSpawnerOf(netId, out _);
        };

        long actor = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(30, 10, TileDirection.S));

        Assert.Equal(new[] { actor }, raised);
        Assert.False(linked);
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

    // THE ONE-ARGUMENT CONSTRUCTOR has no caller in the tree: the server always passes two, one tuned to the leash.
    // Keeping it is right (it is still the shape a head with no actors wants, and dropping it would be a breaking
    // change for one), and nothing pinned that it still routes BOTH kinds of entity through the single simulator it
    // was handed. The observable is the route CAP, which is the one knob a second simulator built behind the
    // constructor could not accidentally agree on.
    [Fact]
    public void The_one_argument_movement_system_steps_a_player_and_an_actor_through_the_one_simulator()
    {
        var simulator = new TileMoveSimulator(TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()),
            TileStepTicks.Default, options: new TileMoveOptions { MaxRouteSteps = 3 });
        var world = new World();
        Entity player = Walker(world, new TileCoord(20, 20, 0), actor: false);
        Entity actor = Walker(world, new TileCoord(20, 24, 0), actor: true);

        new TileMovementSystem(simulator).Update(world, Dt);

        // Both routes are truncated to the SAME cap, which is this simulator's rather than a default one's.
        Assert.Equal(RouteLengthOf(world, player), RouteLengthOf(world, actor));
        Assert.Equal(2, RouteLengthOf(world, actor));
    }

    // One entity with the three components the movement pass queries, walking ten tiles east, tagged as an actor or
    // not. Built straight into a bare World, because the constructor under test is the one no server calls.
    static Entity Walker(World world, TileCoord at, bool actor)
    {
        Entity e = world.Spawn();
        world.Set(e, TileMoveState.At(at, TileDirection.S));
        world.Set(e, new TileRouteState { Remaining = Array.Empty<TileDirection>() });
        world.Set(e, new PendingTileCommand
        {
            Command = TileCommand.WalkTo(new TileCoord(at.X + 10, at.Z, at.Plane), TileMoveMode.Walk),
        });
        if (actor) world.Set(e, new TileActor());
        return e;
    }

    static int RouteLengthOf(World world, Entity e)
    {
        Assert.True(world.TryGet(e, out TileRouteState route));
        return route.Remaining?.Length ?? -1;
    }

    // The despawn is the moment the actor stops existing, so every index keyed on its net id has to answer for that
    // at once. The spawner link used to survive until the spawner's own next tick noticed the actor was gone, which
    // left TryGetSpawnerOf answering true for an id nothing else in the server referenced. Harmless while net ids
    // are never recycled, and an index that lies for a window either way.
    [Fact]
    public void Despawning_a_spawner_built_actor_drops_its_spawner_link_at_once()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(20, 20, 0));
        s.Tick(Dt);
        long actor = spawner.ActorNetId;
        Assert.True(actor > 0);
        Assert.True(s.Actors.TryGetSpawnerOf(actor, out _));

        Assert.True(s.DespawnActor(actor));

        // Immediately, rather than on the tick the spawner notices. The spawner itself is untouched here: noticing
        // the loss and starting the respawn countdown stays its own tick's job.
        Assert.False(s.Actors.TryGetSpawnerOf(actor, out _));
        Assert.Equal(TileActorSpawnerState.Alive, spawner.State);
    }
}
