using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>Actors as NxN bodies on the server: the spawn writes the size, placement and <c>CanPlace</c> check the
/// whole footprint, the wander only picks goals the whole body can stand on, the leash measures anchor to home
/// anchor, and a region handoff keeps the size. Every body is anchored on its south-west tile and z counts north.
/// Wall rotation: 0 W, 1 N, 2 E, 3 S edge of the placed tile, mirrored onto the neighbour.</summary>
public class TileFootprintActorTests
{
    const float Dt = 0.25f;

    static readonly TileActorTraversalProfile Water = new(7);

    static readonly TileActorDefinition Body = new()
    {
        Id = "body",
        MaxHealth = 30,
        WanderRadius = 0,
        LeashRadius = 10,
        RespawnDelayTicks = 6,
    };

    static TileActorSpawn Spawn(int size) => new(30, 10, TileDirection.S) { FootprintSize = size };

    // Every goal the wrapped behaviour walks to, in the order it chose them.
    sealed class GoalRecorder(ITileActorBehaviour inner) : ITileActorBehaviour
    {
        public readonly List<TileCoord> Goals = new();

        public TileActorIntent Decide(in TileActorContext context)
        {
            TileActorIntent intent = inner.Decide(context);
            if (intent.Kind == TileActorIntentKind.WalkTo) Goals.Add(intent.Tile);
            return intent;
        }
    }

    static bool TryAdd(TileWorldServer s, TileActorDefinition definition, TileCoord home)
    {
        try
        {
            s.Actors.Add(definition, home);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_spawn_writes_its_size_onto_the_move_state(int n)
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(TileMoveSimulatorTests.FlatWorld(), hub.Server,
            new TileCoord(5, 5, 0));
        long direct = s.SpawnActor(new TileCoord(20, 20, 0), Spawn(n));
        TileActorSpawner spawner = s.Actors.Add(Body with { FootprintSize = n }, new TileCoord(40, 20, 0));

        s.Tick(Dt);

        Assert.True(s.TryGetActorState(direct, out TileMoveState built));
        Assert.Equal(n, built.FootprintSize);
        Assert.Equal(new TileRect(20, 20, n, n), built.Footprint);
        Assert.Equal(TileActorSpawnerState.Alive, spawner.State);
        Assert.True(s.TryGetActorState(spawner.ActorNetId, out TileMoveState spawned));
        Assert.Equal(n, spawned.FootprintSize);
        Assert.Equal(new TileRect(40, 20, n, n), spawned.Footprint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void The_spawner_door_refuses_a_size_outside_one_to_eight(int n)
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(TileMoveSimulatorTests.FlatWorld(), hub.Server,
            new TileCoord(5, 5, 0));
        var home = new TileCoord(20, 20, 0);
        TileActorDefinition wrong = Body with { FootprintSize = n };

        ArgumentOutOfRangeException added = Assert.Throws<ArgumentOutOfRangeException>(() => s.Actors.Add(wrong, home));
        Assert.Equal("definition", added.ParamName);
        ArgumentOutOfRangeException spawned = Assert.Throws<ArgumentOutOfRangeException>(() =>
            s.SpawnActor(home, Spawn(n)));
        Assert.Equal("spec", spawned.ParamName);
        Assert.False(s.Actors.CanPlace(wrong, home));
        Assert.Empty(s.Actors.Spawners);
        Assert.Equal(0, s.ActorCount);

        // Both ends of the admitted range pass the same doors.
        s.Actors.Add(Body with { FootprintSize = 8 }, home);
        Assert.True(s.SpawnActor(new TileCoord(40, 40, 0), Spawn(TileMoveState.MaxFootprintSize)) > 0);
        Assert.True(s.SpawnActor(new TileCoord(50, 10, 0), Spawn(1)) > 0);
    }

    [Fact]
    public void Placement_refuses_a_footprint_that_does_not_fit()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("tree", 21, 21, 0, 0);                       // the north-east tile of a 2x2 on (20, 20)
        doc.AddObject("wall", 30, 20, 0, 2);                       // between (30, 20) and (31, 20)
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(5, 5, 0));
        TileActorDefinition large = Body with { FootprintSize = 2 };
        var homes = new[]
        {
            new TileCoord(20, 20, 0),
            new TileCoord(30, 20, 0),
            // Its east column is x 64, a region the map never loaded.
            new TileCoord(TileRegion.Size - 1, 20, 0),
        };

        foreach (TileCoord home in homes)
        {
            Assert.Throws<ArgumentException>(() => s.Actors.Add(large, home));
            Assert.False(s.Actors.CanPlace(large, home), $"CanPlace admitted {home}");
            Assert.Throws<ArgumentException>(() => s.SpawnActor(home, Spawn(2)));
        }
        Assert.Empty(s.Actors.Spawners);
        Assert.Equal(0, s.ActorCount);

        // The legacy rule, one tile on the default profile only: a blocked home is still admitted and still spawns.
        var blocked = new TileCoord(21, 21, 0);
        Assert.True(s.Actors.CanPlace(Body, blocked));
        TileActorSpawner legacy = s.Actors.Add(Body, blocked);
        Assert.True(s.SpawnActor(blocked, Spawn(1)) > 0);
        s.Tick(Dt);
        Assert.Equal(TileActorSpawnerState.Alive, legacy.State);
        Assert.Equal(2, s.ActorCount);
    }

    [Fact]
    public void CanPlace_agrees_with_Add()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("tree", 20, 20, 0, 0);
        TileCollisionMap ground = TileMoveSimulatorTests.Bake(doc);
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(5, 5, 0));
        s.Actors.RegisterTraversalProfile(Water, TileMoveSimulatorTests.Bake(doc));
        var unregistered = new TileActorTraversalProfile(9);

        int admitted = 0;
        int refused = 0;
        foreach (TileActorTraversalProfile profile in new[] { TileActorTraversalProfile.Default, Water, unregistered })
            for (int n = 1; n <= 3; n++)
                for (int z = 17; z <= 21; z++)
                    for (int x = 17; x <= 21; x++)
                    {
                        TileActorDefinition definition = Body with { FootprintSize = n, TraversalProfile = profile };
                        var home = new TileCoord(x, z, 0);
                        bool can = s.Actors.CanPlace(definition, home);
                        Assert.Equal(TryAdd(s, definition, home), can);

                        // One tile on the default profile keeps the legacy rule, everything else is the whole body.
                        bool expected = profile != unregistered
                            && ((profile == TileActorTraversalProfile.Default && n == 1)
                                || TileCollision.CanStand(ground, x, z, 0, n));
                        Assert.True(expected == can, $"profile {profile.Value}, size {n} at {home}: CanPlace {can}");
                        if (can) admitted++;
                        else refused++;
                    }

        Assert.True(admitted > 0 && refused > 0, $"admitted {admitted}, refused {refused}");
    }

    // The committed tile is always standable because the stepper refuses a step onto anything else, so what pins
    // the wander rather than the stepper is the GOAL: an anchor-only check admits a goal whose other tiles are
    // trees, and the pathfinder's nearest-reachable fallback then parks the body against the obstruction.
    [Fact]
    public void Wander_goals_always_fit_the_whole_footprint()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        var home = new TileCoord(30, 30, 0);
        for (int z = 20; z <= 41; z++)
            for (int x = 20; x <= 41; x++)
            {
                bool onHome = x >= home.X && x < home.X + 2 && z >= home.Z && z < home.Z + 2;
                if (!onHome && ((x * 92821) ^ (z * 68917)) % 6 == 0) doc.AddObject("tree", x, z, 0, 0);
            }
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var recorder = new GoalRecorder(TileWanderBehaviour.CreateWithTiming(meanPauseTicks: 4));
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWanderBehaviourTests.Server(doc, hub.Server, new TileCoord(5, 5, 0), seed: 3,
            behaviour: recorder);
        TileActorSpawner spawner = s.Actors.Add(Body with { FootprintSize = 2, WanderRadius = 4 }, home);

        var visited = new HashSet<TileCoord>();
        for (int i = 0; i < 400; i++)
        {
            s.Tick(Dt);
            Assert.True(s.TryGetActorState(spawner.ActorNetId, out TileMoveState st));
            Assert.Equal(2, st.FootprintSize);
            Assert.True(TileCollision.CanStand(map, st.Tile.X, st.Tile.Z, st.Tile.Plane, 2),
                $"tick {i}: the body stands on {st.Tile}, which it does not fit");
            visited.Add(st.Tile);
        }

        Assert.True(visited.Count > 1, "the actor never moved");
        Assert.NotEmpty(recorder.Goals);
        foreach (TileCoord goal in recorder.Goals)
            Assert.True(TileCollision.CanStand(map, goal.X, goal.Z, goal.Plane, 2),
                $"the wander chose {goal}, which the whole body cannot stand on");
    }

    // Walked WEST, which is the direction that tells the three candidate measures apart: the body's centre is one
    // tile nearer home than its anchor and its east column two tiles nearer, so either of those would break later.
    [Fact]
    public void The_leash_measures_anchor_to_home_anchor()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWanderBehaviourTests.Server(TileMoveSimulatorTests.FlatWorld(), hub.Server,
            new TileCoord(5, 5, 0));
        var home = new TileCoord(40, 30, 0);
        TileActorDefinition large = Body with { FootprintSize = 3, LeashRadius = 6 };
        TileActorSpawner spawner = s.Actors.Add(large, home);
        s.Tick(Dt);
        long actor = spawner.ActorNetId;
        Assert.True(s.TryGetActorState(actor, out TileMoveState born));
        Assert.Equal(3, born.FootprintSize);

        // One latch, then hands off: the behaviour keeps the walk going until the leash fires.
        s.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(home.X - 10, home.Z, 0), TileMoveMode.Walk));
        int brokeAt = -1;
        for (int i = 0; i < 80 && brokeAt < 0; i++)
        {
            Assert.True(s.TryGetActorState(actor, out TileMoveState before));
            s.Tick(Dt);
            int distance = TileWanderBehaviourTests.Chebyshev(before.Tile, home);
            Assert.True((distance > large.LeashRadius) == spawner.Returning,
                $"anchor {distance} tiles from home, Returning {spawner.Returning}");
            if (spawner.Returning) brokeAt = distance;
        }

        Assert.Equal(large.LeashRadius + 1, brokeAt);
        Assert.True(s.TryGetActorState(actor, out TileMoveState broken));
        Assert.Equal(home, broken.Route.End);
    }

    // A real cell handoff through the server, not a codec call: the body walks from cell (0, 0) into cell (1, 0),
    // and the destination rebuilds it from the Migrate capture.
    [Fact]
    public void A_region_handoff_keeps_the_size()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(5, 5, 0));
        long body = s.SpawnActor(new TileCoord(60, 10, 0), Spawn(2));
        s.Tick(Dt);
        Assert.True(s.Host.TryGetOwner(body, out CellSim source, out _));
        Assert.Equal(new CellCoord(0, 0), source.Coord);

        var destination = new TileCoord(70, 10, 0);
        s.Actors.Command(body, TileCommand.WalkTo(destination, TileMoveMode.Run));
        for (int i = 0; i < 40; i++) s.Tick(Dt);

        Assert.True(s.Host.TryGetOwner(body, out CellSim owner, out Entity entity));
        Assert.Equal(new CellCoord(1, 0), owner.Coord);
        Assert.True(owner.TryGetOwned(body, out _));
        Assert.True(owner.World.TryGet(entity, out TileMoveState raw));
        Assert.Equal(2, raw.FootprintSize);
        Assert.True(s.TryGetActorState(body, out TileMoveState arrived));
        Assert.Equal(destination, arrived.Tile);
        Assert.True(arrived.Route.IsIdle);
        Assert.Equal(2, arrived.FootprintSize);

        // The entity target space answers the whole 2x2. Against a 1x1 answer the follow stops on (70, 11), which
        // is inside the body, so an attacker standing in range of the whole footprint is the observable.
        var rect = new TileRect(destination.X, destination.Z, 2, 2);
        long attacker = s.SpawnActor(new TileCoord(70, 20, 0), Spawn(1));
        s.Actors.Command(attacker, TileCommand.Attack(body, TileMoveMode.Run));
        for (int i = 0; i < 40; i++) s.Tick(Dt);

        Assert.True(s.TryGetActorState(attacker, out TileMoveState chaser));
        Assert.Equal(body, chaser.CombatTarget);
        Assert.True(chaser.Route.IsIdle);
        Assert.False(chaser.IsStepping);
        Assert.True(TileReach.Contains(map, rect, 0, chaser.Tile, 1), $"the attacker stopped on {chaser.Tile}");
        Assert.True(chaser.Footprint.Intersect(rect).IsEmpty);
    }
}
