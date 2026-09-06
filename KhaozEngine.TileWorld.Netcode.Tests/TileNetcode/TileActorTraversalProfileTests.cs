using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public partial class TileActorTraversalProfileTests
{
    const float Dt = 0.25f;

    static readonly TileActorTraversalProfile Water = new(7);

    sealed class CapturingBehaviour : ITileActorBehaviour
    {
        public List<TileActorContext> Seen { get; } = new();

        public TileActorIntent Decide(in TileActorContext context)
        {
            Seen.Add(context);
            return TileActorIntent.Idle;
        }
    }

    static TileWorldServer Server(INetTransport transport, TileCollisionMap map)
    {
        TileWorldServerConfig config = TileWorldServerTickTests.Config(new TileCoord(5, 5, 0));
        return new TileWorldServer(transport, config, map, authenticator: new AllowAllAuthenticator());
    }

    static TileCollisionMap Topology(TileCollisionMap source, Func<int, int, int, bool> standable)
    {
        var map = new TileCollisionMap(source.PlaneCount);
        foreach (RegionCoord region in source.Regions)
        {
            map.EnsureRegion(region);
            TileRect rect = region.Rect;
            for (int plane = 0; plane < source.PlaneCount; plane++)
                for (int z = rect.Z; z < rect.Z1; z++)
                    for (int x = rect.X; x < rect.X1; x++)
                    {
                        TileCollisionFlags flags = source.Get(x, z, plane);
                        flags = standable(x, z, plane)
                            ? flags & ~TileCollisionFlags.Blocked
                            : flags | TileCollisionFlags.Blocked;
                        map.Or(x, z, plane, flags);
                    }
        }
        return map;
    }

    [Fact]
    public void Existing_definition_spawn_and_deconstruction_shapes_default_to_the_ground_profile()
    {
        var definition = new TileActorDefinition { Id = "rat", MaxHealth = 5 };
        var spawn = new TileActorSpawn(5, 10, TileDirection.S);
        (ushort health, byte attackTicks, TileDirection facing, TileMoveMode mode) = spawn;

        Assert.Equal(TileActorTraversalProfile.Default, definition.TraversalProfile);
        Assert.Equal(TileActorTraversalProfile.Default, spawn.TraversalProfile);
        Assert.Equal((5, 10, TileDirection.S, TileMoveMode.Walk), (health, attackTicks, facing, mode));
    }

    [Fact]
    public void A_non_default_profile_registers_once_before_the_first_tick()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        using TileWorldServer server = Server(hub.Server, ground);
        var alternate = new TileCollisionMap(ground.PlaneCount);
        alternate.EnsureRegion(new RegionCoord(0, 0));

        server.Actors.RegisterTraversalProfile(Water, alternate);

        Assert.Throws<ArgumentException>(() => server.Actors.RegisterTraversalProfile(Water, alternate));
        Assert.Throws<ArgumentException>(() =>
            server.Actors.RegisterTraversalProfile(TileActorTraversalProfile.Default, alternate));

        server.Tick(Dt);

        Assert.Throws<InvalidOperationException>(() =>
            server.Actors.RegisterTraversalProfile(new TileActorTraversalProfile(8), alternate));
    }

    [Fact]
    public void Registration_refuses_a_map_with_another_plane_count()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        using TileWorldServer server = Server(hub.Server, ground);

        Assert.Throws<ArgumentException>(() =>
            server.Actors.RegisterTraversalProfile(Water, new TileCollisionMap(ground.PlaneCount + 1)));
    }

    [Fact]
    public void A_spawn_with_an_unregistered_profile_is_refused_before_it_creates_an_actor()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        using TileWorldServer server = Server(hub.Server, ground);
        var spawn = new TileActorSpawn(5, 10, TileDirection.S) { TraversalProfile = Water };

        Assert.Throws<ArgumentException>(() => server.SpawnActor(new TileCoord(10, 10, 0), spawn));
        Assert.Equal(0, server.ActorCount);
    }

    [Fact]
    public void A_spawner_with_an_unregistered_profile_is_refused_when_it_is_added()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        using TileWorldServer server = Server(hub.Server, ground);
        var definition = new TileActorDefinition
        {
            Id = "unknown",
            MaxHealth = 5,
            TraversalProfile = Water,
        };

        Assert.Throws<ArgumentException>(() => server.Actors.Add(definition, new TileCoord(10, 10, 0)));
        Assert.Empty(server.Actors.Spawners);
    }

    [Fact]
    public void A_custom_profile_refuses_a_blocked_spawn_while_default_keeps_the_legacy_rule()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        var home = new TileCoord(10, 10, 0);
        TileCollisionMap water = Topology(ground, (_, _, _) => false);
        using TileWorldServer server = Server(hub.Server, ground);
        server.Actors.RegisterTraversalProfile(Water, water);

        long defaultActor = server.SpawnActor(home, new TileActorSpawn(5, 10, TileDirection.S));
        var custom = new TileActorSpawn(5, 10, TileDirection.S) { TraversalProfile = Water };

        Assert.True(defaultActor > 0);
        Assert.Throws<ArgumentException>(() => server.SpawnActor(home, custom));
        Assert.Equal(1, server.ActorCount);
    }

    [Fact]
    public void Ground_and_custom_actors_follow_their_own_topologies_in_the_same_tick_stream()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        ground.Or(21, 10, 0, TileCollisionFlags.Blocked);
        ground.Or(22, 10, 0, TileCollisionFlags.Blocked);
        TileCollisionMap water = Topology(ground,
            (x, z, plane) => plane == 0 && z == 10 && x >= 20 && x <= 22);
        using TileWorldServer server = Server(hub.Server, ground);
        server.Actors.RegisterTraversalProfile(Water, water);
        long walker = server.SpawnActor(new TileCoord(10, 10, 0),
            new TileActorSpawn(5, 10, TileDirection.S));
        long swimmer = server.SpawnActor(new TileCoord(20, 10, 0),
            new TileActorSpawn(5, 10, TileDirection.S) { TraversalProfile = Water });

        server.Actors.Command(walker, TileCommand.WalkTo(new TileCoord(12, 10, 0), TileMoveMode.Run));
        server.Actors.Command(swimmer, TileCommand.WalkTo(new TileCoord(22, 10, 0), TileMoveMode.Run));
        for (int i = 0; i < 12; i++) server.Tick(Dt);

        Assert.True(server.TryGetActorState(walker, out TileMoveState walkerState));
        Assert.True(server.TryGetActorState(swimmer, out TileMoveState swimmerState));
        Assert.Equal(new TileCoord(12, 10, 0), walkerState.Tile);
        Assert.Equal(new TileCoord(22, 10, 0), swimmerState.Tile);
    }

    [Fact]
    public void A_behaviour_receives_each_actors_profile_and_registered_map()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        TileCollisionMap water = Topology(ground, (x, z, plane) => plane == 0 && z == 20 && x >= 18 && x <= 22);
        using TileWorldServer server = Server(hub.Server, ground);
        server.Actors.RegisterTraversalProfile(Water, water);
        var behaviour = new CapturingBehaviour();
        server.Actors.Behaviour = behaviour;
        long walker = server.SpawnActor(new TileCoord(10, 10, 0),
            new TileActorSpawn(5, 10, TileDirection.S));
        long swimmer = server.SpawnActor(new TileCoord(20, 20, 0),
            new TileActorSpawn(5, 10, TileDirection.S) { TraversalProfile = Water });

        server.Tick(Dt);

        TileActorContext walkerContext = Assert.Single(behaviour.Seen, c => c.NetId == walker);
        TileActorContext swimmerContext = Assert.Single(behaviour.Seen, c => c.NetId == swimmer);
        Assert.Equal(TileActorTraversalProfile.Default, walkerContext.TraversalProfile);
        Assert.Same(ground, walkerContext.TraversalMap);
        Assert.Equal(Water, swimmerContext.TraversalProfile);
        Assert.Same(water, swimmerContext.TraversalMap);
    }

    [Fact]
    public void The_default_wander_behaviour_keeps_a_custom_actor_on_its_own_topology()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        TileCollisionMap water = Topology(ground, (x, z, plane) => plane == 0 && z == 20 && x >= 18 && x <= 22);
        using TileWorldServer server = Server(hub.Server, ground);
        server.Actors.RegisterTraversalProfile(Water, water);
        server.Actors.Behaviour = TileWanderBehaviour.CreateWithTiming(meanPauseTicks: 1);
        var definition = new TileActorDefinition
        {
            Id = "swimmer",
            MaxHealth = 5,
            WanderRadius = 2,
            LeashRadius = 5,
            TraversalProfile = Water,
        };
        TileActorSpawner spawner = server.Actors.Add(definition, new TileCoord(20, 20, 0));
        var visited = new HashSet<TileCoord>();

        for (int i = 0; i < 200; i++)
        {
            server.Tick(Dt);
            if (server.TryGetActorState(spawner.ActorNetId, out TileMoveState state)) visited.Add(state.Tile);
        }

        Assert.Contains(visited, tile => tile.X != 20);
        Assert.All(visited, tile =>
        {
            Assert.Equal(20, tile.Z);
            Assert.InRange(tile.X, 18, 22);
        });
    }

    [Fact]
    public void An_unknown_profile_tag_freezes_instead_of_falling_back_to_the_ground_simulator()
    {
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        var simulator = new TileMoveSimulator(ground, TileMoveSimulatorTests.Ticks);
        var movement = new TileMovementSystem(simulator);
        var world = new World();
        Entity actor = world.Spawn();
        TileMoveState start = TileMoveState.At(new TileCoord(5, 5, 0), TileDirection.S);
        world.Set(actor, start);
        world.Set(actor, new TileRouteState { Remaining = Array.Empty<TileDirection>() });
        world.Set(actor, new PendingTileCommand
        {
            Command = TileCommand.WalkTo(new TileCoord(8, 5, 0), TileMoveMode.Run),
        });
        world.Set(actor, new TileActor { TraversalProfile = Water });

        movement.Update(world, Dt);

        Assert.True(world.TryGet(actor, out TileMoveState state));
        Assert.True(world.TryGet(actor, out TileRouteState route));
        Assert.True(world.TryGet(actor, out PendingTileCommand pending));
        Assert.Equal(start, state);
        Assert.NotNull(route.Remaining);
        Assert.Empty(route.Remaining);
        Assert.Equal(TileCommand.Continue(TileMoveMode.Walk), pending.Command);
    }
}
