using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The combat half of <see cref="TileWorldServer"/>: tick step 4b, which is roll, then apply, then die, and the
/// step 5b reap that finishes a dead actor off once the serve has gone out. See the other partials for
/// construction, the tick order, the session lifecycle, the pending-action resolution and the actor lifecycle.
/// <para>TWO PHASES, AND THE SPLIT IS THE DESIGN. The roll phase reads every combatant as it stands at the START of
/// the phase and produces a list of outcomes. The apply phase subtracts them all. So no hit's accuracy or damage can
/// depend on another hit having landed, and the pass is order-independent for OUTCOMES. Death is evaluated ONCE,
/// after every application, which is what makes a mutual kill kill both: if A and B are each other's target and both
/// blows are lethal, BOTH DIE, and A's swing lands even though B's killing blow is applied in the same pass, because
/// the swing was rolled before either landed. The alternative, resolving attackers one at a time, makes the outcome
/// of a mutual kill depend on net id ordering, which is an arbitrary tiebreak deciding who lives.</para>
/// <para>The ORDER of the rolls still decides which draw each attacker gets from a game's own RNG, so it is FIXED at
/// oldest lock first with the net id breaking the tie. Nothing here enumerates a dictionary to reach a decision.</para>
/// <para>Tiles read here are POST-MOVEMENT, deliberately, and that is the one place this pass differs from the
/// follow. The follow reads the tick-START snapshot in <see cref="TileEntityTargets"/> so that no entity's decision
/// depends on another having already moved. A HIT is about where the two bodies ended the tick, which is what makes
/// a fleeing target out of range on exactly the tick it commits its step and back in range on the next one.</para>
/// </summary>
public sealed partial class TileWorldServer
{
    readonly List<TileCombatEvent> combatEvents = new();
    readonly List<long> combatants = new();
    readonly List<(long sinceTick, long netId, long target)> rollOrder = new();
    readonly List<(long attacker, long target, TileAttackOutcome outcome)> rolled = new();
    readonly List<(long netId, long killer)> died = new();
    // The actors this tick's deaths owe a despawn, held from phase 3 until after the serve. See ReapDeadActors.
    readonly List<long> deadActors = new();
    // Players whose lock the movement pass may have broken, captured at the drain so a player's OWN disengaging walk
    // is never reported as a failure to reach. A list rather than a dictionary because it is enumerated.
    //
    // `clicked` is the entry's PROVENANCE: true for a lock this tick's own Attack command asked for, false for one
    // the player was already holding. See ReportBrokenLocks, which is the only thing that can tell a stale click
    // apart from a target that has since gone, and can only tell them apart with this.
    readonly List<(int slot, long target, bool clicked)> watchedLocks = new();

    static readonly Comparison<(long sinceTick, long netId, long target)> OldestLockFirst = (a, b) =>
        a.sinceTick != b.sinceTick ? a.sinceTick.CompareTo(b.sinceTick) : a.netId.CompareTo(b.netId);

    /// <summary>The game's damage rules. Null means nothing ever swings, which is the right default for a head that
    /// has not wired combat: the cooldown still runs down and no roll is ever asked for.</summary>
    public ITileCombatRules? CombatRules { get; set; }

    /// <summary>Ticks on which an entity holding a combat target and ready to swing carried no
    /// <see cref="TileHealth"/> at all, and was therefore skipped.
    /// <para>THE SIGNAL FOR ONE WIRING MISTAKE, and it is the commonest one this package has: a player is spawned
    /// WITHOUT health, deliberately, because what a player's <see cref="TileHealth.Max"/> should be belongs to the
    /// game's own skill core. A game that never calls <see cref="SetHealth"/> for a player therefore has one who
    /// can neither swing nor be hit, and every guard involved is silent. This is what makes that visible. It climbs
    /// once per tick per such attacker while the lock is held, so a healthy head leaves it at zero and a non-zero
    /// reading names exactly one fix: write the health on join.</para>
    /// <para>A combatant at ZERO health is not counted. That one is a corpse, which is an ordinary outcome rather
    /// than a mistake, and counting it would bury the signal under every death in the world.</para></summary>
    public long SkippedHealthlessCombatantCount { get; private set; }

    /// <summary>Raised as (dead net id, killer net id, slot) for every entity whose health reached zero this tick,
    /// after EVERY application, which is what makes a mutual kill raise both. The slot is the dead entity's
    /// connection slot, or -1 for anything that is not a player.
    /// <para>The engine's own half of a death is small and deliberate: it clears the DEAD entity's own combat
    /// target, and for an ACTOR it despawns the entity so the spawner starts the respawn. That despawn happens
    /// AFTER this tick's serve rather than inside this event, so the corpse ships in one last snapshot at zero
    /// health and the blow that killed it reaches every viewer watching (see the reap in this file). A handler
    /// reads a live entity either way. It does NOT clear every
    /// other entity's target pointing at the corpse, because it does not have to: the target stops resolving the
    /// moment the entity is gone and the follow already clears a target that does not resolve. One rule, one place.
    /// What happens to a dead PLAYER is the game's, through this event.</para></summary>
    public event Action<long, long, int>? OnDied;

    /// <summary>Raised once per resolved swing, misses included, before the tick's serve. A game awards experience
    /// and writes a log line here.</summary>
    public event Action<TileCombatEvent>? OnCombatEvent;

    /// <summary>Every swing resolved on the tick that just ran, in roll order. Rebuilt each tick, so a caller reads
    /// it inside its own tick handler rather than keeping it.</summary>
    public IReadOnlyList<TileCombatEvent> CombatEventsThisTick => combatEvents;

    // Tick step 4b. Four phases, and the two-phase split between rolling and applying is the whole design.
    void ResolveCombat()
    {
        combatEvents.Clear();
        // Cleared at the TOP rather than beside the apply phase, so "died this tick" is answerable for the whole
        // tick rather than only from the moment a roll happened. ReportBrokenLocks reads it below, and the early
        // return under it would otherwise leave the previous tick's dead in the list.
        died.Clear();
        // DRAINED rather than cleared, and the difference between the two words is a corpse that lives forever.
        // deadActors is filled in phase 3 and spent by the reap at step 5b, which is a whole serve loop away, so a
        // throw out of any client's send loses that reap. Clearing here would then DISCARD a despawn the world is
        // still owed: the corpse stands at zero health for good, holding a slot against the cell's actor cap, keeping
        // its spawner Alive because the id still answers, and shipping to every viewer in range on every tick, with
        // nothing raised, logged or counted. Draining costs one no-op call on a healthy tick, since 5b already
        // emptied the list, and it leaves 5b the only reap site that ever runs on one, so the deferral's own argument
        // below is untouched. The recovery is at the TOP of the pass rather than beside phase 0's guards, so the
        // corpse is out of the world before this tick's roll order is built.
        ReapDeadActors();

        // PHASE 0, per combatant: stamp the lock, run the cooldown down, and decide who is eligible. Built from two
        // ORDERED lists rather than from a dictionary enumeration, because a hash layout must never reach a
        // decision and this is where the roll order is assembled.
        combatants.Clear();
        for (int i = 0; i < tickSlots.Count; i++)
            if (netIdBySlot.TryGetValue(tickSlots[i], out long playerNetId)) combatants.Add(playerNetId);
        combatants.AddRange(actorNetIds);

        rollOrder.Clear();
        for (int i = 0; i < combatants.Count; i++)
        {
            long netId = combatants[i];
            if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) continue;
            if (!cell.World.TryGet(e, out TileMoveState state)) continue;
            cell.World.TryGet(e, out TileCombatState combat);

            // A CHANGED target starts a new lock, and the tick it started is what the roll order is taken on.
            bool moved = false;
            if (combat.TargetSeen != state.CombatTarget)
            {
                combat.TargetSeen = state.CombatTarget;
                combat.TargetSinceTick = TickCount;
                moved = true;
            }
            // Runs down EVERY tick regardless of range and FLOORS at zero, which is what lets an attacker who spent
            // the wait walking swing on the first tick both conditions hold.
            if (combat.CooldownRemaining > 0) { combat.CooldownRemaining--; moved = true; }
            // WRITTEN ONLY WHEN SOMETHING MOVED. World.Set is not free for a component nothing replicates: the
            // first write is an archetype move, and every later one costs a change-tracking set insert plus a list
            // append per entity per tick. Unconditional, this pass touched every combatant on every tick, so a world
            // standing still still paid for one, and every player acquired the component on its first tick whether
            // or not it ever fought. Guarded, an idle world costs two comparisons per entity and no writes at all.
            if (moved) cell.World.Set(e, combat);

            if (state.CombatTarget == 0 || combat.CooldownRemaining != 0) continue;
            // COUNTED, and only for an ABSENT component. Everything that reaches this line wants to swing and is off
            // cooldown, so a missing TileHealth here is a game that never wrote one, which is silent in every other
            // way. A zero is a corpse and stays uncounted. See SkippedHealthlessCombatantCount.
            if (!cell.World.TryGet(e, out TileHealth health))
            {
                SkippedHealthlessCombatantCount++;
                continue;
            }
            if (health.Current == 0) continue;
            rollOrder.Add((combat.TargetSinceTick, netId, state.CombatTarget));
        }

        if (CombatRules is null || rollOrder.Count == 0) return;
        rollOrder.Sort(OldestLockFirst);

        // PHASE 1, ROLL. Every combatant is read as it stands at the START of this phase, so no hit's accuracy or
        // damage can depend on another hit having landed. Tiles here are POST-MOVEMENT, which is the one place this
        // pass differs from the follow: a hit is about where the two bodies ENDED the tick.
        rolled.Clear();
        for (int i = 0; i < rollOrder.Count; i++)
        {
            (long _, long attacker, long target) = rollOrder[i];
            if (!TryGetActorState(attacker, out TileMoveState attackerState)) continue;
            if (!TryGetActorState(target, out TileMoveState targetState)) continue;
            if (!TryGetHealth(attacker, out TileHealth attackerHealth) || attackerHealth.Current == 0) continue;
            if (!TryGetHealth(target, out TileHealth targetHealth) || targetHealth.Current == 0) continue;

            var footprint = new TileRect(targetState.Tile.X, targetState.Tile.Z, 1, 1);
            if (!TileReach.Contains(simulator.Map, footprint, targetState.Tile.Plane, attackerState.Tile)) continue;

            TileAttackOutcome outcome = CombatRules.Roll(new TileAttackContext(
                attacker, attackerState.Tile, attackerHealth, target, targetState.Tile, targetHealth, TickCount));
            rolled.Add((attacker, target, outcome));
        }

        // PHASE 2, APPLY, after every roll.
        for (int i = 0; i < rolled.Count; i++)
        {
            (long attacker, long target, TileAttackOutcome outcome) = rolled[i];
            // A RESOLVED SWING is what section 13.3 calls a combat event touching them, so both parties are stamped
            // whether it landed or missed and whether it took any health. Two writes per swing, one per party, with
            // the cooldown and the damage record folded into the same read-modify-write rather than costing a
            // second one each.
            NoteSwing(attacker);
            NoteSwungAt(target, attacker, outcome.Landed);

            bool killed = false;
            if (outcome.Damage > 0 && TryGetHealth(target, out TileHealth hp))
            {
                int next = hp.Current - outcome.Damage;
                hp.Current = (ushort)Math.Max(0, next);
                SetHealth(target, hp);
                // Not already dead this pass, so the death rides the blow that actually took it to zero rather than
                // every later blow rolled against the same corpse.
                if (hp.Current == 0 && !AlreadyDead(target))
                {
                    died.Add((target, attacker));
                    killed = true;
                }
            }

            var ev = new TileCombatEvent(attacker, target, outcome.Damage, outcome.Kind, outcome.Landed, killed);
            combatEvents.Add(ev);
            OnCombatEvent?.Invoke(ev);
        }

        // PHASE 3, DIE, ONCE, after every application. That is what makes a mutual kill kill both.
        for (int i = 0; i < died.Count; i++)
        {
            (long netId, long killer) = died[i];
            // The engine clears the DEAD entity's own target and nothing else. Every OTHER entity's target pointing
            // at the corpse stops resolving the moment the entity is gone, and the follow already clears a target
            // that does not resolve. One rule, one place.
            ClearCombatTarget(netId);
            int slot = SlotOf(netId);
            // BEFORE anything is removed, so a handler can still read the entity it is being told about.
            OnDied?.Invoke(netId, killer, slot);
            // COLLECTED here and despawned after the serve. See ReapDeadActors for the whole reason.
            if (slot < 0 && actorNetIds.Contains(netId)) deadActors.Add(netId);
        }
    }

    // Tick step 5b: the despawn half of an ACTOR's death, held back until every client has been served.
    //
    // IT USED TO RUN INSIDE PHASE 3, and that made the Killed bit dead on the wire for the only case R1 has. The
    // serve builds each viewer's interest set from the LIVE world at step 5, and SendCombatTo keeps an event whose
    // TARGET is in that set, so a monster despawned at 4b was already gone from the grid when the set was built and
    // its killing blow was filtered out of every viewer's frame. A head could then only learn a monster died by
    // noticing its absence, which is the one thing the flag exists to avoid, and an absence cannot be told apart
    // from a walk out of interest at all.
    //
    // Deferring rather than widening the filter is the choice, and the tick order is the argument. The alternative
    // was to let SendCombatTo accept a target that DIED this tick as well as one in interest, which is cheaper but
    // hands a death to a viewer who was nowhere near the fight unless the previous serve's interest set is kept as
    // a second piece of per-slot state. Here the corpse simply ships in one more snapshot at zero health, which is
    // what presentation wants anyway (the monster is drawn dead on the tick it died, then leaves), it makes the step
    // 4b comment true as written, and it costs no new state at all.
    //
    // CALLED FROM TWO PLACES, and the second one is a recovery rather than a second reap site. Step 5b runs it on
    // every healthy tick and empties the list. The top of the next ResolveCombat runs it again, which does nothing at
    // all unless a tick threw between 4b and 5b, in which case it is what stops the lost despawn being lost forever.
    //
    // IT DOES NOT RESURRECT THE GHOST-KILL BUG. Nothing between phase 3 and here can target or resolve a corpse:
    // step 1b's actor decisions and step 4b's rolls both ran earlier in this tick, and this runs before the next
    // tick's 0c snapshot is taken, so no later pass ever sees the entity. The zero-health guards in phase 0 and the
    // roll phase are unchanged and would refuse it anyway.
    void ReapDeadActors()
    {
        for (int i = 0; i < deadActors.Count; i++) DespawnActor(deadActors[i]);
        deadActors.Clear();
    }

    bool AlreadyDead(long netId)
    {
        for (int i = 0; i < died.Count; i++) if (died[i].netId == netId) return true;
        return false;
    }

    // The ATTACKER's half of a resolved swing: it pays its cadence, and the swing counts as a combat event that
    // touched it, which is what keeps an attacker whose target died in combat for the window rather than free to
    // log out on the tick the fight ended.
    //
    // A cadence of zero would swing every tick, which no game means, so a swung attacker always pays at least one.
    // The rules member wins over the component's own copy, which is the value the spawn seeded and the fallback for
    // a game that answers zero.
    void NoteSwing(long attacker)
    {
        if (!host.TryGetOwner(attacker, out CellSim cell, out Entity e)) return;
        cell.World.TryGet(e, out TileCombatState combat);
        byte ticks = CombatRules?.AttackTicks(attacker) ?? 0;
        if (ticks == 0) ticks = combat.AttackTicks;
        combat.CooldownRemaining = ticks == 0 ? (byte)1 : ticks;
        combat.LastCombatTick = TickCount;
        cell.World.Set(e, combat);
    }

    // The TARGET's half, and the two facts it writes are deliberately not the same fact. Every resolved swing
    // touched it, miss included, which is what the logout window reads. Only a swing that LANDED names an attacker,
    // which is what a retaliation reads, and a hit that connected for zero is a landed swing: the ruling asks for a
    // counterattack when a hit lands rather than when damage does.
    void NoteSwungAt(long target, long attacker, bool landed)
    {
        if (!host.TryGetOwner(target, out CellSim cell, out Entity e)) return;
        cell.World.TryGet(e, out TileCombatState combat);
        combat.LastCombatTick = TickCount;
        if (landed)
        {
            combat.LastDamagedBy = attacker;
            combat.LastDamagedTick = TickCount;
        }
        cell.World.Set(e, combat);
    }

    // The mirror of ClearInteractTarget in TileWorldServer.Actions.cs, and for the same reason: the state is the one
    // record of the lock, and the simulator reads it on every tick.
    bool ClearCombatTarget(long netId)
    {
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) return false;
        if (!cell.World.TryGet(e, out TileMoveState live)) return false;
        live.CombatTarget = 0;
        cell.World.Set(e, live);
        return true;
    }

    // The reverse of the player index, scanned rather than mirrored: it is asked once per death, the table is bounded
    // by MaxPlayers, and a second index is a second thing to keep correct on every join and every leave.
    int SlotOf(long netId)
    {
        foreach (KeyValuePair<int, long> entry in netIdBySlot)
            if (entry.Value == netId) return entry.Key;
        return -1;
    }

    // The server half of the follow's rule 5. A lock that the movement pass cleared, whose target STILL RESOLVES, was
    // broken by a failure to reach rather than by a death or a departure, and that is the one case the player has to
    // be told about, because their own client is still showing a pending attack. Same event and same token an
    // unreachable interaction gets, because it is the same fact.
    void ReportBrokenLocks()
    {
        for (int i = 0; i < watchedLocks.Count; i++)
        {
            (int slot, long target, bool clicked) = watchedLocks[i];
            if (!netIdBySlot.TryGetValue(slot, out long netId)) continue;
            // A player who died this tick had their lock cleared by the death rather than by a failure to reach, and
            // the engine is the thing that cleared it. Telling them they could not reach the fight they were just
            // killed in would be a second, wrong explanation for the same tick.
            if (AlreadyDead(netId)) continue;
            if (!TryGetActorState(netId, out TileMoveState state) || state.CombatTarget != 0) continue;
            // A target this tick's snapshot cannot resolve is two different facts, and only the provenance of the
            // watch tells them apart. A lock the player was ALREADY HOLDING lost its target to a death, a despawn or
            // a handoff, which is not a failure to reach and is not the player's to be told about. A lock THIS
            // TICK's click asked for named an id the world never held on this tick, which is a click at a monster
            // that went away a moment ago, and CannotReach is exactly its answer: that is what the same click made
            // as an Interact already gets (see Admit's Interact case), and the two clicks should not disagree.
            if (!clicked && !combatTargets.TryGetFootprint(target, out _, out _)) continue;
            OnCannotReach?.Invoke(slot, target);
            SendNotice(slot, TileServerReason.CannotReach);
        }
        // NOT cleared here. The tick body empties it before it fills it (step 1), so a tick that threw between the
        // fill and this report cannot carry its entries into the next one and report a lock that was broken a tick
        // ago. The tick is already broken at that point, and a stale notice is a second wrong thing on top of it.
    }
}
