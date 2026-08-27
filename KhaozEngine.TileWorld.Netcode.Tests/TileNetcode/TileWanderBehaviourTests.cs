using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileWanderBehaviourTests
{
    const float Dt = 0.25f;

    static readonly TileActorDefinition Rat = new()
    {
        Id = "rat",
        MaxHealth = 30,
        AttackTicks = 10,
        WanderRadius = 4,
        LeashRadius = 8,
        RespawnDelayTicks = 6,
    };

    sealed class ScriptedBehaviour : ITileActorBehaviour
    {
        public readonly List<TileActorContext> Seen = new();
        public TileActorIntent Next = TileActorIntent.Idle;

        public TileActorIntent Decide(in TileActorContext context)
        {
            Seen.Add(context);
            return Next;
        }
    }

    static TileWorldServer Server(TileWorldDocument doc, INetTransport transport, TileCoord spawn, int seed = 1,
        ITileActorBehaviour? behaviour = null)
    {
        var server = new TileWorldServer(transport, TileWorldServerTickTests.Config(spawn),
            TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
        server.Actors.Seed = seed;
        server.Actors.Behaviour = behaviour ?? new TileWanderBehaviour(TileMoveSimulatorTests.Bake(doc));
        return server;
    }

    static int Chebyshev(TileCoord a, TileCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Z - b.Z));

    // A hit landing on an actor, written where the combat pass will write it: the damage record on TileCombatState.
    static void Damage(TileWorldServer s, long netId, long attacker)
    {
        Assert.True(s.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
        Assert.True(cell.World.TryGet(e, out TileCombatState combat));
        combat.LastDamagedBy = attacker;
        combat.LastDamagedTick = s.TickCount;
        cell.World.Set(e, combat);
    }

    // The same stream from the same three numbers, a different stream from a different actor, and no dependence on
    // System.Random's sequence, which is not stable across runtimes and would make the reproducibility golden fail on
    // an upgrade rather than on a regression.
    [Fact]
    public void TileActorRandom_is_deterministic_per_actor_and_per_tick()
    {
        TileActorRandom a = TileActorRandom.For(7, 42L, 100L);
        TileActorRandom b = TileActorRandom.For(7, 42L, 100L);
        for (int i = 0; i < 8; i++) Assert.Equal(a.NextUInt64(), b.NextUInt64());

        TileActorRandom other = TileActorRandom.For(7, 43L, 100L);
        TileActorRandom again = TileActorRandom.For(7, 42L, 100L);
        Assert.NotEqual(again.NextUInt64(), other.NextUInt64());

        TileActorRandom bounded = TileActorRandom.For(1, 1L, 1L);
        for (int i = 0; i < 200; i++)
        {
            int v = bounded.Next(3, 9);
            Assert.InRange(v, 3, 8);
        }
        Assert.Equal(0, TileActorRandom.For(1, 1L, 1L).Next(1));
    }

    [Fact]
    public void An_idle_actor_wanders_and_never_leaves_its_wander_radius()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(30, 30, 0));

        var visited = new HashSet<TileCoord>();
        for (int i = 0; i < 400; i++)
        {
            s.Tick(Dt);
            if (s.TryGetActorState(spawner.ActorNetId, out TileMoveState st)) visited.Add(st.Tile);
        }

        Assert.True(visited.Count > 1, "an idle actor eventually moves");
        foreach (TileCoord tile in visited)
            Assert.True(Chebyshev(tile, new TileCoord(30, 30, 0)) <= Rat.WanderRadius,
                $"{tile} is outside the wander radius");
    }

    // Reproducibility: two servers from the same seed running the same script produce the same wander, which is what
    // a replay and a golden both depend on.
    [Fact]
    public void Two_servers_from_the_same_seed_wander_identically_and_a_different_seed_differs()
    {
        var hubA = new InMemoryTransportHub();
        var hubB = new InMemoryTransportHub();
        var hubC = new InMemoryTransportHub();
        using TileWorldServer a = Server(TileMoveSimulatorTests.FlatWorld(), hubA.Server, new TileCoord(5, 5, 0), seed: 11);
        using TileWorldServer b = Server(TileMoveSimulatorTests.FlatWorld(), hubB.Server, new TileCoord(5, 5, 0), seed: 11);
        using TileWorldServer c = Server(TileMoveSimulatorTests.FlatWorld(), hubC.Server, new TileCoord(5, 5, 0), seed: 12);
        TileActorSpawner sa = a.Actors.Add(Rat, new TileCoord(30, 30, 0));
        TileActorSpawner sb = b.Actors.Add(Rat, new TileCoord(30, 30, 0));
        TileActorSpawner sc = c.Actors.Add(Rat, new TileCoord(30, 30, 0));

        var tilesA = new List<TileCoord>();
        var tilesB = new List<TileCoord>();
        var tilesC = new List<TileCoord>();
        for (int i = 0; i < 200; i++)
        {
            a.Tick(Dt);
            b.Tick(Dt);
            c.Tick(Dt);
            if (a.TryGetActorState(sa.ActorNetId, out TileMoveState ta)) tilesA.Add(ta.Tile);
            if (b.TryGetActorState(sb.ActorNetId, out TileMoveState tb)) tilesB.Add(tb.Tile);
            if (c.TryGetActorState(sc.ActorNetId, out TileMoveState tc)) tilesC.Add(tc.Tile);
        }

        Assert.Equal(tilesA, tilesB);
        Assert.NotEqual(tilesA, tilesC);
    }

    // First attacker wins, which is the simplest rule that is not an aggro table. A target already held is NOT
    // replaced by a later attacker.
    [Fact]
    public void A_damaged_actor_retaliates_against_its_attacker_and_a_held_target_wins()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(30, 32, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(30, 30, 0));
        long player = s.SpawnPlayer(0, "a", "Ari");
        s.Tick(Dt);
        long actor = spawner.ActorNetId;

        Assert.True(s.Host.TryGetOwner(actor, out CellSim cell, out Entity e));
        Assert.True(cell.World.TryGet(e, out TileCombatState combat));
        combat.LastDamagedBy = player;
        combat.LastDamagedTick = s.TickCount;
        cell.World.Set(e, combat);

        s.Tick(Dt);
        Assert.True(s.TryGetActorState(actor, out TileMoveState st));
        Assert.Equal(player, st.CombatTarget);

        // A second attacker does not steal a held target.
        long other = s.SpawnActor(new TileCoord(30, 28, 0), new TileActorSpawn(30, 10, TileDirection.S));
        Assert.True(s.Host.TryGetOwner(actor, out cell, out e));
        Assert.True(cell.World.TryGet(e, out combat));
        combat.LastDamagedBy = other;
        combat.LastDamagedTick = s.TickCount;
        cell.World.Set(e, combat);
        s.Tick(Dt);

        Assert.True(s.TryGetActorState(actor, out st));
        Assert.Equal(player, st.CombatTarget);
    }

    // The chase costs the behaviour ONE value re-issued, because the follow itself is the stepper's.
    [Fact]
    public void A_retaliating_actor_closes_on_a_target_that_walks_away()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(30, 31, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat with { LeashRadius = 40, WanderRadius = 0 },
            new TileCoord(30, 30, 0));
        long player = s.SpawnPlayer(0, "a", "Ari");
        s.Tick(Dt);
        long actor = spawner.ActorNetId;

        Assert.True(s.Host.TryGetOwner(actor, out CellSim cell, out Entity e));
        Assert.True(cell.World.TryGet(e, out TileCombatState combat));
        combat.LastDamagedBy = player;
        combat.LastDamagedTick = s.TickCount;
        cell.World.Set(e, combat);

        s.Enqueue(0, seq: 0, TileCommand.WalkTo(new TileCoord(30, 40, 0), TileMoveMode.Walk));
        for (int i = 0; i < 60; i++) s.Tick(Dt);

        Assert.True(s.TryGetPlayerState(0, out TileMoveState p));
        Assert.True(s.TryGetActorState(actor, out TileMoveState a));
        Assert.Equal(player, a.CombatTarget);
        Assert.Equal(new TileCoord(30, 40, 0), p.Tile);
        Assert.True(Chebyshev(a.Tile, p.Tile) <= 1, $"the actor closed to melee, it is at {a.Tile}");
    }

    // Full restore on ARRIVAL rather than on the break, so a monster dragged out and abandoned is not instantly
    // healthy where the player left it. This one drags with a latched command and never damages the actor, so it
    // pins the arrival rule alone. The damaged case is the test below it, and it is a different failure.
    [Fact]
    public void An_actor_dragged_past_its_leash_breaks_walks_home_and_heals_on_arrival()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat with { WanderRadius = 0 }, new TileCoord(30, 30, 0));
        s.Tick(Dt);
        long actor = spawner.ActorNetId;
        Assert.True(s.SetHealth(actor, new TileHealth { Current = 4, Max = 30 }));

        // Drag it out with a latched command, which outranks the behaviour, until it is past the leash.
        for (int i = 0; i < 60; i++)
        {
            s.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(30, 46, 0), TileMoveMode.Run));
            s.Tick(Dt);
            if (s.TryGetActorState(actor, out TileMoveState dragged)
                && Chebyshev(dragged.Tile, new TileCoord(30, 30, 0)) > Rat.LeashRadius) break;
        }
        Assert.True(s.TryGetActorState(actor, out TileMoveState outside));
        Assert.True(Chebyshev(outside.Tile, new TileCoord(30, 30, 0)) > Rat.LeashRadius);
        Assert.True(s.TryGetHealth(actor, out TileHealth hurt));
        Assert.Equal(4, hurt.Current);

        // Hands off. The behaviour breaks and walks it home, and only the ARRIVAL restores it.
        for (int i = 0; i < 200; i++)
        {
            s.Tick(Dt);
            if (s.TryGetActorState(actor, out TileMoveState st) && st.Tile.Equals(new TileCoord(30, 30, 0))
                && st.Route.IsIdle && !st.IsStepping) break;
        }

        Assert.True(s.TryGetActorState(actor, out TileMoveState home));
        Assert.Equal(new TileCoord(30, 30, 0), home.Tile);
        Assert.Equal(0L, home.CombatTarget);
        Assert.True(s.TryGetHealth(actor, out TileHealth healed));
        Assert.Equal(30, healed.Current);
    }

    // The case the test above cannot reach, because it drags with a latched command and never damages the actor. A
    // player drags a monster by HITTING it, so the damage record is fresh when the leash fires, and a break that
    // dropped only the target left the retaliation rule to re-acquire the same attacker the moment the actor was
    // back inside its radius. The heal is then lost for that break permanently, because acquiring a target clears
    // the Returning flag the restore is gated on.
    [Fact]
    public void An_actor_dragged_past_its_leash_after_real_combat_stops_retaliating_and_heals_at_home()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(30, 32, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat with { WanderRadius = 0 }, new TileCoord(30, 30, 0));
        long player = s.SpawnPlayer(0, "a", "Ari");
        s.Tick(Dt);
        long actor = spawner.ActorNetId;
        Assert.True(s.SetHealth(actor, new TileHealth { Current = 4, Max = 30 }));

        // Dragged out and hit on every tick of the drag, which is what a player dragging a monster does.
        for (int i = 0; i < 60; i++)
        {
            s.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(30, 46, 0), TileMoveMode.Run));
            Damage(s, actor, player);
            s.Tick(Dt);
            if (s.TryGetActorState(actor, out TileMoveState dragged)
                && Chebyshev(dragged.Tile, new TileCoord(30, 30, 0)) > Rat.LeashRadius) break;
        }
        Assert.True(s.TryGetActorState(actor, out TileMoveState outside));
        Assert.True(Chebyshev(outside.Tile, new TileCoord(30, 30, 0)) > Rat.LeashRadius);

        // Hands off entirely. Nobody touches it again, so nothing may pull it back into the fight.
        for (int i = 0; i < 200; i++) s.Tick(Dt);

        Assert.True(s.TryGetActorState(actor, out TileMoveState home));
        Assert.True(s.TryGetHealth(actor, out TileHealth healed));
        // One assertion over all three, because the three wrong answers are one failure: an actor that re-acquired
        // its attacker is standing beside it, still fighting, still hurt.
        Assert.True(home.Tile.Equals(new TileCoord(30, 30, 0)) && home.CombatTarget == 0L && healed.Current == 30,
            $"the actor is at {home.Tile} targeting {home.CombatTarget} at {healed.Current} of {healed.Max}");
    }

    // The other half of the mode-lifetime contract, with a behaviour installed: a latch outranks the behaviour on
    // its own tick and the mode it left behind outranks the definition's cadence afterwards, so a scripted event
    // that runs one actor somewhere does not need to re-latch on every tick to keep it running.
    [Fact]
    public void A_latched_commands_mode_survives_the_behaviours_own_ticks()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat with { WanderRadius = 0 }, new TileCoord(30, 30, 0));
        s.Tick(Dt);
        long actor = spawner.ActorNetId;

        s.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(30, 36, 0), TileMoveMode.Run));
        s.Tick(Dt);
        Assert.True(s.TryGetActorState(actor, out TileMoveState onTheLatch));
        Assert.Equal(TileMoveMode.Run, onTheLatch.Mode);

        s.Tick(Dt);
        Assert.True(s.TryGetActorState(actor, out TileMoveState next));
        Assert.Equal(TileMoveMode.Run, next.Mode);
        for (int i = 0; i < 4; i++) s.Tick(Dt);
        Assert.True(s.TryGetActorState(actor, out TileMoveState later));
        Assert.Equal(TileMoveMode.Run, later.Mode);
    }

    // The seam itself: a game's own behaviour drives the actor, and the context it is handed is the read-only view
    // the spec names, with tick-START tiles.
    [Fact]
    public void A_supplied_behaviour_drives_the_actor_and_is_handed_the_tick_start_view()
    {
        var hub = new InMemoryTransportHub();
        var scripted = new ScriptedBehaviour();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0),
            behaviour: scripted);
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(30, 30, 0));
        s.Tick(Dt);
        long actor = spawner.ActorNetId;

        TileActorContext first = Assert.Single(scripted.Seen);
        Assert.Equal(actor, first.NetId);
        Assert.Equal(new TileCoord(30, 30, 0), first.Tile);
        Assert.Equal(new TileCoord(30, 30, 0), first.Home);
        Assert.Equal("rat", first.Definition.Id);
        Assert.Equal(30, first.Health.Current);
        Assert.Equal(0L, first.CombatTarget);
        Assert.False(first.Walking);

        scripted.Seen.Clear();
        scripted.Next = TileActorIntent.WalkTo(new TileCoord(34, 30, 0));
        for (int i = 0; i < 24; i++) s.Tick(Dt);

        Assert.True(s.TryGetActorState(actor, out TileMoveState st));
        Assert.Equal(new TileCoord(34, 30, 0), st.Tile);
        // Walking was true for at least one of the decisions along the way, which is the field a behaviour uses to
        // avoid re-rolling a destination it is still walking to.
        Assert.Contains(scripted.Seen, c => c.Walking);
    }
}
