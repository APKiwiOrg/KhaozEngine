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
            // A created attempt must be able to retain its entire cadence when completed, not just fit its impact.
            _ = checked(impact + cadence);
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

    public static bool TryComplete(ref TileCombatPreparationState state, long tick,
        in TileCombatEvent outcome, out PreparedCombatEvent completed)
    {
        completed = default;
        if (!state.HasActive) return false;
        TileCombatPreparation active = state.Active;
        if (tick != active.ImpactTick || outcome.AttackerNetId != active.AttackerNetId
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
                _ = checked(ready + state.Active.CadenceTicks);
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
        acceptedTicks = accepted;
        return true;
    }

    static void ClearActive(ref TileCombatPreparationState state)
    {
        state.HasActive = false;
        state.Active = default;
        state.AttackerTeleportEpoch = 0;
        state.TargetTeleportEpoch = 0;
    }
}
