using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The combat half of <see cref="TileWorldServer"/>: tick step 4b, which is roll, then apply, then die. See the
/// other partials for construction, the tick order, the session lifecycle, the pending-action resolution and the
/// actor lifecycle.
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
    // Players whose lock the movement pass may have broken, captured at the drain so a player's OWN disengaging walk
    // is never reported as a failure to reach. A list rather than a dictionary because it is enumerated.
    readonly List<(int slot, long target)> watchedLocks = new();

    static readonly Comparison<(long sinceTick, long netId, long target)> OldestLockFirst = (a, b) =>
        a.sinceTick != b.sinceTick ? a.sinceTick.CompareTo(b.sinceTick) : a.netId.CompareTo(b.netId);

    /// <summary>The game's damage rules. Null means nothing ever swings, which is the right default for a head that
    /// has not wired combat: the cooldown still runs down and no roll is ever asked for.</summary>
    public ITileCombatRules? CombatRules { get; set; }

    /// <summary>Raised as (dead net id, killer net id, slot) for every entity whose health reached zero this tick,
    /// after EVERY application, which is what makes a mutual kill raise both. The slot is the dead entity's
    /// connection slot, or -1 for anything that is not a player.
    /// <para>The engine's own half of a death is small and deliberate: it clears the DEAD entity's own combat
    /// target, and for an ACTOR it despawns the entity so the spawner starts the respawn. It does NOT clear every
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
            if (combat.TargetSeen != state.CombatTarget)
            {
                combat.TargetSeen = state.CombatTarget;
                combat.TargetSinceTick = TickCount;
            }
            // Runs down EVERY tick regardless of range and FLOORS at zero, which is what lets an attacker who spent
            // the wait walking swing on the first tick both conditions hold.
            if (combat.CooldownRemaining > 0) combat.CooldownRemaining--;
            cell.World.Set(e, combat);

            if (state.CombatTarget == 0 || combat.CooldownRemaining != 0) continue;
            if (!cell.World.TryGet(e, out TileHealth health) || health.Current == 0) continue;
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
            ResetCooldown(attacker);

            bool killed = false;
            if (outcome.Damage > 0 && TryGetHealth(target, out TileHealth hp))
            {
                int next = hp.Current - outcome.Damage;
                hp.Current = (ushort)Math.Max(0, next);
                SetHealth(target, hp);
                StampDamage(target, attacker);
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
            if (slot < 0 && actorNetIds.Contains(netId)) DespawnActor(netId);
        }
    }

    bool AlreadyDead(long netId)
    {
        for (int i = 0; i < died.Count; i++) if (died[i].netId == netId) return true;
        return false;
    }

    // A cadence of zero would swing every tick, which no game means, so a swung attacker always pays at least one.
    // The rules member wins over the component's own copy, which is the value the spawn seeded and the fallback for
    // a game that answers zero.
    void ResetCooldown(long attacker)
    {
        if (!host.TryGetOwner(attacker, out CellSim cell, out Entity e)) return;
        cell.World.TryGet(e, out TileCombatState combat);
        byte ticks = CombatRules?.AttackTicks(attacker) ?? 0;
        if (ticks == 0) ticks = combat.AttackTicks;
        combat.CooldownRemaining = ticks == 0 ? (byte)1 : ticks;
        cell.World.Set(e, combat);
    }

    void StampDamage(long target, long attacker)
    {
        if (!host.TryGetOwner(target, out CellSim cell, out Entity e)) return;
        cell.World.TryGet(e, out TileCombatState combat);
        combat.LastDamagedBy = attacker;
        combat.LastDamagedTick = TickCount;
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
            (int slot, long target) = watchedLocks[i];
            if (!netIdBySlot.TryGetValue(slot, out long netId)) continue;
            // A player who died this tick had their lock cleared by the death rather than by a failure to reach, and
            // the engine is the thing that cleared it. Telling them they could not reach the fight they were just
            // killed in would be a second, wrong explanation for the same tick.
            if (AlreadyDead(netId)) continue;
            if (!TryGetActorState(netId, out TileMoveState state) || state.CombatTarget != 0) continue;
            if (!combatTargets.TryGetFootprint(target, out _, out _)) continue;
            OnCannotReach?.Invoke(slot, target);
            SendNotice(slot, TileServerReason.CannotReach);
        }
        watchedLocks.Clear();
    }
}
