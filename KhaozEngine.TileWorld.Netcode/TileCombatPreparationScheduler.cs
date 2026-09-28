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

    static void ClearActive(ref TileCombatPreparationState state)
    {
        state.HasActive = false;
        state.Active = default;
        state.AttackerTeleportEpoch = 0;
        state.TargetTeleportEpoch = 0;
    }
}
