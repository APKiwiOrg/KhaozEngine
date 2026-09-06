using System;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public partial class TileActorTraversalProfileTests
{
    [Fact]
    public void Registration_refuses_null_and_the_reserved_unresolved_profile()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        using TileWorldServer server = Server(hub.Server, ground);

        Assert.Throws<ArgumentNullException>(() => server.Actors.RegisterTraversalProfile(Water, null!));
        Assert.Throws<ArgumentException>(() =>
            server.Actors.RegisterTraversalProfile(TileActorTraversalProfile.Unresolved, ground));
    }

    [Fact]
    public void A_spawner_refuses_a_home_blocked_by_its_custom_profile()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        TileCollisionMap water = Topology(ground, (_, _, _) => false);
        using TileWorldServer server = Server(hub.Server, ground);
        server.Actors.RegisterTraversalProfile(Water, water);
        var definition = new TileActorDefinition
        {
            Id = "blocked",
            MaxHealth = 5,
            TraversalProfile = Water,
        };

        Assert.Throws<ArgumentException>(() => server.Actors.Add(definition, new TileCoord(10, 10, 0)));
        Assert.Empty(server.Actors.Spawners);
    }

    [Fact]
    public void A_leash_break_returns_and_restores_health_over_the_custom_profile()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        for (int x = 11; x <= 14; x++) ground.Or(x, 10, 0, TileCollisionFlags.Blocked);
        TileCollisionMap water = Topology(ground,
            (x, z, plane) => plane == 0 && z == 10 && x >= 10 && x <= 14);
        using TileWorldServer server = Server(hub.Server, ground);
        server.Actors.RegisterTraversalProfile(Water, water);
        server.Actors.Behaviour = new TileWanderBehaviour();
        var definition = new TileActorDefinition
        {
            Id = "leashed",
            MaxHealth = 5,
            WanderRadius = 0,
            LeashRadius = 1,
            TraversalProfile = Water,
        };
        var home = new TileCoord(10, 10, 0);
        TileActorSpawner spawner = server.Actors.Add(definition, home);
        server.Tick(Dt);
        long actor = spawner.ActorNetId;
        Assert.True(server.SetHealth(actor, new TileHealth { Current = 2, Max = 5 }));
        server.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(14, 10, 0), TileMoveMode.Run));
        int furthest = 0;

        for (int i = 0; i < 80; i++)
        {
            server.Tick(Dt);
            Assert.True(server.TryGetActorState(actor, out TileMoveState state));
            furthest = Math.Max(furthest, state.Tile.X - home.X);
        }

        Assert.True(furthest > definition.LeashRadius);
        Assert.True(server.TryGetActorState(actor, out TileMoveState returned));
        Assert.Equal(home, returned.Tile);
        Assert.True(returned.Route.IsIdle);
        Assert.True(server.TryGetHealth(actor, out TileHealth health));
        Assert.Equal(health.Max, health.Current);
    }

    [Fact]
    public void A_respawn_waits_while_its_custom_home_is_blocked_and_retries_when_it_opens()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        var home = new TileCoord(10, 10, 0);
        TileCollisionMap water = Topology(ground,
            (x, z, plane) => plane == 0 && x == home.X && z == home.Z);
        using TileWorldServer server = Server(hub.Server, ground);
        server.Actors.RegisterTraversalProfile(Water, water);
        var definition = new TileActorDefinition
        {
            Id = "respawn",
            MaxHealth = 5,
            WanderRadius = 0,
            RespawnDelayTicks = 2,
            TraversalProfile = Water,
        };
        TileActorSpawner spawner = server.Actors.Add(definition, home);
        server.Tick(Dt);
        long first = spawner.ActorNetId;
        Assert.True(server.DespawnActor(first));
        water.Or(home.X, home.Z, home.Plane, TileCollisionFlags.Blocked);

        for (int i = 0; i < 5; i++) server.Tick(Dt);

        Assert.Equal(TileActorSpawnerState.Waiting, spawner.State);
        Assert.Equal(0, spawner.TicksUntilRespawn);
        Assert.Equal(0, server.ActorCount);
        water.Clear(new TileRect(home.X, home.Z, 1, 1), home.Plane);

        server.Tick(Dt);

        Assert.Equal(TileActorSpawnerState.Alive, spawner.State);
        Assert.NotEqual(first, spawner.ActorNetId);
        Assert.True(server.Host.TryGetOwner(spawner.ActorNetId, out CellSim cell, out Entity entity));
        Assert.True(cell.World.TryGet(entity, out TileActor tag));
        Assert.Equal(Water, tag.TraversalProfile);
    }
}
