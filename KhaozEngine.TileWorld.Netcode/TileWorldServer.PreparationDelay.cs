using System;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

public sealed partial class TileWorldServer
{
    long combatCountdownTick = -1;
    int combatCountdownPassed;
    bool combatCountdownComplete;

    void ResetCombatCountdown() => combatCountdownTick = -1;

    void BeginCombatCountdown()
    {
        combatCountdownTick = TickCount;
        combatCountdownPassed = 0;
        combatCountdownComplete = false;
    }

    bool CombatCountdownHasRun(long attacker)
    {
        if (combatCountdownTick != TickCount) return false;
        if (combatCountdownComplete) return true;
        // Scheduling reads its current actor. Keep that common path constant-time across a crowded combat pass.
        if (combatCountdownPassed > 0 && combatants[combatCountdownPassed - 1] == attacker) return true;
        // A profile can inspect readiness during the pass. Actors later in the existing combatant list still
        // owe their decrement, so a single whole-pass flag would answer one of the two actors a tick wrong.
        int index = combatants.IndexOf(attacker);
        return index >= 0 && index < combatCountdownPassed;
    }

    long CooldownReadyTick(long attacker, in TileCombatState combat)
    {
        int remaining = combat.CooldownRemaining;
        if (remaining > 0 && !CombatCountdownHasRun(attacker)) remaining--;
        return checked(TickCount + remaining);
    }

    bool CurrentPreparedRoll(long attacker, out TileCombatPreparation rolledAttempt)
    {
        rolledAttempt = default;
        return preparation is not null && preparation.BufferTick == TickCount
            && preparation.Rolled.TryGetValue(attacker, out rolledAttempt);
    }

    long AttackReadyTick(long attacker, in TileCombatState combat, in TileCombatPreparationState state)
    {
        long ready = CooldownReadyTick(attacker, combat);
        if (preparation is null) return ready;
        bool rolled = CurrentPreparedRoll(attacker, out TileCombatPreparation admitted);
        if (rolled) ready = Math.Max(ready, checked(TickCount + admitted.CadenceTicks));
        if (state.HasActive && (!rolled || state.Active.AttackId != admitted.AttackId))
            ready = Math.Max(ready, state.Active.ImpactTick);
        return Math.Max(ready, state.ReadyNotBeforeTick);
    }

    /// <summary>The authoritative earliest boundary for the next unresolved attack. A current preparation supplies
    /// its impact tick, or the current tick while that impact is deferred for legal reach, because it resolves on the
    /// first tick reach is legal. Without one, this is a cooldown/readiness bound and a fresh preparation may start
    /// later. A swing already rolled this tick cannot be delayed, so reads during its apply callbacks name the next
    /// wait.</summary>
    /// <param name="attackerNetId">The live entity whose readiness to read.</param>
    /// <param name="tick">The absolute ready tick, or the current tick when no wait remains. Default on a miss.</param>
    /// <returns>False only when the entity is unknown or removed.</returns>
    /// <exception cref="OverflowException">The server clock plus its outstanding cooldown is not representable.</exception>
    public bool TryGetAttackReadyTick(long attackerNetId, out long tick)
    {
        tick = default;
        if (!host.TryGetOwner(attackerNetId, out CellSim cell, out Entity e) || !cell.World.IsAlive(e)) return false;
        cell.World.TryGet(e, out TileCombatState combat);
        cell.World.TryGet(e, out TileCombatPreparationState state);
        tick = AttackReadyTick(attackerNetId, combat, state);
        return true;
    }

    // True means this preparation path handled the request. False leaves the unchanged idle/legacy byte path
    // in DelayAttack in charge. A handled request can still fail without mutation if its identity or tick runs out.
    bool TryHandlePreparationDelay(long attacker, CellSim cell, Entity entity, byte ticks,
        ref TileCombatState combat, out bool succeeded)
    {
        succeeded = false;
        if (preparation is null) return false;
        cell.World.TryGet(entity, out TileCombatPreparationState state);
        bool rolled = CurrentPreparedRoll(attacker, out TileCombatPreparation admitted);
        if (!state.HasActive && state.ReadyNotBeforeTick <= TickCount && !rolled) return false;

        long ready;
        try { ready = AttackReadyTick(attacker, combat, state); }
        catch (OverflowException)
        {
            InvalidCombatPreparationCount++;
            return true;
        }

        TileCombatPreparationState delayed = state;
        bool awaitingApply = rolled && state.HasActive && state.Active.AttackId == admitted.AttackId;
        // Another attacker's result callback may charge this actor before its own already-rolled hit is applied.
        // Preserve that hit's identity and deadline. Only its following readiness absorbs the delay.
        if (awaitingApply) delayed.HasActive = false;
        if (!TileCombatPreparationScheduler.TryDelay(ref delayed, TickCount, ticks, ready,
            out byte acceptedTicks, out TilePreparationFailure failure))
        {
            if (failure == TilePreparationFailure.IdentityExhausted) ExhaustedCombatPreparationIdentityCount++;
            else InvalidCombatPreparationCount++;
            return true;
        }
        if (acceptedTicks > 0)
        {
            if (awaitingApply) state.ReadyNotBeforeTick = delayed.ReadyNotBeforeTick;
            else state = delayed;
            combat.CooldownRemaining = (byte)Math.Min(byte.MaxValue, combat.CooldownRemaining + acceptedTicks);
            cell.World.Set(entity, state);
            cell.World.Set(entity, combat);
        }
        succeeded = true;
        return true;
    }
}
