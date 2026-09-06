using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileActorSpawnAdmissionTests
{
    const float Dt = 0.25f;

    static readonly TileActorDefinition Rat = new()
    {
        Id = "rat",
        MaxHealth = 30,
        AttackTicks = 10,
        RespawnDelayTicks = 2,
    };

    [Fact]
    public void Spawn_admission_denies_an_initial_spawner_before_allocation_linkage_or_event()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = Server(hub.Server);
        TileActorSpawner spawner = server.Actors.Add(Rat, new TileCoord(20, 20, 0));
        var spawned = new List<long>();
        bool admit = false;
        int admissionCalls = 0;
        server.OnActorSpawned += spawned.Add;
        server.Actors.SpawnAdmission = candidate =>
        {
            admissionCalls++;
            Assert.Same(spawner, candidate);
            return admit;
        };

        server.Tick(Dt);

        Assert.Equal(TileActorSpawnerState.Empty, spawner.State);
        Assert.Equal(0, server.ActorCount);
        Assert.Empty(server.ActorNetIds);
        Assert.Empty(spawned);
        Assert.Equal(1, admissionCalls);

        admit = true;
        server.Tick(Dt);

        Assert.Equal(TileActorSpawnerState.Alive, spawner.State);
        Assert.Equal(1L, spawner.ActorNetId);
        Assert.Equal(new[] { 1L }, spawned);
        Assert.True(server.Actors.TryGetSpawnerOf(1L, out TileActorSpawner linked));
        Assert.Same(spawner, linked);
        Assert.Equal(2, admissionCalls);
    }

    [Fact]
    public void Spawn_admission_holds_a_due_respawn_at_zero_and_retries_until_admitted()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = Server(hub.Server);
        TileActorSpawner spawner = server.Actors.Add(Rat, new TileCoord(20, 20, 0));
        bool admit = true;
        int admissionCalls = 0;
        server.Actors.SpawnAdmission = _ =>
        {
            admissionCalls++;
            return admit;
        };
        server.Tick(Dt);
        long first = spawner.ActorNetId;
        Assert.True(server.DespawnActor(first));

        server.Tick(Dt);
        Assert.Equal(TileActorSpawnerState.Waiting, spawner.State);
        Assert.Equal(2, spawner.TicksUntilRespawn);
        admit = false;
        server.Tick(Dt);
        server.Tick(Dt);

        Assert.Equal(TileActorSpawnerState.Waiting, spawner.State);
        Assert.Equal(0, spawner.TicksUntilRespawn);
        Assert.Equal(0, server.ActorCount);
        Assert.Equal(2, admissionCalls);

        server.Tick(Dt);

        Assert.Equal(TileActorSpawnerState.Waiting, spawner.State);
        Assert.Equal(0, spawner.TicksUntilRespawn);
        Assert.Equal(3, admissionCalls);

        admit = true;
        server.Tick(Dt);

        Assert.Equal(TileActorSpawnerState.Alive, spawner.State);
        Assert.Equal(2L, spawner.ActorNetId);
        Assert.Equal(4, admissionCalls);
    }

    [Fact]
    public void Spawn_admission_does_not_intercept_direct_actor_spawns()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = Server(hub.Server);
        int admissionCalls = 0;
        server.Actors.SpawnAdmission = _ =>
        {
            admissionCalls++;
            return false;
        };

        long actor = server.SpawnActor(
            new TileCoord(20, 20, 0),
            new TileActorSpawn(30, 10, TileDirection.S));

        Assert.Equal(1L, actor);
        Assert.Equal(1, server.ActorCount);
        Assert.Equal(0, admissionCalls);
        Assert.False(server.Actors.TryGetSpawnerOf(actor, out _));
    }

    static TileWorldServer Server(INetTransport transport)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        return new TileWorldServer(
            transport,
            TileWorldServerTickTests.Config(new TileCoord(5, 5, 0)),
            TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs),
            new AllowAllAuthenticator());
    }
}
