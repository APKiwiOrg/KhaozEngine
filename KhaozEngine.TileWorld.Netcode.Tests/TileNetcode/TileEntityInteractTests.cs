using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileEntityInteractTests
{
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
}
