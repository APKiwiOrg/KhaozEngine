using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileStaticEntityTests
{
    const float Tick = 0.25f;

    static TileWorldServer Server(INetTransport transport, TileCoord spawn)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        return new TileWorldServer(transport, TileWorldServerTickTests.Config(spawn),
            TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
    }

    [Fact]
    public void A_static_entity_has_an_idle_footprint_without_actor_combat_or_movement_state()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = Server(hub.Server, new TileCoord(10, 10, 0));
        var spec = new TileStaticEntitySpawn(TileDirection.W, FootprintSize: 2);

        long netId = server.SpawnStaticEntity(new TileCoord(12, 13, 0), spec);

        Assert.True(netId > 0);
        Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out Entity entity));
        Assert.True(cell.World.TryGet(entity, out TileMoveState state));
        Assert.Equal(new TileCoord(12, 13, 0), state.Tile);
        Assert.Equal(new TileCoord(12, 13, 0), state.StepFrom);
        Assert.Equal(TileDirection.W, state.Facing);
        Assert.Equal(2, state.FootprintSize);
        Assert.True(state.Route.IsIdle);
        Assert.False(cell.World.Has<TileActor>(entity));
        Assert.False(cell.World.Has<TileRouteState>(entity));
        Assert.False(cell.World.Has<PendingTileCommand>(entity));
        Assert.False(cell.World.Has<TileHealth>(entity));
        Assert.False(cell.World.Has<TileCombatState>(entity));
        Assert.True(cell.World.TryGet(entity, out Transient transient));
        Assert.Equal(TransientScope.Always, transient.Scope);

        for (int i = 0; i < 4; i++) server.Tick(Tick);
        Assert.True(server.Host.TryGetOwner(netId, out cell, out entity));
        Assert.True(cell.World.TryGet(entity, out TileMoveState after));
        Assert.Equal(state, after);
    }

    [Fact]
    public void Static_entity_placement_refuses_the_same_invalid_world_and_footprint_shapes_as_an_actor()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = Server(hub.Server, new TileCoord(10, 10, 0));
        var valid = new TileStaticEntitySpawn(TileDirection.S);

        Assert.Throws<ArgumentException>(() =>
            server.SpawnStaticEntity(new TileCoord(10, 10, 99), valid));
        Assert.Throws<ArgumentException>(() =>
            server.SpawnStaticEntity(new TileCoord(9_000, 9_000, 0), valid));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            server.SpawnStaticEntity(new TileCoord(10, 10, 0), valid with { FootprintSize = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            server.SpawnStaticEntity(new TileCoord(10, 10, 0),
                valid with { FootprintSize = TileMoveState.MaxFootprintSize + 1 }));
        Assert.All(server.Host.Cells, cell => Assert.Equal(0, cell.OwnedCount));
    }

    [Fact]
    public void Despawn_is_type_safe_and_idempotent()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = Server(hub.Server, new TileCoord(10, 10, 0));
        long actor = server.SpawnActor(new TileCoord(11, 10, 0),
            new TileActorSpawn(10, 4, TileDirection.S));
        long retained = server.SpawnStaticEntity(new TileCoord(12, 10, 0),
            new TileStaticEntitySpawn(TileDirection.N));

        Assert.False(server.DespawnStaticEntity(actor));
        Assert.True(server.TryGetActorState(actor, out _));
        Assert.True(server.DespawnStaticEntity(retained));
        Assert.False(server.DespawnStaticEntity(retained));
        Assert.False(server.DespawnStaticEntity(999_999));
        Assert.False(server.Host.TryGetOwner(retained, out _, out _));
    }

    [Fact]
    public void Removing_a_cell_drops_static_membership_with_its_always_transient_entity()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = Server(hub.Server, new TileCoord(10, 10, 0));
        TileCoord at = new(12, 10, 0);
        long retained = server.SpawnStaticEntity(at, new TileStaticEntitySpawn(TileDirection.N));

        Assert.True(server.Host.RemoveCell(TileCells.CoordOf(at)));

        Assert.False(server.Host.TryGetOwner(retained, out _, out _));
        Assert.False(server.DespawnStaticEntity(retained));
    }

    [Fact]
    public void InteractEntity_routes_to_a_reachable_static_footprint()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = Server(hub.Server, new TileCoord(5, 10, 0));
        server.SpawnPlayer(0, "a", "Ari");
        long retained = server.SpawnStaticEntity(new TileCoord(10, 10, 0),
            new TileStaticEntitySpawn(TileDirection.S, FootprintSize: 2));
        var interactions = new List<long>();
        server.OnInteractEntity += (_, _, target) => interactions.Add(target);

        server.Enqueue(0, 0, TileCommand.InteractEntity(retained, TileMoveMode.Run));
        for (int i = 0; i < 40 && interactions.Count == 0; i++) server.Tick(Tick);

        Assert.Equal([retained], interactions);
        Assert.True(server.TryGetPlayerState(0, out TileMoveState player));
        Assert.True(TileReach.Contains(TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()),
            new TileRect(10, 10, 2, 2), 0, player.Tile));
    }

    [Fact]
    public void A_static_entity_spawned_before_an_observer_joins_replicates_its_full_idle_state()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        var hub = new InMemoryTransportHub();
        using var server = new TileWorldServer(hub.Server,
            TileWorldServerTickTests.Config(new TileCoord(10, 10, 0)),
            TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
        long retained = server.SpawnStaticEntity(new TileCoord(12, 10, 0),
            new TileStaticEntitySpawn(TileDirection.E, FootprintSize: 2));
        using var client = new TileWorldClient(hub.CreateClient(), new TileWorldClientConfig
        {
            TickSeconds = Tick,
            StepTicks = new TileStepTicks(walk: 4, run: 2),
        }, TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()));
        client.Tick(0.13f);
        client.Poll();

        float accumulator = 0f;
        for (int i = 0; i < 16 && !client.View.TryGetEntity(retained, out _); i++)
        {
            client.Tick(0.05f);
            server.Poll();
            accumulator += 0.05f;
            while (accumulator >= Tick)
            {
                accumulator -= Tick;
                server.Tick(Tick);
            }
            client.Poll();
            client.AdvancePresentation(0.05f);
        }

        Assert.True(client.View.TryGetEntity(retained, out Entity entity));
        Assert.True(client.World.TryGet(entity, out TileMoveState state));
        Assert.Equal(new TileCoord(12, 10, 0), state.Tile);
        Assert.Equal(TileDirection.E, state.Facing);
        Assert.Equal(2, state.FootprintSize);
        Assert.True(state.Route.IsIdle);
        Assert.False(client.World.Has<TileHealth>(entity));
    }
}
