using System;
using System.Collections.Generic;
using System.Text;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileCombatResolveTests
{
    const float Dt = 0.25f;

    sealed class FixedRules : ITileCombatRules
    {
        public ushort Damage = 5;
        public bool Land = true;
        public byte Ticks = 4;
        public byte Kind = 7;
        public readonly List<TileAttackContext> Rolls = new();

        public TileAttackOutcome Roll(in TileAttackContext context)
        {
            Rolls.Add(context);
            return Land ? TileAttackOutcome.Hit(Damage, Kind) : TileAttackOutcome.Miss(Kind);
        }

        public byte AttackTicks(long attackerNetId) => Ticks;
    }

    static TileWorldServer Server(TileWorldDocument doc, INetTransport transport, TileCoord spawn,
        ITileCombatRules rules, int combatLogoutTicks = 0)
    {
        var server = new TileWorldServer(transport,
            TileWorldServerTickTests.Config(spawn) with { CombatLogoutTicks = combatLogoutTicks },
            TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
        server.CombatRules = rules;
        // No behaviour is wired, which is the host's own default, so every actor below stands exactly where this
        // test put it. Deciding what a monster does is task 4's subject, not this task's.
        return server;
    }

    // Two actors placed exactly where the test wants them, locked onto each other or not, with no spawner and no
    // behaviour in the way.
    static (long a, long b) Pair(TileWorldServer s, TileCoord at, TileCoord other, ushort health = 100)
    {
        long a = s.SpawnActor(at, new TileActorSpawn(health, 4, TileDirection.S));
        long b = s.SpawnActor(other, new TileActorSpawn(health, 4, TileDirection.S));
        return (a, b);
    }

    static void Lock(TileWorldServer s, long attacker, long target)
    {
        Assert.True(s.Host.TryGetOwner(attacker, out CellSim cell, out Entity e));
        Assert.True(cell.World.TryGet(e, out TileMoveState state));
        state.CombatTarget = target;
        cell.World.Set(e, state);
    }

    [Fact]
    public void A_swing_at_a_cardinal_neighbour_lands_and_subtracts_damage()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Damage = 9 };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0), rules);
        (long a, long b) = Pair(s, new TileCoord(20, 20, 0), new TileCoord(20, 21, 0));
        Lock(s, a, b);

        s.Tick(Dt);

        Assert.True(s.TryGetHealth(b, out TileHealth hp));
        Assert.Equal(91, hp.Current);
        TileAttackContext roll = Assert.Single(rules.Rolls);
        Assert.Equal(a, roll.AttackerNetId);
        Assert.Equal(b, roll.TargetNetId);
        Assert.Equal(new TileCoord(20, 20, 0), roll.AttackerTile);
        Assert.Equal(new TileCoord(20, 21, 0), roll.TargetTile);
        TileCombatEvent ev = Assert.Single(s.CombatEventsThisTick);
        Assert.True(ev.Landed);
        Assert.False(ev.Killed);
        Assert.Equal(9, ev.Amount);
        Assert.Equal(7, ev.Kind);
    }

    // Melee range IS TileReach.Contains, which for a 1x1 target is the four cardinals and none of the four
    // diagonals. No new rule, no new function, no second definition of range.
    //
    // What is asked is the tile the swing was rolled FROM, not merely whether one happened. A locked attacker
    // CHASES, and a diagonal is one step from a cardinal, so a diagonally placed attacker steps into range and
    // swings on that very tick: movement runs before combat on purpose, so a hit is judged on where the two bodies
    // ended the tick. Asking only "did it swing" would therefore pass for every one of the eight.
    [Fact]
    public void A_hit_lands_on_the_four_cardinals_and_on_none_of_the_four_diagonals()
    {
        var cardinals = new[] { (0, 1), (0, -1), (1, 0), (-1, 0) };
        var diagonals = new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) };

        foreach ((int dx, int dz) in cardinals) Assert.True(Swings(dx, dz), $"cardinal ({dx},{dz}) should reach");
        foreach ((int dx, int dz) in diagonals) Assert.False(Swings(dx, dz), $"diagonal ({dx},{dz}) must not reach");

        static bool Swings(int dx, int dz)
        {
            var hub = new InMemoryTransportHub();
            var rules = new FixedRules();
            using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0), rules);
            (long a, long b) = Pair(s, new TileCoord(20, 20, 0), new TileCoord(20 + dx, 20 + dz, 0));
            Lock(s, a, b);
            s.Tick(Dt);
            return rules.Rolls.Count == 1 && rules.Rolls[0].AttackerTile.Equals(new TileCoord(20, 20, 0));
        }
    }

    // THE SAFESPOT, and it falls out rather than being built: TileReach's outward step asks whether the target's own
    // tile could step onto the attacker's, so a fence between them denies melee with no combat code at all.
    [Fact]
    public void A_blocker_between_attacker_and_target_denies_the_hit_with_no_combat_code()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules();
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        // A solid greybox object ON the tile the attacker would have to stand on, so the target's outward step has
        // nowhere to land on that side.
        doc.AddObject("tree", 20, 20, 0, 0);
        using TileWorldServer s = Server(doc, hub.Server, new TileCoord(5, 5, 0), rules);
        long a = s.SpawnActor(new TileCoord(19, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));
        long b = s.SpawnActor(new TileCoord(21, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));
        Lock(s, a, b);

        s.Tick(Dt);

        Assert.Empty(rules.Rolls);
        Assert.True(s.TryGetHealth(b, out TileHealth hp));
        Assert.Equal(100, hp.Current);
    }

    // ROLL THEN APPLY. Both swings were rolled before either landed, so a mutual kill kills BOTH, and the outcome
    // does not depend on which entity the pass reached first. The reversed-order run is what makes this pin
    // order-independence rather than one ordering.
    [Fact]
    public void Two_lethal_blows_on_one_tick_kill_both_whichever_order_the_pair_was_spawned_in()
    {
        foreach (bool reversed in new[] { false, true })
        {
            var hub = new InMemoryTransportHub();
            var rules = new FixedRules { Damage = 50 };
            using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0), rules);
            var deaths = new List<(long dead, long killer)>();
            s.OnDied += (dead, killer, _) => deaths.Add((dead, killer));

            TileCoord first = reversed ? new TileCoord(20, 21, 0) : new TileCoord(20, 20, 0);
            TileCoord second = reversed ? new TileCoord(20, 20, 0) : new TileCoord(20, 21, 0);
            long x = s.SpawnActor(first, new TileActorSpawn(40, 4, TileDirection.S));
            long y = s.SpawnActor(second, new TileActorSpawn(40, 4, TileDirection.S));
            Lock(s, x, y);
            Lock(s, y, x);

            s.Tick(Dt);

            Assert.Equal(2, s.CombatEventsThisTick.Count);
            Assert.All(s.CombatEventsThisTick, ev => Assert.True(ev.Killed));
            Assert.Equal(2, deaths.Count);
            Assert.Contains(deaths, d => d.dead == x && d.killer == y);
            Assert.Contains(deaths, d => d.dead == y && d.killer == x);
            Assert.Equal(0, s.ActorCount);
        }
    }

    // The cooldown runs down EVERY tick regardless of range and FLOORS at zero, so an attacker who spent the wait
    // walking swings on the first tick both conditions hold. That is OSRS, and it is what stops a chase from also
    // being a cooldown reset.
    [Fact]
    public void The_cooldown_floors_at_zero_out_of_range_and_swings_on_the_first_tick_both_conditions_hold()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Ticks = 4 };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0), rules);
        long a = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));
        long b = s.SpawnActor(new TileCoord(20, 21, 0), new TileActorSpawn(100, 4, TileDirection.S));
        Lock(s, a, b);

        s.Tick(Dt);                                   // swing 1, cooldown 4
        Assert.Single(rules.Rolls);

        // Walk the target out of reach and hold it there for longer than the cadence.
        s.Actors.Command(b, TileCommand.WalkTo(new TileCoord(20, 30, 0), TileMoveMode.Run));
        for (int i = 0; i < 10; i++) s.Tick(Dt);
        Assert.Single(rules.Rolls);

        // Bring it back adjacent. The very next tick swings, because the cooldown floored while it was away.
        s.Actors.Command(b, TileCommand.WalkTo(new TileCoord(20, 21, 0), TileMoveMode.Run));
        for (int i = 0; i < 24 && rules.Rolls.Count < 2; i++) s.Tick(Dt);
        Assert.True(rules.Rolls.Count >= 2, "the attacker swung on the first tick it was back in reach");
    }

    // Acquiring a NEW target does not reset the cooldown, so target switching is neither a penalty nor free damage.
    // An attacker that MISSED still pays it.
    [Fact]
    public void A_target_switch_does_not_reset_the_cooldown_and_a_miss_still_pays_it()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Ticks = 4, Land = false };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0), rules);
        long a = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));
        long b = s.SpawnActor(new TileCoord(20, 21, 0), new TileActorSpawn(100, 4, TileDirection.S));
        long c = s.SpawnActor(new TileCoord(19, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));
        Lock(s, a, b);

        s.Tick(Dt);
        Assert.Single(rules.Rolls);
        // A miss still produced an event, because a fight with invisible misses reads as a broken fight.
        TileCombatEvent miss = Assert.Single(s.CombatEventsThisTick);
        Assert.False(miss.Landed);
        Assert.Equal(0, miss.Amount);
        Assert.True(s.TryGetHealth(b, out TileHealth untouched));
        Assert.Equal(100, untouched.Current);

        Lock(s, a, c);
        s.Tick(Dt);
        Assert.Single(rules.Rolls);                   // still on cooldown, the switch bought nothing
        s.Tick(Dt);
        s.Tick(Dt);
        Assert.Single(rules.Rolls);
        s.Tick(Dt);
        Assert.Equal(2, rules.Rolls.Count);
        Assert.Equal(c, rules.Rolls[1].TargetNetId);
    }

    // Death, for an ACTOR: the engine despawns it and its spawner starts the respawn on the next tick, with nothing
    // listening for a death event to make that happen.
    [Fact]
    public void A_killed_actor_is_despawned_and_its_spawner_respawns_it()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Damage = 500 };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0), rules);
        TileActorSpawner spawner = s.Actors.Add(new TileActorDefinition
        {
            Id = "rat", MaxHealth = 30, AttackTicks = 0, WanderRadius = 0, LeashRadius = 8, RespawnDelayTicks = 3,
        }, new TileCoord(20, 21, 0));
        s.Tick(Dt);
        long victim = spawner.ActorNetId;
        long killer = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));
        Lock(s, killer, victim);

        var deaths = new List<(long dead, long killerId, int slot)>();
        s.OnDied += (dead, k, slot) => deaths.Add((dead, k, slot));

        s.Tick(Dt);

        (long dead, long killerId, int slot) = Assert.Single(deaths);
        Assert.Equal(victim, dead);
        Assert.Equal(killer, killerId);
        Assert.Equal(-1, slot);
        Assert.False(s.TryGetActorState(victim, out _));
        TileCombatEvent blow = Assert.Single(s.CombatEventsThisTick);
        Assert.True(blow.Killed, "the death rides the blow that caused it");

        for (int i = 0; i < 6; i++) s.Tick(Dt);
        Assert.Equal(TileActorSpawnerState.Alive, spawner.State);
        Assert.NotEqual(victim, spawner.ActorNetId);
        Assert.True(s.TryGetHealth(spawner.ActorNetId, out TileHealth fresh));
        Assert.Equal(30, fresh.Current);
    }

    // A PLAYER's death is the game's: OnDied names the slot, and the game answers it. The engine clears the dead
    // entity's own lock and nothing else, because every OTHER entity's target stops resolving on its own.
    [Fact]
    public void A_killed_player_raises_OnDied_with_its_slot_and_the_engine_clears_only_its_own_lock()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Damage = 500 };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(20, 21, 0), rules);
        long player = s.SpawnPlayer(0, "a", "Ari");
        Assert.True(s.SetHealth(player, new TileHealth { Current = 40, Max = 40 }));
        long monster = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));
        Lock(s, monster, player);
        Lock(s, player, monster);

        var deaths = new List<(long dead, long killer, int slot)>();
        s.OnDied += (dead, killer, slot) => deaths.Add((dead, killer, slot));

        s.Tick(Dt);

        // The pair are locked onto each other and one blow is lethal either way, so BOTH die: that is the mutual
        // kill the roll-then-apply test above pins. What THIS test is about is the player's half of it, which is
        // why the death is picked out by name rather than by being the only one.
        Assert.Equal(2, deaths.Count);
        (long dead, long killerId, int slot) = Assert.Single(deaths, d => d.dead == player);
        Assert.Equal(player, dead);
        Assert.Equal(monster, killerId);
        Assert.Equal(0, slot);
        // Still in the world: what happens to a dead player is the game's decision, through this event.
        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Equal(0L, st.CombatTarget);
    }

    // Two servers from the same seed running the same scripted commands produce the same event sequence, which is
    // what the fixed (TargetSinceTick, netId) roll order buys.
    [Fact]
    public void Two_servers_running_the_same_script_produce_the_same_combat_event_sequence()
    {
        var a = Run();
        var b = Run();
        Assert.Equal(a, b);

        static List<TileCombatEvent> Run()
        {
            var hub = new InMemoryTransportHub();
            var rules = new FixedRules { Damage = 3, Ticks = 2 };
            using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0), rules);
            long x = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 2, TileDirection.S));
            long y = s.SpawnActor(new TileCoord(20, 21, 0), new TileActorSpawn(100, 2, TileDirection.S));
            long z = s.SpawnActor(new TileCoord(20, 22, 0), new TileActorSpawn(100, 2, TileDirection.S));
            Lock(s, x, y);
            Lock(s, z, y);
            Lock(s, y, x);

            var seen = new List<TileCombatEvent>();
            for (int i = 0; i < 20; i++)
            {
                s.Tick(Dt);
                seen.AddRange(s.CombatEventsThisTick);
            }
            return seen;
        }
    }

    // The follow's rule 5 and its server-side half: a target that cannot be reached at all clears the lock AND the
    // player is told, with the same token an unreachable interaction gets. A player's own WalkTo also clears the
    // lock, and that must NOT produce a notice: it is a disengage, not a failure.
    [Fact]
    public void An_unreachable_target_notices_the_player_and_a_disengaging_walk_does_not()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules();
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("tree", 30, 29, 0, 0);
        doc.AddObject("tree", 30, 31, 0, 0);
        doc.AddObject("tree", 29, 30, 0, 0);
        doc.AddObject("tree", 31, 30, 0, 0);
        using TileWorldServer s = Server(doc, hub.Server, new TileCoord(20, 20, 0), rules);
        s.SpawnPlayer(0, "a", "Ari");
        long walled = s.SpawnActor(new TileCoord(30, 30, 0), new TileActorSpawn(100, 4, TileDirection.S));
        var refused = new List<long>();
        s.OnCannotReach += (_, target) => refused.Add(target);

        s.Enqueue(0, seq: 0, TileCommand.Attack(walled, TileMoveMode.Run));
        s.Tick(Dt);

        Assert.Equal(new[] { walled }, refused);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Equal(0L, st.CombatTarget);

        // Now a reachable target, broken by the player's own walk. No notice.
        refused.Clear();
        long reachable = s.SpawnActor(new TileCoord(20, 25, 0), new TileActorSpawn(100, 4, TileDirection.S));
        s.Enqueue(0, seq: 1, TileCommand.Attack(reachable, TileMoveMode.Run));
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out st));
        Assert.Equal(reachable, st.CombatTarget);

        s.Enqueue(0, seq: 2, TileCommand.WalkTo(new TileCoord(20, 20, 0), TileMoveMode.Run));
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out st));
        Assert.Equal(0L, st.CombatTarget);
        Assert.Empty(refused);
    }

    // Section 13.3. A player in combat who disconnects is not removed at once: the entity LINGERS in world, still
    // attackable, until the window lapses, and then persists and leaves through the ordinary drain.
    //
    // The leave is driven by DROPPING THE TRANSPORT rather than by Kick, because Kick forces an immediate close:
    // an operator kick, a drain and a recycled seat all bypass the linger deliberately, since none of them is the
    // leaving player's decision. A dropped link is the one path that lingers, and it is the path the rule exists
    // for.
    [Fact]
    public void A_player_in_combat_who_leaves_lingers_attackable_and_then_drains_normally()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Damage = 2, Ticks = 1 };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(20, 21, 0), rules,
            combatLogoutTicks: 8);
        INetTransport c = hub.CreateClient();
        var client = new NetClient(c, Encoding.UTF8.GetBytes("a"));
        client.Poll();
        s.Poll();
        int slot = Assert.Single(s.JoinedSlots);
        Assert.True(s.TryGetPlayerNetId(slot, out long player));
        Assert.True(s.SetHealth(player, new TileHealth { Current = 100, Max = 100 }));
        long monster = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 1, TileDirection.S));
        Lock(s, monster, player);
        // More than one tick, because a hit landing on tick zero is indistinguishable from never having been hit:
        // LastDamagedTick is zero for both.
        for (int i = 0; i < 3; i++) s.Tick(Dt);

        var left = new List<string>();
        s.PlayerLeaving += (_, account, _) => left.Add(account);

        hub.DisconnectClient(c);
        s.Poll();
        Assert.Empty(left);
        // The seat is still held, because the leave has been DEFERRED rather than run: the body is still stepped,
        // still served and still in the player index.
        Assert.Equal(1, s.PlayerCount);

        // The entity is still there and still being hit.
        Assert.True(s.TryGetHealth(player, out TileHealth before));
        for (int i = 0; i < 4; i++) s.Tick(Dt);
        Assert.True(s.TryGetHealth(player, out TileHealth during));
        Assert.True(during.Current < before.Current, "the lingering body is still attackable");
        Assert.Empty(left);

        for (int i = 0; i < 8; i++) s.Tick(Dt);
        Assert.Equal(new[] { "a" }, left);
        Assert.False(s.TryGetHealth(player, out _));
        Assert.Equal(0, s.PlayerCount);
    }
}
