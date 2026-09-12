using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileWorldServerAfterMovementTests
{
    const float Dt = 0.25f;

    [Fact]
    public void Attack_is_invisible_before_movement_and_visible_after_it()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = TileWorldServerTickTests.Server(
            TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        server.SpawnPlayer(0, "a", "Ari");
        long target = server.SpawnActor(
            new TileCoord(5, 6, 0), new TileActorSpawn(30, 4, TileDirection.S));
        long before = -1;
        long after = -1;

        server.OnBeforeTick += _ =>
        {
            Assert.True(server.TryGetPlayerState(0, out TileMoveState state));
            before = state.CombatTarget;
        };
        server.OnAfterMovement += _ =>
        {
            Assert.True(server.TryGetPlayerState(0, out TileMoveState state));
            after = state.CombatTarget;
        };

        server.Enqueue(0, 0, TileCommand.Attack(target, TileMoveMode.Run));
        server.Tick(Dt);

        Assert.Equal(0, before);
        Assert.Equal(target, after);
    }

    [Fact]
    public void WalkTo_is_visible_after_movement_on_its_admission_tick()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = TileWorldServerTickTests.Server(
            TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        server.SpawnPlayer(0, "a", "Ari");
        TileMoveState seen = default;

        server.OnAfterMovement += _ =>
        {
            Assert.True(server.TryGetPlayerState(0, out seen));
        };

        TileCoord goal = new(10, 14, 0);
        server.Enqueue(0, 0, TileCommand.WalkTo(goal, TileMoveMode.Run));
        server.Tick(Dt);

        Assert.Equal(new TileCoord(10, 11, 0), seen.Tile);
        Assert.Equal(goal, seen.Route.End);
    }

    [Fact]
    public void OnAfterMovement_runs_once_per_whole_tick()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = TileWorldServerTickTests.Server(
            TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        var seen = new List<float>();
        server.OnAfterMovement += seen.Add;

        server.Tick(Dt / 2f);
        Assert.Empty(seen);

        server.Tick(Dt / 2f);
        server.Tick(Dt * 3f);

        Assert.Equal(4, seen.Count);
        Assert.All(seen, value => Assert.Equal(Dt, value));
    }

    [Fact]
    public void OnAfterMovement_precedes_interaction_resolution()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject target = doc.AddObject("bank_booth", 10, 10, 0, 0);
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = TileWorldServerTickTests.Server(
            doc, hub.Server, new TileCoord(9, 10, 0));
        server.SpawnPlayer(0, "a", "Ari");
        var order = new List<string>();
        server.OnAfterMovement += _ => order.Add("movement");
        server.OnInteract += (_, _, _) => order.Add("interaction");

        server.Enqueue(0, 0, TileCommand.Interact(target.Id, TileMoveMode.Walk));
        server.Tick(Dt);

        Assert.Equal(new[] { "movement", "interaction" }, order);
    }

    [Fact]
    public void OnAfterMovement_precedes_combat_resolution()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = TileCombatResolveTests.Server(
            TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0),
            new TileCombatResolveTests.FixedRules());
        long attacker = server.SpawnActor(
            new TileCoord(20, 20, 0), new TileActorSpawn(30, 4, TileDirection.S));
        long target = server.SpawnActor(
            new TileCoord(20, 21, 0), new TileActorSpawn(30, 4, TileDirection.S));
        TileCombatResolveTests.Lock(server, attacker, target);
        var order = new List<string>();
        server.OnAfterMovement += _ => order.Add("movement");
        server.OnCombatEvent += _ => order.Add("combat");

        server.Tick(Dt);

        Assert.Equal(new[] { "movement", "combat" }, order);
    }

    [Fact]
    public void A_state_write_from_OnAfterMovement_reaches_that_ticks_owner_snapshot()
    {
        using var harness = new TileCombatHarness(
            TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        int calls = 0;
        harness.Server.OnAfterMovement += _ =>
        {
            calls++;
            Assert.True(harness.Server.TryGetPlayerNetId(0, out long player));
            Assert.True(harness.Server.SetHealth(
                player, new TileHealth { Current = 23, Max = 40 }));
        };

        harness.Frames(5);

        Assert.Equal(1, calls);
        Assert.Equal(0, harness.Client.ServerTick);
        Assert.True(harness.Client.View.TryGetEntity(harness.Client.LocalNetId, out var local));
        Assert.True(harness.Client.World.TryGet(local, out TileHealth health));
        Assert.Equal((ushort)23, health.Current);
        Assert.Equal((ushort)40, health.Max);
    }

    [Fact]
    public void OnAfterMovement_sees_settled_handoff_ownership_and_ghosts()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(
            4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = TileWorldServerTickTests.Server(
            doc, hub.Server, new TileCoord(63, 10, 0));
        long player = server.SpawnPlayer(0, "a", "Ari");
        CellCoord ownerSeen = default;
        TileCoord tileSeen = default;
        bool sourceGhostSeen = false;

        server.OnAfterMovement += _dt =>
        {
            Assert.True(server.Host.TryGetOwner(player, out CellSim owner, out _));
            ownerSeen = owner.Coord;
            Assert.True(server.TryGetPlayerState(0, out TileMoveState state));
            tileSeen = state.Tile;
            Assert.True(server.Host.TryGetCell(new CellCoord(0, 0), out CellSim source));
            sourceGhostSeen = source.TryGetGhost(player, out _);
        };

        server.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(68, 10, 0), TileMoveMode.Run));
        server.Tick(Dt);

        Assert.Equal(new CellCoord(1, 0), ownerSeen);
        Assert.Equal(new TileCoord(64, 10, 0), tileSeen);
        Assert.True(sourceGhostSeen);
        Assert.Equal(1, server.Host.OwnerCount(player));
    }
}
