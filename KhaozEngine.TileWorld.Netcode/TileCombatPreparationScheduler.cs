using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Why the pure scheduler refused a new attempt without changing state.</summary>
internal enum TilePreparationFailure
{
    None,
    InvalidProfile,
    IdentityExhausted,
    TickOverflow
}

/// <summary>Pure attempt transitions. The caller owns reach, life, cooldown decrement and the combat roll.</summary>
internal static class TileCombatPreparationScheduler
{
    public static bool TryCreate(ref TileCombatPreparationState state, long tick, long attacker, long target,
        in TileCombatPreparationProfile profile, byte cadence, long readyTick,
        uint attackerEpoch, uint targetEpoch, out TilePreparationFailure failure)
    {
        failure = TilePreparationFailure.None;
        if (state.HasActive) return false;
        if (profile.LeadTicks == 0 || profile.StrikeTicks == 0
            || profile.StrikeTicks > profile.LeadTicks || profile.LeadTicks > cadence)
        {
            failure = TilePreparationFailure.InvalidProfile;
            return false;
        }
        if (state.LastAttackId == ulong.MaxValue)
        {
            failure = TilePreparationFailure.IdentityExhausted;
            return false;
        }

        long impact, prepare;
        try
        {
            impact = Math.Max(checked(tick + profile.LeadTicks), Math.Max(readyTick, state.ReadyNotBeforeTick));
            prepare = checked(impact - profile.LeadTicks);
            // A created attempt must be able to retain its entire cadence when completed on its last legal
            // late tick, not just fit its impact.
            _ = checked(impact + profile.StrikeTicks + cadence);
        }
        catch (OverflowException)
        {
            failure = TilePreparationFailure.TickOverflow;
            return false;
        }

        ulong id = state.LastAttackId + 1;
        state.Active = new TileCombatPreparation(attacker, target, id, 1, profile.PresentationKey,
            prepare, impact, profile.StrikeTicks, cadence);
        state.HasActive = true;
        state.LastAttackId = id;
        state.AttackerTeleportEpoch = attackerEpoch;
        state.TargetTeleportEpoch = targetEpoch;
        return true;
    }

    public static bool TryCancel(ref TileCombatPreparationState state, long tick,
        TileCombatPreparationEndReason reason, out CombatPreparationEnded ended)
    {
        ended = default;
        if (!state.HasActive) return false;
        TileCombatPreparation active = state.Active;
        ended = new CombatPreparationEnded(tick, active.AttackerNetId, active.TargetNetId, active.AttackId,
            active.Revision, active.PresentationKey, active.ImpactTick, reason);
        state.ReadyNotBeforeTick = Math.Max(state.ReadyNotBeforeTick, active.ImpactTick);
        ClearActive(ref state);
        return true;
    }

    /// <summary>
    /// Keeps an active attempt whose impact lacks legal reach, recording <paramref name="tick"/> as its latest
    /// deferral. Accepted only from the impact tick while below the bound, and past the impact only after a
    /// deferral on the previous tick. The attempt itself is unchanged. False changes nothing.
    /// </summary>
    public static bool TryDefer(ref TileCombatPreparationState state, long tick)
    {
        if (!state.HasActive) return false;
        TileCombatPreparation active = state.Active;
        if (tick < active.ImpactTick || !TileCombatPreparationDeferral.IsLive(active, tick)) return false;
        if (tick != active.ImpactTick && state.DeferredTick != tick - 1) return false;
        state.DeferredTick = tick;
        return true;
    }

    public static bool TryComplete(ref TileCombatPreparationState state, long tick,
        in TileCombatEvent outcome, out PreparedCombatEvent completed)
    {
        completed = default;
        if (!state.HasActive) return false;
        TileCombatPreparation active = state.Active;
        // On time, or late within the bound after an unbroken deferral through the previous tick.
        bool due = tick == active.ImpactTick
            || (tick > active.ImpactTick
                && tick - active.ImpactTick <= TileCombatPreparationDeferral.BoundTicks(active)
                && state.DeferredTick == tick - 1);
        if (!due || outcome.AttackerNetId != active.AttackerNetId
            || outcome.TargetNetId != active.TargetNetId) return false;

        long ready = checked(tick + active.CadenceTicks);
        completed = new PreparedCombatEvent(tick, active.AttackId, active.Revision,
            active.PresentationKey, outcome);
        state.ReadyNotBeforeTick = Math.Max(state.ReadyNotBeforeTick, ready);
        ClearActive(ref state);
        return true;
    }

    public static bool TryDelay(ref TileCombatPreparationState state, long tick, byte ticks, long cooldownReadyTick,
        out byte acceptedTicks, out TilePreparationFailure failure)
    {
        acceptedTicks = 0;
        failure = TilePreparationFailure.None;
        if (ticks == 0) return true;

        long ready, prepare = 0;
        byte accepted;
        try
        {
            long basis = Math.Max(tick, Math.Max(cooldownReadyTick, state.ReadyNotBeforeTick));
            if (state.HasActive) basis = Math.Max(basis, state.Active.ImpactTick);
            long remaining = checked(basis - tick);
            if (remaining >= byte.MaxValue) return true;
            accepted = (byte)Math.Min(ticks, byte.MaxValue - remaining);
            ready = checked(basis + accepted);
            if (state.HasActive)
            {
                long shift = checked(ready - state.Active.ImpactTick);
                prepare = checked(state.Active.PrepareTick + shift);
                _ = checked(ready + state.Active.StrikeTicks + state.Active.CadenceTicks);
            }
        }
        catch (OverflowException)
        {
            failure = TilePreparationFailure.TickOverflow;
            return false;
        }

        TileCombatPreparation active = state.Active;
        ulong lastId = state.LastAttackId;
        if (state.HasActive)
        {
            uint revision = active.Revision;
            ulong id = active.AttackId;
            if (revision == uint.MaxValue)
            {
                if (lastId == ulong.MaxValue)
                {
                    failure = TilePreparationFailure.IdentityExhausted;
                    return false;
                }
                id = ++lastId;
                revision = 1;
            }
            else revision++;
            active = active with { AttackId = id, Revision = revision, PrepareTick = prepare, ImpactTick = ready };
        }

        state.Active = active;
        state.LastAttackId = lastId;
        state.ReadyNotBeforeTick = ready;
        // A revision restarts the deferral bound from its new impact.
        state.DeferredTick = 0;
        acceptedTicks = accepted;
        return true;
    }

    static void ClearActive(ref TileCombatPreparationState state)
    {
        state.HasActive = false;
        state.Active = default;
        state.AttackerTeleportEpoch = 0;
        state.TargetTeleportEpoch = 0;
        state.DeferredTick = 0;
    }
}
