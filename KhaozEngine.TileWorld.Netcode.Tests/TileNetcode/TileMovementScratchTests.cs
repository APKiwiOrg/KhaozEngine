using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileMovementScratchTests
{
    sealed class Targets : ITileTargets
    {
        public TileCoord Tile { get; set; }

        public bool TryGetFootprint(long target, out TileRect footprint, out int plane)
        {
            footprint = new TileRect(Tile.X, Tile.Z, 1, 1);
            plane = Tile.Plane;
            return target == 1L;
        }
    }

    [Fact]
    public void Scratch_backed_steps_match_the_allocating_simulator()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var targets = new Targets { Tile = new TileCoord(18, 18, 0) };
        var simulator = new TileMoveSimulator(map, TileMoveSimulatorTests.Ticks, targets,
            new TileMoveOptions { MaxPathRadius = 64 }, targets);
        var scratch = new TilePathfinderScratch(simulator.MaxPathRadius);
        TileMoveState allocating = TileMoveState.At(new TileCoord(5, 5, 0), TileDirection.S);
        TileMoveState reused = allocating;
        TileCommand[] commands =
        {
            TileCommand.Interact(1L, TileMoveMode.Run),
            TileCommand.Continue(TileMoveMode.Run),
            TileCommand.WalkTo(new TileCoord(22, 6, 0), TileMoveMode.Run),
            TileCommand.Continue(TileMoveMode.Run),
            TileCommand.Attack(1L, TileMoveMode.Run),
            TileCommand.Continue(TileMoveMode.Run),
        };

        for (int i = 0; i < commands.Length; i++)
        {
            allocating = simulator.Step(allocating, commands[i], TileMoveSimulatorTests.Dt, self: 9L);
            reused = simulator.Step(reused, commands[i], TileMoveSimulatorTests.Dt, self: 9L, scratch);
            Assert.Equal(allocating, reused);
        }
    }
}

/// <summary>Allocation checks are isolated from the assembly's parallel workers.</summary>
[Collection("AllocSensitive")]
public class TileMovementScratchAllocationTests
{
    const int ActorCount = 6;

    [Fact]
    public void A_cell_reuses_pathfinder_windows_across_actor_repaths()
    {
        TileCollisionMap map = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        var simulator = new TileMoveSimulator(map, TileMoveSimulatorTests.Ticks,
            options: new TileMoveOptions { MaxPathRadius = 64 });
        var movement = new TileMovementSystem(simulator, simulator);
        var world = new World();
        var actors = new List<Entity>();
        for (int i = 0; i < ActorCount; i++) actors.Add(SpawnActor(world, i));

        // Warm the system, pathfinder queue and ECS query before measuring the same work again.
        movement.Update(world, TileMoveSimulatorTests.Dt);
        Rearm(world, actors);

        long before = GC.GetAllocatedBytesForCurrentThread();
        movement.Update(world, TileMoveSimulatorTests.Dt);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Six fresh radius 64 window pairs alone cost about 500 KB. Route materialization remains allowed.
        Assert.True(allocated < 160_000,
            $"six actor repaths allocated {allocated} bytes, which includes fresh pathfinder windows");
    }

    static Entity SpawnActor(World world, int row)
    {
        Entity e = world.Spawn();
        world.Set(e, new TileActor());
        SetActorState(world, e, row);
        return e;
    }

    static void Rearm(World world, IReadOnlyList<Entity> actors)
    {
        for (int i = 0; i < actors.Count; i++) SetActorState(world, actors[i], i);
    }

    static void SetActorState(World world, Entity e, int row)
    {
        var at = new TileCoord(5, 5 + row, 0);
        world.Set(e, TileMoveState.At(at, TileDirection.S));
        world.Set(e, new TileRouteState { Remaining = Array.Empty<TileDirection>() });
        world.Set(e, new PendingTileCommand
        {
            Command = TileCommand.WalkTo(new TileCoord(45, 5 + row, 0), TileMoveMode.Run),
        });
    }
}
