using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileEntityInteractTests
{
    [Fact]
    public void A_negative_authored_object_id_remains_in_the_object_domain()
    {
        TileCollisionMap map = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        var objects = new FixedTarget(-7, new TileRect(10, 10, 1, 1), plane: 0);
        var entities = new FixedTarget(7, new TileRect(30, 30, 1, 1), plane: 0);
        var simulator = new TileMoveSimulator(map, new TileStepTicks(walk: 4, run: 2), objects,
            combatTargets: entities);
        TileMoveState state = TileMoveState.At(new TileCoord(5, 10, 0), TileDirection.W);

        state = simulator.Step(state, TileCommand.InteractObject(-7, TileMoveMode.Walk), 0.25f);
        for (int i = 0; i < 40 && !state.Route.IsIdle; i++)
            state = simulator.Step(state, TileCommand.Continue(TileMoveMode.Walk), 0.25f);

        Assert.Equal(-7, state.InteractTarget);
        Assert.Equal(TileDirection.E, state.Facing);
        Assert.True(state.Route.IsIdle);
    }

    [Fact]
    public void Colliding_object_and_actor_ids_route_to_distinct_callbacks()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        for (int i = 0; i < 6; i++) doc.AddObject("tree", 40 + i, 40, 0, 0);
        TileObject booth = doc.AddObject("bank_booth", 28, 20, 0, 0);
        Assert.Equal(7, booth.Id);

        using var h = new TileCombatHarness(doc, new TileCoord(20, 20, 0), predictObjectInteractions: true);
        h.Frames(8);
        long actor = 0;
        for (int i = 0; i < 6; i++)
            actor = h.Server.SpawnActor(new TileCoord(20, 28 + i, 0),
                new TileActorSpawn(10, AttackTicks: 4, TileDirection.S));
        Assert.Equal(booth.Id, actor);
        h.Frames(8);

        var objects = new List<long>();
        var entities = new List<long>();
        h.Server.OnInteract += (_, _, target) => objects.Add(target);
        h.Server.OnInteractEntity += (_, _, target) => entities.Add(target);

        h.Client.Queue(TileCommand.InteractObject(booth.Id, TileMoveMode.Run));
        for (int i = 0; i < 200 && objects.Count == 0; i++) h.Frames(1);
        Assert.Equal(new[] { booth.Id }, objects);
        Assert.Empty(entities);

        h.Server.SetPlayerState(0, TileMoveState.At(new TileCoord(20, 20, 0), TileDirection.N));
        h.Frames(8);
        h.Client.Queue(TileCommand.InteractEntity(actor, TileMoveMode.Run));
        for (int i = 0; i < 200 && entities.Count == 0; i++) h.Frames(1);

        Assert.Equal(new[] { booth.Id }, objects);
        Assert.Equal(new[] { actor }, entities);
    }

    [Fact]
    public void A_client_refuses_an_entity_interaction_it_cannot_resolve()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new TileCombatHarness(doc, new TileCoord(20, 20, 0));
        h.Frames(8);
        var entities = new List<long>();
        h.Server.OnInteractEntity += (_, _, target) => entities.Add(target);
        int before = h.Client.DroppedClickCount;

        h.Client.Queue(TileCommand.InteractEntity(999_999, TileMoveMode.Run));
        h.Frames(8);

        Assert.Equal(before + 1, h.Client.DroppedClickCount);
        Assert.Empty(entities);
    }

    [Fact]
    public void A_valid_entity_interaction_replaces_a_live_combat_lock_without_a_cannot_reach_notice()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new TileCombatHarness(doc, new TileCoord(20, 20, 0));
        h.Frames(8);
        long oldTarget = h.Server.SpawnActor(new TileCoord(21, 20, 0),
            new TileActorSpawn(10, AttackTicks: 4, TileDirection.W));
        long interactionTarget = h.Server.SpawnActor(new TileCoord(20, 21, 0),
            new TileActorSpawn(10, AttackTicks: 4, TileDirection.S));
        h.Frames(8);
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState fighting));
        fighting.CombatTarget = oldTarget;
        h.Server.SetPlayerState(0, fighting);
        h.Frames(8);

        var interactions = new List<long>();
        var refused = new List<long>();
        int clientNotices = 0;
        h.Server.OnInteractEntity += (_, _, target) => interactions.Add(target);
        h.Server.OnCannotReach += (_, target) => refused.Add(target);
        h.Client.CannotReach += () => clientNotices++;

        h.Client.Queue(TileCommand.InteractEntity(interactionTarget, TileMoveMode.Run));
        for (int i = 0; i < 40 && interactions.Count == 0; i++) h.Frames(1);
        h.Frames(2);

        Assert.Equal(new[] { interactionTarget }, interactions);
        Assert.Empty(refused);
        Assert.Equal(0, clientNotices);
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState interacted));
        Assert.Equal(0, interacted.CombatTarget);
    }

    [Fact]
    public void The_server_refuses_zero_missing_and_cross_plane_entity_targets_before_the_action_queue()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(20, 20, 0));
        server.SpawnPlayer(0, "a", "Ari");
        long upstairs = server.SpawnActor(new TileCoord(20, 20, 1),
            new TileActorSpawn(10, AttackTicks: 4, TileDirection.S));
        var entities = new List<long>();
        var refused = new List<long>();
        server.OnInteractEntity += (_, _, target) => entities.Add(target);
        server.OnCannotReach += (_, target) => refused.Add(target);

        server.Enqueue(0, 0, TileCommand.InteractEntity(0, TileMoveMode.Run));
        server.Tick(TileCombatHarness.Tick);
        server.Enqueue(0, 1, TileCommand.InteractEntity(999_999, TileMoveMode.Run));
        server.Tick(TileCombatHarness.Tick);
        server.Enqueue(0, 2, TileCommand.InteractEntity(upstairs, TileMoveMode.Run));
        server.Tick(TileCombatHarness.Tick);

        Assert.Empty(entities);
        Assert.Empty(refused);
        Assert.Equal(0, server.Actions.PendingCount);
        Assert.True(server.TryGetPlayerState(0, out TileMoveState state));
        Assert.Equal(0, state.InteractTarget);
        Assert.Equal(TileMoveMode.Run, state.Mode);
    }

    [Fact]
    public void Entity_interaction_domain_survives_a_cell_crossing_and_cancellation_normalizes_it()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(60, 10, 0));
        long player = server.SpawnPlayer(0, "a", "Ari");
        long actor = server.SpawnActor(new TileCoord(70, 10, 0),
            new TileActorSpawn(10, AttackTicks: 4, TileDirection.W));
        var entities = new List<long>();
        server.OnInteractEntity += (_, _, target) => entities.Add(target);

        server.Enqueue(0, 0, TileCommand.InteractEntity(actor, TileMoveMode.Run));
        server.Tick(TileCombatHarness.Tick);
        Assert.True(server.TryGetPlayerState(0, out TileMoveState approaching));
        Assert.Equal(actor, approaching.InteractTarget);
        Assert.Equal(TileInteractionDomain.Entity, approaching.InteractDomain);
        Assert.True(server.CancelPendingAction(0));
        Assert.True(server.TryGetPlayerState(0, out TileMoveState cancelled));
        Assert.Equal(0, cancelled.InteractTarget);
        Assert.Equal(TileInteractionDomain.AuthoredObject, cancelled.InteractDomain);

        server.Enqueue(0, 1, TileCommand.InteractEntity(actor, TileMoveMode.Run));
        for (int i = 0; i < 40 && entities.Count == 0; i++) server.Tick(TileCombatHarness.Tick);

        Assert.Equal(new[] { actor }, entities);
        Assert.True(server.Host.TryGetOwner(player, out KhaozEngine.Sharding.CellSim owner, out _));
        Assert.Equal(new KhaozEngine.Sharding.CellCoord(1, 0), owner.Coord);
        Assert.True(server.TryGetPlayerState(0, out TileMoveState arrived));
        Assert.Equal(0, arrived.InteractTarget);
        Assert.Equal(TileInteractionDomain.AuthoredObject, arrived.InteractDomain);
    }

    [Fact]
    public void Entity_interaction_domain_survives_continue_ticks_and_a_route_rebuild()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var entities = new FixedTarget(7, new TileRect(10, 10, 1, 1), plane: 0);
        var simulator = new TileMoveSimulator(map, new TileStepTicks(walk: 4, run: 2),
            combatTargets: entities);
        TileMoveState state = TileMoveState.At(new TileCoord(5, 10, 0), TileDirection.E);
        state = simulator.Step(state, TileCommand.InteractEntity(7, TileMoveMode.Run), 0.25f);
        Assert.Equal(new TileCoord(6, 10, 0), state.Tile);

        // Block the next planned tile after the first step has committed. The landing tick must rebuild the route
        // around it without losing which resolver owns target 7.
        doc.AddObject("tree", 7, 10, 0, 0);
        TileCollisionBaker.Rebake(map, doc, TileMoveSimulatorTests.Catalogs, new TileRect(6, 9, 3, 3), 0);
        for (int i = 0; i < 40 && !state.Route.IsIdle; i++)
        {
            state = simulator.Step(state, TileCommand.Continue(TileMoveMode.Run), 0.25f);
            if (state.InteractTarget != 0)
                Assert.Equal(TileInteractionDomain.Entity, state.InteractDomain);
        }

        Assert.Equal(7, state.InteractTarget);
        Assert.Equal(TileInteractionDomain.Entity, state.InteractDomain);
        Assert.True(TileReach.Contains(map, new TileRect(10, 10, 1, 1), 0, state.Tile));
    }

    sealed class FixedTarget(long id, TileRect footprint, int plane) : ITileTargets
    {
        public bool TryGetFootprint(long target, out TileRect found, out int foundPlane)
        {
            found = footprint;
            foundPlane = plane;
            return target == id;
        }
    }
}
