using System.Collections.Generic;
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
    public void A_custom_route_repaths_around_a_new_blocker_without_abandoning_the_committed_step()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        for (int z = 10; z <= 11; z++)
            for (int x = 11; x <= 14; x++) ground.Or(x, z, 0, TileCollisionFlags.Blocked);
        TileCollisionMap water = Topology(ground,
            (x, z, plane) => plane == 0 && x >= 10 && x <= 14 && z >= 10 && z <= 11);
        using TileWorldServer server = Server(hub.Server, ground);
        server.Actors.RegisterTraversalProfile(Water, water);
        long actor = server.SpawnActor(new TileCoord(10, 10, 0),
            new TileActorSpawn(5, 10, TileDirection.E) { TraversalProfile = Water });
        server.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(14, 10, 0), TileMoveMode.Walk));

        server.Tick(Dt);

        Assert.True(server.TryGetActorState(actor, out TileMoveState committed));
        Assert.Equal(new TileCoord(11, 10, 0), committed.Tile);
        Assert.Equal(new TileCoord(10, 10, 0), committed.StepFrom);
        Assert.True(committed.IsStepping);
        water.Or(12, 10, 0, TileCollisionFlags.Blocked);
        var visited = new HashSet<TileCoord> { committed.Tile };

        server.Tick(Dt);
        Assert.True(server.TryGetActorState(actor, out TileMoveState stillCommitted));
        Assert.Equal(committed.Tile, stillCommitted.Tile);
        for (int i = 0; i < 30; i++)
        {
            server.Tick(Dt);
            Assert.True(server.TryGetActorState(actor, out TileMoveState state));
            visited.Add(state.Tile);
        }

        Assert.DoesNotContain(new TileCoord(12, 10, 0), visited);
        Assert.Contains(visited, tile => tile.Z == 11);
        Assert.True(server.TryGetActorState(actor, out TileMoveState arrived));
        Assert.Equal(new TileCoord(14, 10, 0), arrived.Tile);
    }

    [Fact]
    public void A_custom_profile_honours_directional_walls_and_solid_footprint_flags()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        TileCollisionMap water = Topology(ground, (x, z, plane) =>
            plane == 0 && z == 10 && ((x >= 10 && x <= 11) || (x >= 20 && x <= 21)));
        water.Or(10, 10, 0, TileCollisionFlags.WallE);
        water.Or(11, 10, 0, TileCollisionFlags.WallW);
        water.Or(21, 10, 0, TileCollisionFlags.Blocked);
        using TileWorldServer server = Server(hub.Server, ground);
        server.Actors.RegisterTraversalProfile(Water, water);
        long walled = server.SpawnActor(new TileCoord(10, 10, 0),
            new TileActorSpawn(5, 10, TileDirection.E) { TraversalProfile = Water });
        long solid = server.SpawnActor(new TileCoord(20, 10, 0),
            new TileActorSpawn(5, 10, TileDirection.E) { TraversalProfile = Water });

        server.Actors.Command(walled, TileCommand.WalkTo(new TileCoord(11, 10, 0), TileMoveMode.Run));
        server.Actors.Command(solid, TileCommand.WalkTo(new TileCoord(21, 10, 0), TileMoveMode.Run));
        for (int i = 0; i < 12; i++) server.Tick(Dt);

        Assert.True(server.TryGetActorState(walled, out TileMoveState walledState));
        Assert.True(server.TryGetActorState(solid, out TileMoveState solidState));
        Assert.Equal(new TileCoord(10, 10, 0), walledState.Tile);
        Assert.Equal(new TileCoord(20, 10, 0), solidState.Tile);
    }

    [Fact]
    public void A_custom_actor_chases_over_its_profile_when_the_ground_map_blocks_the_route()
    {
        var hub = new InMemoryTransportHub();
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        for (int x = 11; x <= 13; x++) ground.Or(x, 10, 0, TileCollisionFlags.Blocked);
        TileCollisionMap water = Topology(ground,
            (x, z, plane) => plane == 0 && z == 10 && x >= 10 && x <= 14);
        using TileWorldServer server = Server(hub.Server, ground);
        server.Actors.RegisterTraversalProfile(Water, water);
        long chaser = server.SpawnActor(new TileCoord(10, 10, 0),
            new TileActorSpawn(5, 10, TileDirection.E) { TraversalProfile = Water });
        long target = server.SpawnActor(new TileCoord(14, 10, 0),
            new TileActorSpawn(5, 10, TileDirection.W));

        server.Actors.Command(chaser, TileCommand.Attack(target, TileMoveMode.Run));
        for (int i = 0; i < 30; i++) server.Tick(Dt);

        Assert.True(server.TryGetActorState(chaser, out TileMoveState state));
        Assert.Equal(new TileCoord(13, 10, 0), state.Tile);
        Assert.Equal(target, state.CombatTarget);
    }

    [Fact]
    public void An_actor_attackers_adjacent_reach_uses_its_profile_map()
    {
        Assert.True(Swings(groundWall: true, profileWall: false));
        Assert.False(Swings(groundWall: false, profileWall: true));

        static bool Swings(bool groundWall, bool profileWall)
        {
            var hub = new InMemoryTransportHub();
            TileCollisionMap ground = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
            TileCollisionMap water = Topology(ground,
                (x, z, plane) => plane == 0 && x == 20 && (z == 20 || z == 21));
            if (groundWall) AddNorthWall(ground);
            if (profileWall) AddNorthWall(water);
            using TileWorldServer server = Server(hub.Server, ground);
            server.Actors.RegisterTraversalProfile(Water, water);
            var rules = new TileCombatResolveTests.FixedRules();
            server.CombatRules = rules;
            long attacker = server.SpawnActor(new TileCoord(20, 20, 0),
                new TileActorSpawn(5, 1, TileDirection.N) { TraversalProfile = Water });
            long target = server.SpawnActor(new TileCoord(20, 21, 0),
                new TileActorSpawn(5, 1, TileDirection.S));
            server.Actors.Command(attacker, TileCommand.Attack(target, TileMoveMode.Walk));

            server.Tick(Dt);

            return rules.Rolls.Count == 1;
        }

        static void AddNorthWall(TileCollisionMap map)
        {
            map.Or(20, 20, 0, TileCollisionFlags.WallN);
            map.Or(20, 21, 0, TileCollisionFlags.WallS);
        }
    }

    [Fact]
    public void A_custom_actor_keeps_its_profile_and_route_across_a_region_handoff()
    {
        var hub = new InMemoryTransportHub();
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(doc);
        for (int x = 61; x <= 70; x++) ground.Or(x, 10, 0, TileCollisionFlags.Blocked);
        TileCollisionMap water = Topology(ground,
            (x, z, plane) => plane == 0 && z == 10 && x >= 60 && x <= 70);
        using TileWorldServer server = Server(hub.Server, ground);
        server.Actors.RegisterTraversalProfile(Water, water);
        long actor = server.SpawnActor(new TileCoord(60, 10, 0),
            new TileActorSpawn(5, 10, TileDirection.E) { TraversalProfile = Water });
        server.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(70, 10, 0), TileMoveMode.Run));

        for (int i = 0; i < 50; i++) server.Tick(Dt);

        Assert.True(server.TryGetActorState(actor, out TileMoveState state));
        Assert.Equal(new TileCoord(70, 10, 0), state.Tile);
        Assert.True(server.Host.TryGetOwner(actor, out CellSim cell, out Entity entity));
        Assert.Equal(new CellCoord(1, 0), cell.Coord);
        Assert.True(cell.World.TryGet(entity, out TileActor tag));
        Assert.Equal(Water, tag.TraversalProfile);
    }
}
