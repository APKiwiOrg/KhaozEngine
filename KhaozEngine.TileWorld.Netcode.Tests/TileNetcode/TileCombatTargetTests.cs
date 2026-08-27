using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileCombatTargetTests
{
    const float Dt = 0.25f;
    static readonly TileStepTicks Ticks = new(walk: 4, run: 2);

    // A fake entity space, so the follow can be driven tick by tick without a server. It answers exactly what
    // TileEntityTargets answers, a 1x1 rect at a committed tile, and the test MOVES it between steps, which is what
    // makes the dance reproducible.
    sealed class FakeTargets : ITileTargets
    {
        public readonly Dictionary<long, TileCoord> Tiles = new();

        public bool TryGetFootprint(long target, out TileRect footprint, out int plane)
        {
            footprint = default;
            plane = 0;
            if (!Tiles.TryGetValue(target, out TileCoord t)) return false;
            footprint = new TileRect(t.X, t.Z, 1, 1);
            plane = t.Plane;
            return true;
        }
    }

    static (TileMoveSimulator sim, FakeTargets targets) Sim(TileWorldDocument? doc = null)
    {
        doc ??= TileMoveSimulatorTests.FlatWorld();
        var targets = new FakeTargets();
        return (new TileMoveSimulator(TileMoveSimulatorTests.Bake(doc), Ticks, null, null, targets), targets);
    }

    [Fact]
    public void Attack_is_its_own_command_kind_and_rides_the_existing_fixed_frame()
    {
        var cmd = TileCommand.Attack(0x0001_0000_0000_002AL, TileMoveMode.Run);
        Assert.Equal(TileCommandKind.Attack, cmd.Kind);
        Assert.Equal(0x0001_0000_0000_002AL, cmd.Target);
        Assert.Equal(default, cmd.Goal);

        byte[] frame = TileProtocol.EncodeCommand(7, cmd);
        Assert.Equal(24, frame.Length);
        Assert.True(TileProtocol.TryDecodeCommand(frame, planeCount: 4, out int seq, out TileCommand back));
        Assert.Equal(7, seq);
        Assert.Equal(cmd, back);

        // The kind ceiling moved by exactly one. A kind of 4 is still a malformed frame, because an unchecked byte
        // cast into an enum reaches a switch that has no case for it.
        frame[5] = 4;
        Assert.False(TileProtocol.TryDecodeCommand(frame, planeCount: 4, out _, out _));
    }

    // CombatTarget joins equality, which is what makes a mispredicted target a RECONCILE rather than a silent
    // divergence, and it joins the wire, which is what makes it 41 payload bytes instead of 33. The delta is
    // measured against an entity carrying no move state at all, so the 44 is the framed cost section 5.4 budgets.
    [Fact]
    public void The_combat_target_joins_equality_and_the_wire()
    {
        TileMoveState a = TileMoveState.At(new TileCoord(4, 5, 0), TileDirection.N);
        TileMoveState b = a;
        b.CombatTarget = 99L;
        Assert.NotEqual(a, b);
        Assert.False(a.Equals(b));

        ReplicationRegistry registry = TileProtocol.CreateRegistry();
        var bare = new World();
        Entity e0 = bare.Spawn();
        bare.Set(e0, new NetId(7L));

        var full = new World();
        Entity e1 = full.Spawn();
        full.Set(e1, new NetId(7L));
        full.Set(e1, b);

        var interest = new HashSet<long> { 7L };
        int delta = SnapshotWriter.WriteFiltered(full, registry, interest, ReplicationChannels.Replicate, null).Length
                  - SnapshotWriter.WriteFiltered(bare, registry, interest, ReplicationChannels.Replicate, null).Length;
        // [typeId:2][len:1][payload:41]
        Assert.Equal(44, delta);

        var back = new World();
        var view = new ClientReplicationView(registry);
        view.Apply(back, SnapshotWriter.WriteFiltered(full, registry, interest, ReplicationChannels.Replicate, null));
        Assert.True(view.TryGetEntity(7L, out Entity r));
        Assert.True(back.TryGet(r, out TileMoveState decoded));
        Assert.Equal(99L, decoded.CombatTarget);
    }

    // Two records of one intent, each clearing the other, for the reason TileActionQueue gives about its own pair:
    // the one that outlives the other fires against something the player visibly walked away from. A WalkTo
    // therefore BREAKS a fight, which is how a player disengages and is the same rule OSRS uses.
    [Fact]
    public void The_combat_and_interact_targets_are_mutually_exclusive_and_a_walk_breaks_the_fight()
    {
        (TileMoveSimulator sim, FakeTargets targets) = Sim();
        targets.Tiles[42L] = new TileCoord(20, 20, 0);
        TileMoveState s = TileMoveState.At(new TileCoord(10, 10, 0), TileDirection.N);
        s.InteractTarget = 7L;

        s = sim.Step(s, TileCommand.Attack(42L, TileMoveMode.Walk), Dt);
        Assert.Equal(42L, s.CombatTarget);
        Assert.Equal(0L, s.InteractTarget);

        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(10, 14, 0), TileMoveMode.Walk), Dt);
        Assert.Equal(0L, s.CombatTarget);
    }

    // An attacker in range does not shuffle. The route is DROPPED rather than walked out, which is what stops a
    // fight from being a dance around the target.
    [Fact]
    public void An_attacker_already_in_reach_drops_its_route_and_stands()
    {
        (TileMoveSimulator sim, FakeTargets targets) = Sim();
        targets.Tiles[42L] = new TileCoord(10, 11, 0);
        TileMoveState s = TileMoveState.At(new TileCoord(10, 10, 0), TileDirection.N);

        s = sim.Step(s, TileCommand.Attack(42L, TileMoveMode.Walk), Dt);
        Assert.Equal(42L, s.CombatTarget);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(new TileCoord(10, 10, 0), s.Tile);
        Assert.False(s.IsStepping);

        for (int i = 0; i < 8; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(10, 10, 0), s.Tile);
        Assert.Equal(42L, s.CombatTarget);
    }

    // A SELF TARGET, which is the one case rule 4's reach test cannot answer on its own: a tile is never in its own
    // reach set, so the old rule 4 read "out of range", rule 5 pathed to a cardinal neighbour, the footprint moved
    // with the body, and the route end was out of reach again on the next tick. Measured on the server before the
    // fix, on this very world: a player attacking its own net id left (10,10,0) and crossed 10 distinct tiles in 30
    // seconds, ending on (1,10,0) at the map edge, one FindPath per tick the whole way. Rule 5's memo can never hit
    // for a self target, so that is exactly the section 5.4 budget the rule was written to protect, spent forever.
    //
    // The simulator cannot refuse this in Accepts. It sees a TileMoveState and a TileCommand and neither carries a
    // net id, so the only place that knows enough is rule 4, and the rule it needs there is the general one: a body
    // standing INSIDE the footprint is as close to it as it can get. The lock therefore HOLDS and nothing moves,
    // which is the same answer any other in-reach target gets. Whether a swing lands from that tile is the cooldown
    // seam's question, and that lands in task 5.
    [Fact]
    public void A_self_attack_stands_rather_than_walking_away_forever()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(
            TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        long netId = s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.Attack(netId, TileMoveMode.Run));

        var visited = new HashSet<TileCoord>();
        for (int i = 0; i < 120; i++)                       // 30 seconds at a 250 ms tick, the measured window
        {
            s.Tick(Dt);
            Assert.True(s.TryGetPlayerState(0, out TileMoveState each));
            visited.Add(each.Tile);
        }

        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Single(visited);
        Assert.Equal(new TileCoord(10, 10, 0), st.Tile);
        Assert.True(st.Route.IsIdle);
        Assert.False(st.IsStepping);
        Assert.Equal(netId, st.CombatTarget);
    }

    // The general form of the same rule, which is why the fix is a footprint test rather than a net id comparison:
    // two ENTITIES on one tile answer it too. Actors do not occupy tiles this round (section 12 defers it), so an
    // attacker sharing its target's tile is reachable content rather than a hypothetical, and the answer is the same
    // one an adjacent attacker gets: drop the route, hold the lock, stand. The facing is left alone deliberately,
    // because a tile you are standing on has no direction to face.
    [Fact]
    public void An_attacker_standing_on_a_1x1_target_holds_the_lock_and_stands()
    {
        (TileMoveSimulator sim, FakeTargets targets) = Sim();
        targets.Tiles[42L] = new TileCoord(10, 10, 0);
        TileMoveState s = TileMoveState.At(new TileCoord(10, 10, 0), TileDirection.N);

        s = sim.Step(s, TileCommand.Attack(42L, TileMoveMode.Run), Dt);
        Assert.Equal(42L, s.CombatTarget);
        Assert.True(s.Route.IsIdle);

        for (int i = 0; i < 12; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(10, 10, 0), s.Tile);
        Assert.Equal(TileDirection.N, s.Facing);
        Assert.Equal(42L, s.CombatTarget);
        Assert.True(s.Route.IsIdle);
        Assert.False(s.IsStepping);
    }

    // Rule 5: re-path only when the target's committed tile CHANGED, and the memo is the route's own END rather than
    // a new field. A stationary target therefore costs ZERO pathfinding per tick, which is what keeps section 5.4's
    // CPU budget honest.
    [Fact]
    public void An_attacker_paths_to_a_reach_tile_and_does_not_re_path_while_the_target_stands_still()
    {
        (TileMoveSimulator sim, FakeTargets targets) = Sim();
        targets.Tiles[42L] = new TileCoord(10, 20, 0);
        TileMoveState s = TileMoveState.At(new TileCoord(10, 10, 0), TileDirection.N);

        s = sim.Step(s, TileCommand.Attack(42L, TileMoveMode.Run), Dt);
        Assert.False(s.Route.IsIdle);
        TileCoord end = s.Route.End;
        Assert.Equal(new TileCoord(10, 19, 0), end);

        // Fifteen, and the number is derived rather than counted off: the route is 9 steps, the click's own tick
        // commits the first of them, and the 8 that are left take 2 ticks each at the run cadence, of which the
        // click tick already paid one. A route empties on the tick its LAST step STARTS, so 8 * 2 - 1 = 15.
        for (int i = 0; i < 15 && !s.Route.IsIdle; i++)
        {
            s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
            if (!s.Route.IsIdle) Assert.Equal(end, s.Route.End);
        }
        Assert.Equal(new TileCoord(10, 19, 0), s.Tile);
        Assert.Equal(42L, s.CombatTarget);
    }

    // The free half of death handling: the seam's contract already says an id stops resolving the moment the thing it
    // named stops existing, so nothing has to tell the follow that its target died.
    [Fact]
    public void A_target_that_stops_resolving_or_moves_to_another_plane_clears_the_lock()
    {
        (TileMoveSimulator sim, FakeTargets targets) = Sim();
        targets.Tiles[42L] = new TileCoord(10, 20, 0);
        TileMoveState s = TileMoveState.At(new TileCoord(10, 10, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.Attack(42L, TileMoveMode.Run), Dt);
        Assert.Equal(42L, s.CombatTarget);

        targets.Tiles.Remove(42L);
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(0L, s.CombatTarget);

        // A fight broken by a staircase is BROKEN rather than chased through the floor: reach never crosses planes,
        // and the rest of the package refuses cross-plane rather than coercing.
        targets.Tiles[43L] = new TileCoord(10, 12, 0);
        s = sim.Step(s, TileCommand.Attack(43L, TileMoveMode.Run), Dt);
        Assert.Equal(43L, s.CombatTarget);
        targets.Tiles[43L] = new TileCoord(10, 12, 1);
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(0L, s.CombatTarget);
    }

    // A target that cannot be reached AT ALL. The lock clears and the attacker stands: the CannotReach notice that
    // goes with it is the server's half and lands in task 5.
    [Fact]
    public void A_target_with_no_reachable_tile_clears_the_lock_and_stands()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        // Wall the target in on all four cardinals with solid greybox objects.
        doc.AddObject("tree", 20, 19, 0, 0);
        doc.AddObject("tree", 20, 21, 0, 0);
        doc.AddObject("tree", 19, 20, 0, 0);
        doc.AddObject("tree", 21, 20, 0, 0);
        (TileMoveSimulator sim, FakeTargets targets) = Sim(doc);
        targets.Tiles[42L] = new TileCoord(20, 20, 0);
        TileMoveState s = TileMoveState.At(new TileCoord(10, 10, 0), TileDirection.N);

        s = sim.Step(s, TileCommand.Attack(42L, TileMoveMode.Run), Dt);

        Assert.Equal(0L, s.CombatTarget);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(new TileCoord(10, 10, 0), s.Tile);
    }

    // SECTION 6.4's TRACED TABLE, asserted tick by tick. A on (x,0), B on (x,1) cardinally adjacent, both at walk
    // cadence, B fleeing north from tick 0. The steady state is the whole result: A's commits lock exactly ONE TICK
    // behind B's, so the pair is out of range on the tick B commits and back in range on the next one, and a
    // same-speed flee in a straight line therefore does NOT escape melee.
    //
    // The order inside each tick is the tick body's: the target space is snapshotted at the START, then both bodies
    // step. That is what the loop below reproduces by reading B's tile into the resolver BEFORE either steps.
    [Fact]
    public void The_dance_locks_A_one_tick_behind_B_and_the_miss_window_is_one_tick()
    {
        const int X = 20;
        (TileMoveSimulator sim, FakeTargets targets) = Sim();
        TileMoveState a = TileMoveState.At(new TileCoord(X, 0, 0), TileDirection.N);
        TileMoveState b = TileMoveState.At(new TileCoord(X, 1, 0), TileDirection.N);
        const long BId = 42L;

        a = StepPair(sim, targets, ref b, ref a, TileCommand.Attack(BId, TileMoveMode.Walk),
            TileCommand.WalkTo(new TileCoord(X, 8, 0), TileMoveMode.Walk));

        var inRange = new List<bool> { Reachable(sim, a, b) };
        var aTiles = new List<TileCoord> { a.Tile };
        var bTiles = new List<TileCoord> { b.Tile };
        for (int tick = 1; tick <= 8; tick++)
        {
            a = StepPair(sim, targets, ref b, ref a, TileCommand.Continue(TileMoveMode.Walk),
                TileCommand.Continue(TileMoveMode.Walk));
            inRange.Add(Reachable(sim, a, b));
            aTiles.Add(a.Tile);
            bTiles.Add(b.Tile);
        }

        // B commits on 0, 3 and 7. A commits on 1, 4 and 8, exactly one tick later each time.
        Assert.Equal(new TileCoord(X, 2, 0), bTiles[0]);
        Assert.Equal(new TileCoord(X, 3, 0), bTiles[3]);
        Assert.Equal(new TileCoord(X, 4, 0), bTiles[7]);
        Assert.Equal(new TileCoord(X, 0, 0), aTiles[0]);
        Assert.Equal(new TileCoord(X, 1, 0), aTiles[1]);
        Assert.Equal(new TileCoord(X, 2, 0), aTiles[4]);
        Assert.Equal(new TileCoord(X, 3, 0), aTiles[8]);
        // Out of range on exactly the ticks B commits, in range on every other one. Three hittable ticks out of four.
        Assert.Equal(new[] { false, true, true, false, true, true, true, false, true }, inRange);
    }

    // The same trace run DIAGONALLY gives the same one-tick window. A diagonal step costs the same tick count as a
    // cardinal one and A follows diagonally at the same rate, so turning buys a fleeing target nothing on open
    // ground. What breaks melee is GEOMETRY, not dancing.
    [Fact]
    public void The_dance_run_diagonally_gives_the_same_one_tick_miss_window()
    {
        const int X = 20;
        (TileMoveSimulator sim, FakeTargets targets) = Sim();
        TileMoveState a = TileMoveState.At(new TileCoord(X, 0, 0), TileDirection.N);
        TileMoveState b = TileMoveState.At(new TileCoord(X, 1, 0), TileDirection.N);

        a = StepPair(sim, targets, ref b, ref a, TileCommand.Attack(42L, TileMoveMode.Walk),
            TileCommand.WalkTo(new TileCoord(X + 8, 9, 0), TileMoveMode.Walk));
        var inRange = new List<bool> { Reachable(sim, a, b) };
        for (int tick = 1; tick <= 8; tick++)
        {
            a = StepPair(sim, targets, ref b, ref a, TileCommand.Continue(TileMoveMode.Walk),
                TileCommand.Continue(TileMoveMode.Walk));
            inRange.Add(Reachable(sim, a, b));
        }

        Assert.Equal(new[] { false, true, true, false, true, true, true, false, true }, inRange);
    }

    // A target FASTER than its attacker escapes outright, and nothing arbitrates that: it is the cadence. It is why
    // a monster's step cadence is content rather than an engine constant.
    [Fact]
    public void A_running_target_escapes_a_walking_attacker_outright()
    {
        const int X = 20;
        (TileMoveSimulator sim, FakeTargets targets) = Sim();
        TileMoveState a = TileMoveState.At(new TileCoord(X, 0, 0), TileDirection.N);
        TileMoveState b = TileMoveState.At(new TileCoord(X, 1, 0), TileDirection.N);

        a = StepPair(sim, targets, ref b, ref a, TileCommand.Attack(42L, TileMoveMode.Walk),
            TileCommand.WalkTo(new TileCoord(X, 40, 0), TileMoveMode.Run));
        int gapAfterFirst = b.Tile.Z - a.Tile.Z;
        for (int tick = 1; tick <= 24; tick++)
            a = StepPair(sim, targets, ref b, ref a, TileCommand.Continue(TileMoveMode.Walk),
                TileCommand.Continue(TileMoveMode.Run));

        Assert.True(b.Tile.Z - a.Tile.Z > gapAfterFirst, "the gap only grows");
        Assert.False(Reachable(sim, a, b));
    }

    // The client's resolver: a REMOTE off the honest read that R0 landed, and the local player off its own
    // prediction. The delayed TryGetRemoteTile is deliberately not consulted, because it is the truth from a moment
    // that has already passed and a rule built on it is wrong by construction.
    [Fact]
    public void TileRemoteTargets_reads_a_remote_off_the_latest_snapshot_and_the_local_player_off_prediction()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0));
        h.Frames(8);
        long actor = h.Server.SpawnActor(new TileCoord(14, 10, 0), new TileActorSpawn(30, 10, TileDirection.S));
        h.Frames(8);

        var resolver = new TileRemoteTargets(h.Client);
        Assert.True(resolver.TryGetFootprint(actor, out TileRect footprint, out int plane));
        Assert.Equal(new TileRect(14, 10, 1, 1), footprint);
        Assert.Equal(0, plane);
        Assert.True(h.Client.TryGetLatestRemoteTile(actor, out TileCoord honest));
        Assert.Equal(new TileCoord(14, 10, 0), honest);

        Assert.True(resolver.TryGetFootprint(h.Client.LocalNetId, out TileRect own, out int ownPlane));
        TileCoord predicted = h.Client.Prediction.PredictedState.Tile;
        Assert.Equal(new TileRect(predicted.X, predicted.Z, 1, 1), own);
        Assert.Equal(predicted.Plane, ownPlane);

        Assert.False(resolver.TryGetFootprint(999_999L, out _, out _));
    }

    // One tick of the pair, in the tick body's own order: the target space is snapshotted BEFORE anything steps, so
    // neither body's decision can depend on the other having already moved.
    static TileMoveState StepPair(TileMoveSimulator sim, FakeTargets targets, ref TileMoveState b,
        ref TileMoveState a, TileCommand aCommand, TileCommand bCommand)
    {
        targets.Tiles[42L] = b.Tile;
        TileMoveState steppedA = sim.Step(a, aCommand, Dt);
        b = sim.Step(b, bCommand, Dt);
        a = steppedA;
        return steppedA;
    }

    static bool Reachable(TileMoveSimulator sim, in TileMoveState a, in TileMoveState b) =>
        TileReach.Contains(sim.Map, new TileRect(b.Tile.X, b.Tile.Z, 1, 1), b.Tile.Plane, a.Tile);
}
