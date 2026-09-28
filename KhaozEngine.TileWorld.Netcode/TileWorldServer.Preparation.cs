using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

public sealed partial class TileWorldServer
{
    readonly PreparationRuntime? preparation;

    sealed class PreparationRuntime
    {
        internal readonly List<PreparedCombatEvent> Results = new();
        internal readonly List<CombatPreparationEnded> Ended = new();
        internal readonly List<CombatPreparationEnded> PendingEnds = new();
        internal readonly Dictionary<long, TileCombatPreparation> Rolled = new();
        internal readonly ReadOnlyCollection<PreparedCombatEvent> ResultView;
        internal readonly ReadOnlyCollection<CombatPreparationEnded> EndedView;
        internal long BufferTick = -1;

        internal PreparationRuntime()
        {
            ResultView = Results.AsReadOnly();
            EndedView = Ended.AsReadOnly();
        }
    }

    /// <summary>Attempts refused because their profile or deadline cannot be represented.</summary>
    public long InvalidCombatPreparationCount { get; private set; }

    /// <summary>Attempts refused because the attacker's monotonically increasing identity is exhausted.</summary>
    public long ExhaustedCombatPreparationIdentityCount { get; private set; }

    internal IReadOnlyList<PreparedCombatEvent> PreparedCombatEventsThisTick =>
        preparation is { } runtime ? runtime.ResultView : Array.Empty<PreparedCombatEvent>();

    internal IReadOnlyList<CombatPreparationEnded> EndedCombatPreparationsThisTick =>
        preparation is { } runtime ? runtime.EndedView : Array.Empty<CombatPreparationEnded>();

    /// <summary>The current authoritative attempt for an attacker, false when preparation is disabled or idle.</summary>
    /// <param name="attackerNetId">The attacker to read.</param>
    /// <param name="preparation">The current attempt, default when none exists.</param>
    public bool TryGetCombatPreparation(long attackerNetId, out TileCombatPreparation preparation)
    {
        preparation = default;
        if (this.preparation is null || !host.TryGetOwner(attackerNetId, out CellSim cell, out Entity e)
            || !cell.World.TryGet(e, out TileCombatPreparationState state) || !state.HasActive) return false;
        preparation = state.Active;
        return true;
    }

    static PreparationRuntime? CreatePreparationRuntime(TileWorldServerConfig config, ReplicationRegistry registry)
    {
        if (config.CombatPreparationRules is null) return null;
        if (!registry.IsRegistered(TileProtocol.TileCombatPreparationStateTypeId))
            throw new ArgumentException("Enabled preparation requires TileCombatPreparationState registration.", nameof(registry));
        return new PreparationRuntime();
    }

    void BeginPreparationTick()
    {
        ResetCombatCountdown();
        if (preparation is null) return;
        preparation.BufferTick = TickCount;
        preparation.Results.Clear();
        preparation.Ended.Clear();
        preparation.Ended.AddRange(preparation.PendingEnds);
        preparation.PendingEnds.Clear();
        preparation.Rolled.Clear();
    }

    bool PreparationIsDue(long attacker, CellSim cell, Entity entity, in TileMoveState move, in TileCombatState combat)
    {
        cell.World.TryGet(entity, out TileCombatPreparationState state);
        if (state.HasActive)
        {
            TileCombatPreparationEndReason reason = PreparationInvalidity(attacker, move, combat, state);
            if (reason == TileCombatPreparationEndReason.None)
                return state.Active.ImpactTick == TickCount && combat.CooldownRemaining == 0;

            bool wasDue = state.Active.ImpactTick <= TickCount;
            EndPreparation(cell, entity, ref state, reason);
            // A failed due attempt cannot restart in its own pass. Earlier intent changes can prepare afresh.
            if (wasDue || reason is not (TileCombatPreparationEndReason.TargetChanged
                or TileCombatPreparationEndReason.ProfileChanged or TileCombatPreparationEndReason.Teleport)) return false;
        }

        TryStartPreparation(attacker, cell, entity, move, combat, ref state);
        return false;
    }

    TileCombatPreparationEndReason PreparationInvalidity(long attacker, in TileMoveState move,
        in TileCombatState combat, in TileCombatPreparationState state)
    {
        if (CombatRules is null) return TileCombatPreparationEndReason.RulesUnavailable;
        if (!PreparationParticipantAlive(attacker, out _)) return TileCombatPreparationEndReason.ParticipantUnavailable;
        // A newly admitted target can start preparing now even if the old target died in this same tick.
        if (move.CombatTarget != 0 && move.CombatTarget != state.Active.TargetNetId)
            return TileCombatPreparationEndReason.TargetChanged;
        if (!PreparationParticipantAlive(state.Active.TargetNetId, out TileMoveState target))
            return TileCombatPreparationEndReason.ParticipantUnavailable;
        if (move.CombatTarget == 0) return TileCombatPreparationEndReason.Disengaged;
        if (move.TeleportEpoch != state.AttackerTeleportEpoch || target.TeleportEpoch != state.TargetTeleportEpoch)
            return TileCombatPreparationEndReason.Teleport;
        if (!CombatRules.CanAttack(attacker, move.CombatTarget)) return TileCombatPreparationEndReason.PermissionRevoked;
        byte cadence = PreparationCadence(attacker, combat);
        TileCombatPreparationProfile profile = config.CombatPreparationRules!.ProfileFor(attacker);
        if (!ValidPreparationProfile(profile, cadence)) return TileCombatPreparationEndReason.InvalidProfile;
        if (!SamePreparationProfile(state.Active, profile, cadence)) return TileCombatPreparationEndReason.ProfileChanged;
        // An attempt not present for its deadline cannot become a late hit when its owner becomes available again.
        if (TickCount > state.Active.ImpactTick) return TileCombatPreparationEndReason.ParticipantUnavailable;
        if (TickCount == state.Active.ImpactTick && !InPreparationReach(attacker, move, target))
            return TileCombatPreparationEndReason.IllegalReach;
        return TileCombatPreparationEndReason.None;
    }

    bool TryStartPreparation(long attacker, CellSim cell, Entity entity, in TileMoveState move,
        in TileCombatState combat, ref TileCombatPreparationState state, TileCombatPreparation? previous = null)
    {
        if (CombatRules is null || move.CombatTarget == 0
            || !PreparationParticipantAlive(attacker, out _)
            || !PreparationParticipantAlive(move.CombatTarget, out TileMoveState target)
            || !CombatRules.CanAttack(attacker, move.CombatTarget)
            || !InPreparationReach(attacker, move, target)) return false;
        byte cadence = PreparationCadence(attacker, combat);
        TileCombatPreparationProfile profile = config.CombatPreparationRules!.ProfileFor(attacker);
        if (!ValidPreparationProfile(profile, cadence)) return false;
        if (previous is { } completed && (completed.TargetNetId != move.CombatTarget
            || !SamePreparationProfile(completed, profile, cadence))) return false;

        long ready;
        try { ready = AttackReadyTick(attacker, combat, state); }
        catch (OverflowException)
        {
            InvalidCombatPreparationCount++;
            return false;
        }
        if (!TileCombatPreparationScheduler.TryCreate(ref state, TickCount, attacker, move.CombatTarget,
            profile, cadence, ready, move.TeleportEpoch, target.TeleportEpoch,
            out TilePreparationFailure failure))
        {
            if (failure == TilePreparationFailure.IdentityExhausted) ExhaustedCombatPreparationIdentityCount++;
            else if (failure != TilePreparationFailure.None) InvalidCombatPreparationCount++;
            return false;
        }
        cell.World.Set(entity, state);
        return true;
    }

    bool ValidPreparationProfile(in TileCombatPreparationProfile profile, byte cadence)
    {
        if (profile.LeadTicks > 0 && profile.StrikeTicks > 0
            && profile.StrikeTicks <= profile.LeadTicks && profile.LeadTicks <= cadence) return true;
        InvalidCombatPreparationCount++;
        return false;
    }

    static bool SamePreparationProfile(in TileCombatPreparation active, in TileCombatPreparationProfile profile,
        byte cadence) => active.PresentationKey == profile.PresentationKey && active.StrikeTicks == profile.StrikeTicks
        && active.ImpactTick - active.PrepareTick == profile.LeadTicks && active.CadenceTicks == cadence;

    byte PreparationCadence(long attacker, in TileCombatState combat)
    {
        byte ticks = CombatRules!.AttackTicks(attacker);
        if (ticks == 0) ticks = combat.AttackTicks;
        return ticks == 0 ? (byte)1 : ticks;
    }

    bool PreparationParticipantAlive(long netId, out TileMoveState move)
    {
        if (!TryGetActorState(netId, out move)) return false;
        if (TryGetHealth(netId, out TileHealth health)) return health.Current > 0;
        SkippedHealthlessCombatantCount++;
        return false;
    }

    bool InPreparationReach(long attacker, in TileMoveState move, in TileMoveState target) =>
        TryGetMoverSimulator(attacker, out TileMoveSimulator mover)
        && TileReach.Contains(mover.Map, target.Footprint, target.Tile.Plane, move.Tile, mover.FootprintOf(move).Width);

    void CapturePreparedRoll(long attacker)
    {
        if (preparation is not null && TryGetCombatPreparation(attacker, out TileCombatPreparation active))
            preparation.Rolled[attacker] = active;
    }

    byte PreparedRollCadence(long attacker) => preparation is not null
        && preparation.Rolled.TryGetValue(attacker, out TileCombatPreparation active) ? active.CadenceTicks : (byte)0;

    void CompletePreparedSwing(in TileCombatEvent outcome)
    {
        if (preparation is null || !preparation.Rolled.TryGetValue(outcome.AttackerNetId, out TileCombatPreparation active)) return;
        // Roll order is fixed before any apply callback. A callback can remove a later attacker, but cannot undo
        // the outcome already rolled for it, so the captured identity survives even when its entity does not.
        var completed = new PreparedCombatEvent(TickCount, active.AttackId, active.Revision, active.PresentationKey, outcome);
        if (host.TryGetOwner(outcome.AttackerNetId, out CellSim cell, out Entity e)
            && cell.World.TryGet(e, out TileCombatPreparationState state)
            && TileCombatPreparationScheduler.TryComplete(ref state, TickCount, outcome, out _))
            cell.World.Set(e, state);
        preparation.Results.Add(completed);
    }

    void FinishPreparationCombat()
    {
        if (preparation is null) return;
        // Any future attempt affected by a death or a game's apply callback ends before this tick is served.
        foreach (long netId in actorNetIds) RecheckPreparation(netId);
        foreach (long netId in netIdBySlot.Values) RecheckPreparation(netId);
        foreach (PreparedCombatEvent result in preparation.Results)
        {
            long attacker = result.Outcome.AttackerNetId;
            if (!host.TryGetOwner(attacker, out CellSim cell, out Entity e)
                || !cell.World.TryGet(e, out TileMoveState move)
                || !cell.World.TryGet(e, out TileCombatState combat)
                || !cell.World.TryGet(e, out TileCombatPreparationState state) || state.HasActive) continue;
            TryStartPreparation(attacker, cell, e, move, combat, ref state, preparation.Rolled[attacker]);
        }
    }

    void RecheckPreparation(long attacker)
    {
        if (!host.TryGetOwner(attacker, out CellSim cell, out Entity e)
            || !cell.World.TryGet(e, out TileCombatPreparationState state) || !state.HasActive
            || !cell.World.TryGet(e, out TileMoveState move)) return;
        cell.World.TryGet(e, out TileCombatState combat);
        TileCombatPreparationEndReason reason = PreparationInvalidity(attacker, move, combat, state);
        if (reason != TileCombatPreparationEndReason.None) EndPreparation(cell, e, ref state, reason);
    }

    void EndPreparation(CellSim cell, Entity entity, ref TileCombatPreparationState state,
        TileCombatPreparationEndReason reason)
    {
        if (!TileCombatPreparationScheduler.TryCancel(ref state, TickCount, reason, out CombatPreparationEnded ended)) return;
        cell.World.Set(entity, state);
        if (preparation!.BufferTick == TickCount) preparation.Ended.Add(ended);
        else preparation.PendingEnds.Add(ended);
    }

    void RemovePreparationParticipant(long netId)
    {
        if (preparation is null) return;
        // The removed participant can already be absent from the actor index, but its entity still exists here.
        EndPreparationForRemovedParticipant(netId, netId);
        foreach (long attacker in actorNetIds)
            if (attacker != netId) EndPreparationForRemovedParticipant(attacker, netId);
        foreach (long attacker in netIdBySlot.Values)
            if (attacker != netId) EndPreparationForRemovedParticipant(attacker, netId);
    }

    void EndPreparationForRemovedParticipant(long attacker, long removed)
    {
        if (!host.TryGetOwner(attacker, out CellSim cell, out Entity e)
            || !cell.World.TryGet(e, out TileCombatPreparationState state) || !state.HasActive
            || (attacker != removed && state.Active.TargetNetId != removed)) return;
        if (preparation!.BufferTick == TickCount && preparation.Rolled.TryGetValue(attacker, out TileCombatPreparation rolledAttempt)
            && rolledAttempt.AttackId == state.Active.AttackId) return;
        EndPreparation(cell, e, ref state, TileCombatPreparationEndReason.ParticipantUnavailable);
    }
}
