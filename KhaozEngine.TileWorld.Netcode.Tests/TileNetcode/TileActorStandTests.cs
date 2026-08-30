using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;
using static KhaozEngine.Tests.TileNetcode.TileWanderBehaviourTests;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The stand-your-ground rule and the <see cref="TileActorIntentKind.Stand"/> intent under it: an actor something
/// has locked onto stops walking away before the first blow lands. The wander harness is
/// <see cref="TileWanderBehaviourTests"/>'s, shared through its internal helpers.
/// </summary>
public class TileActorStandTests
{
    // A hand-built tick-start view, for the rule-order pins below: no server, just the behaviour's own decision.
    static TileActorContext Context(TileCoord tile, TileCoord home, long combatTarget = 0L, long damagedBy = 0L,
        long damagedTick = 0L, bool walking = false, long tick = 100L, long targetedBy = 0L,
        long attackedBy = 0L, long attackedTick = 0L) =>
        new(NetId: 1L, Tile: tile, Home: home, Definition: Rat, Health: new TileHealth { Current = 30, Max = 30 },
            CombatTarget: combatTarget, TargetTile: default, TargetResolved: false, LastDamagedBy: damagedBy,
            LastDamagedTick: damagedTick, Walking: walking, Tick: tick,
            Rng: TileActorRandom.For(1, 1L, tick), TargetedBy: targetedBy,
            LastAttackedBy: attackedBy, LastAttackedTick: attackedTick);

    // The stand-your-ground rule and its PLACE in the order, which is the design: below the leash, the chase and
    // the retaliation, above the wander. Each clause here is one inversion of that order made visible.
    [Fact]
    public void The_stand_rule_sits_between_retaliate_and_wander()
    {
        var behaviour = new TileWanderBehaviour(TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()));
        var home = new TileCoord(30, 30, 0);

        // Targeted while walking a wander leg: stand, which is what cancels the route in flight. Targeted while
        // standing still: stand again, which is what stops a NEW wander from starting.
        Assert.Equal(TileActorIntentKind.Stand,
            behaviour.Decide(Context(home, home, walking: true, targetedBy: 7L)).Kind);
        Assert.Equal(TileActorIntentKind.Stand, behaviour.Decide(Context(home, home, targetedBy: 7L)).Kind);

        // Already SWUNG AT by the thing coming for it, a miss included: retaliation wins, an actor the attacker
        // has opened on answers back rather than waiting politely for the next blow.
        TileActorIntent hitBack = behaviour.Decide(Context(home, home, attackedBy: 7L, attackedTick: 99L,
            targetedBy: 7L));
        Assert.Equal(TileActorIntentKind.Attack, hitBack.Kind);
        Assert.Equal(7L, hitBack.Target);

        // The damage record ALONE no longer drives retaliation: aggression answers the swing, and the wound is
        // for threat tables. In the live pass the two are always written together for a landed hit, so this
        // only distinguishes the read, which is exactly what it is here to pin.
        Assert.Equal(TileActorIntentKind.Stand,
            behaviour.Decide(Context(home, home, damagedBy: 7L, damagedTick: 99L, targetedBy: 7L)).Kind);

        // A held target outranks an incoming lock, which is first-attacker-wins from the other side.
        TileActorIntent held = behaviour.Decide(Context(home, home, combatTarget: 9L, targetedBy: 7L));
        Assert.Equal(TileActorIntentKind.Attack, held.Kind);
        Assert.Equal(9L, held.Target);

        // And the leash still outranks everything: dragged out and targeted, it breaks for home.
        Assert.Equal(TileActorIntentKind.Break,
            behaviour.Decide(Context(new TileCoord(50, 50, 0), home, targetedBy: 7L)).Kind);
    }

    // The headline scenario, through the whole server: a player's attack click freezes a wandering monster where
    // it is, inside the step already under way, instead of letting it finish the walk it had rolled. This is the
    // consumer-visible rule the stand intent exists for, and it is red with the rule removed: the rat walks its
    // leg to the end and the NotEqual below meets the route's own destination.
    [Fact]
    public void An_attacked_wanderer_stops_where_it_is_instead_of_finishing_its_walk()
    {
        var hub = new InMemoryTransportHub();
        // The player spawns far enough away that it cannot arrive inside the observation window: the freeze has
        // to be the LOCK's doing, not the first landed hit's.
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(30, 52, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(30, 30, 0));
        long player = s.SpawnPlayer(0, "a", "Ari");
        s.Tick(Dt);
        long actor = spawner.ActorNetId;

        // Wait for a wander leg long enough that stopping short of its end is observable.
        TileCoord routeEnd = default;
        bool midWalk = false;
        for (int i = 0; i < 600 && !midWalk; i++)
        {
            s.Tick(Dt);
            if (!s.TryGetActorState(actor, out TileMoveState st)) continue;
            if (st.Route.IsIdle || Chebyshev(st.Route.End, st.Tile) < 3) continue;
            routeEnd = st.Route.End;
            midWalk = true;
        }
        Assert.True(midWalk, "the rat never started a wander leg three tiles long, so this pins nothing");

        // The attack click. The lock is written on the tick the command lands, the snapshot hands it to the
        // behaviour on the tick after, and the step in flight finishes on its own cadence.
        s.Enqueue(0, seq: 0, TileCommand.Attack(actor, TileMoveMode.Walk));
        for (int i = 0; i < 12; i++) s.Tick(Dt);

        Assert.True(s.TryGetActorState(actor, out TileMoveState stopped));
        Assert.True(stopped.Route.IsIdle && !stopped.IsStepping, "the wander route is cancelled");
        Assert.NotEqual(routeEnd, stopped.Tile);

        // And it HOLDS: no new wander starts while the lock does, so the monster is standing exactly where it
        // froze when the attacker finally arrives.
        TileCoord held = stopped.Tile;
        for (int i = 0; i < 30; i++)
        {
            s.Tick(Dt);
            Assert.True(s.TryGetActorState(actor, out TileMoveState st));
            Assert.Equal(held, st.Tile);
        }
        Assert.True(s.TryGetPlayerState(0, out TileMoveState p));
        Assert.Equal(actor, p.CombatTarget);
        Assert.NotEqual(0L, player);
    }

    // The Stand intent's own mechanics, driven by a scripted behaviour so nothing here depends on the default
    // rule order: the route dies through the one stepper, and the damage record SURVIVES, which is what makes
    // standing different from Break. An actor that stands is waiting for a fight, not giving one up.
    [Fact]
    public void A_standing_actor_cancels_its_route_and_keeps_its_damage_record()
    {
        var hub = new InMemoryTransportHub();
        var scripted = new ScriptedBehaviour();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0),
            behaviour: scripted);
        TileActorSpawner spawner = s.Actors.Add(Rat with { WanderRadius = 0 }, new TileCoord(30, 30, 0));
        s.Tick(Dt);
        long actor = spawner.ActorNetId;

        var goal = new TileCoord(30, 38, 0);
        scripted.Next = TileActorIntent.WalkTo(goal);
        s.Tick(Dt);
        scripted.Next = TileActorIntent.Idle;
        s.Tick(Dt);
        Assert.True(s.TryGetActorState(actor, out TileMoveState walking));
        Assert.False(walking.Route.IsIdle);

        Damage(s, actor, 4242L);
        scripted.Next = TileActorIntent.Stand;
        for (int i = 0; i < 8; i++) s.Tick(Dt);

        Assert.True(s.TryGetActorState(actor, out TileMoveState stood));
        Assert.True(stood.Route.IsIdle && !stood.IsStepping);
        Assert.NotEqual(goal, stood.Tile);
        Assert.True(s.TryGetCombatState(actor, out TileCombatState record));
        Assert.Equal(4242L, record.LastDamagedBy);
    }

    // The plumbing under the rule: TargetedBy reaches the behaviour out of the tick-start snapshot, one tick
    // behind the accepted command, and the lowest net id answers when several entities hold the same target,
    // which is what keeps the answer independent of cell and ECS iteration order.
    [Fact]
    public void A_behaviour_is_told_who_targets_its_actor_and_the_lowest_id_wins()
    {
        var hub = new InMemoryTransportHub();
        var scripted = new ScriptedBehaviour();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(30, 40, 0),
            behaviour: scripted);
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(30, 30, 0));
        long first = s.SpawnPlayer(0, "a", "Ari");
        long second = s.SpawnPlayer(1, "b", "Bea");
        s.Tick(Dt);
        long actor = spawner.ActorNetId;
        Assert.NotEqual(0L, actor);
        Assert.Equal(0L, scripted.Seen[^1].TargetedBy);

        // The HIGHER net id locks on alone first, so the later both-locked answer below cannot pass by accident.
        (long lower, long higher) = first < second ? (first, second) : (second, first);
        int higherSlot = higher == first ? 0 : 1;
        int lowerSlot = higher == first ? 1 : 0;
        s.Enqueue(higherSlot, seq: 0, TileCommand.Attack(actor, TileMoveMode.Walk));
        s.Tick(Dt);
        s.Tick(Dt);
        Assert.Equal(higher, scripted.Seen[^1].TargetedBy);

        s.Enqueue(lowerSlot, seq: 0, TileCommand.Attack(actor, TileMoveMode.Walk));
        s.Tick(Dt);
        s.Tick(Dt);
        Assert.Equal(lower, scripted.Seen[^1].TargetedBy);
    }
}
