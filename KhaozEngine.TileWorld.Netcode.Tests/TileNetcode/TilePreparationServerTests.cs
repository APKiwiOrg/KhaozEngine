using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationServerTests
{
    [Theory]
    [InlineData(1f / 6f)]
    [InlineData(0.25f)]
    public void First_roll_is_on_the_scheduled_tick(float seconds)
    {
        using var fight = PreparationScenario.Create(tickSeconds: seconds);
        fight.Step();
        Assert.Equal(103L, fight.Preparation().ImpactTick);
        Assert.Empty(fight.Rules.Rolls);
        Assert.True(fight.Server.TryGetCombatState(fight.Attacker, out TileCombatState combat));
        Assert.Equal(0L, combat.LastCombatTick);
        fight.AdvanceTo(103);
        Assert.Empty(fight.Rules.Rolls);
        fight.Step();
        Assert.Equal(103L, Assert.Single(fight.Rules.Rolls).Tick);
        PreparedCombatEvent result = Assert.Single(fight.Server.PreparedCombatEventsThisTick);
        Assert.Equal(103L, result.ImpactTick);
        Assert.Equal(fight.Attacker, result.Outcome.AttackerNetId);
        Assert.Equal(1UL, result.AttackId);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 5)]
    public void Next_roll_keeps_the_existing_cadence(bool landed, ushort damage)
    {
        using var fight = PreparationScenario.Create();
        fight.Rules.Land = landed;
        fight.Rules.Damage = damage;
        int awards = 0;
        fight.Server.OnCombatEvent += outcome =>
        {
            awards++;
            Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
            Assert.True(fight.Server.TryGetCombatState(fight.Attacker, out TileCombatState combat));
            Assert.Equal(14, combat.CooldownRemaining);
        };
        fight.AdvanceTo(104);
        Assert.Equal(new long[] { 103 }, fight.Rules.Rolls.Select(x => x.Tick));
        Assert.Equal((114L, 116L, 117L),
            (fight.Preparation().PrepareTick, fight.Preparation().StrikeTick, fight.Preparation().ImpactTick));
        Assert.Equal(landed, Assert.Single(fight.Server.PreparedCombatEventsThisTick).Outcome.Landed);
        fight.AdvanceTo(118);
        Assert.Equal(new long[] { 103, 117 }, fight.Rules.Rolls.Select(x => x.Tick));
        Assert.Equal(2, awards);
    }

    [Fact]
    public void Temporary_range_loss_does_not_restart_the_attempt()
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        TileCombatPreparation before = fight.Preparation();
        fight.Server.OnAfterMovement += _ =>
        {
            if (fight.Server.TickCount == 101) fight.SetTargetPosition(new TileCoord(40, 40, 0));
        };
        fight.Step();
        Assert.Equal(before, fight.Preparation());
        fight.SetTargetPosition(new TileCoord(20, 21, 0));
        fight.AdvanceTo(104);
        Assert.Equal(103L, Assert.Single(fight.Rules.Rolls).Tick);
    }

    [Fact]
    public void Invalid_due_attempt_requires_a_fresh_lead()
    {
        using var fight = PreparationScenario.Create();
        fight.Server.OnAfterMovement += _ =>
        {
            if (fight.Server.TickCount == 103) fight.SetTargetPosition(new TileCoord(40, 40, 0));
        };
        fight.AdvanceTo(104);
        Assert.Empty(fight.Rules.Rolls);
        Assert.Equal(TileCombatPreparationEndReason.IllegalReach,
            Assert.Single(fight.Server.EndedCombatPreparationsThisTick).Reason);
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
        Assert.True(fight.Server.TryGetCombatState(fight.Attacker, out TileCombatState combat));
        Assert.Equal(0, combat.CooldownRemaining);
        fight.SetTargetPosition(new TileCoord(20, 21, 0));
        fight.Step();
        Assert.Equal(107L, fight.Preparation().ImpactTick);
        Assert.Equal(2UL, fight.Preparation().AttackId);
    }

    [Theory]
    [InlineData(101, 102, 105)]
    [InlineData(103, 104, 107)]
    [InlineData(104, 105, 117)]
    [InlineData(104, 120, 123)]
    public void Null_rules_create_no_attempt_and_cancel_live_attempts(long nullAt, long restoreAt, long expected)
    {
        using var fight = PreparationScenario.Create();
        fight.AdvanceTo(nullAt);
        TileCombatPreparation old = fight.Preparation();
        int rolls = fight.Rules.Rolls.Count;
        fight.Server.CombatRules = null;
        int profileReads = fight.ProfileSource.Reads;
        fight.Step();
        CombatPreparationEnded ended = Assert.Single(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal(TileCombatPreparationEndReason.RulesUnavailable, ended.Reason);
        Assert.Equal(old.ImpactTick, ended.ImpactTick);
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
        fight.AdvanceTo(restoreAt);
        Assert.Equal(rolls, fight.Rules.Rolls.Count);
        Assert.Equal(profileReads, fight.ProfileSource.Reads);
        fight.Server.CombatRules = fight.Rules;
        fight.Step();
        Assert.Equal(expected, fight.Preparation().ImpactTick);
        Assert.True(fight.Preparation().AttackId > old.AttackId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Null_rules_with_no_attempt_never_read_the_preparation_profile(bool enabled)
    {
        using var fight = PreparationScenario.Create(enabled: enabled);
        fight.Server.CombatRules = null;
        fight.AdvanceTo(105);
        Assert.Empty(fight.Rules.Rolls);
        Assert.Equal(0, fight.ProfileSource.Reads);
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("lead")]
    [InlineData("strike")]
    [InlineData("cadence")]
    [InlineData("target")]
    [InlineData("attacker-epoch")]
    [InlineData("target-epoch")]
    public void Changed_intent_replaces_the_attempt_with_a_fresh_identity(string change)
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        TileCombatPreparation old = fight.Preparation();
        TileCombatPreparationEndReason reason = TileCombatPreparationEndReason.ProfileChanged;
        switch (change)
        {
            case "key": fight.SetProfile(new(3, 1, 9)); break;
            case "lead": fight.SetProfile(new(4, 1, 7)); break;
            case "strike": fight.SetProfile(new(3, 2, 7)); break;
            case "cadence": fight.Rules.Ticks = 16; break;
            case "target":
                long target = fight.Server.SpawnActor(new TileCoord(20, 19, 0), new TileActorSpawn(1000, 14, TileDirection.N));
                TileCombatResolveTests.Lock(fight.Server, fight.Attacker, target);
                reason = TileCombatPreparationEndReason.TargetChanged;
                break;
            default:
                long id = change == "attacker-epoch" ? fight.Attacker : fight.Target;
                Assert.True(fight.Server.Host.TryGetOwner(id, out CellSim cell, out Entity e));
                Assert.True(cell.World.TryGet(e, out TileMoveState state));
                state.Epoch++;
                cell.World.Set(e, state);
                reason = TileCombatPreparationEndReason.Teleport;
                break;
        }
        fight.Step();
        Assert.Equal(reason, Assert.Single(fight.Server.EndedCombatPreparationsThisTick).Reason);
        Assert.Equal(old.AttackId + 1, fight.Preparation().AttackId);
        Assert.Equal(change == "lead" ? 105L : 104L, fight.Preparation().ImpactTick);
    }

    [Fact]
    public void A_profile_change_on_the_due_tick_does_not_replace_in_that_pass()
    {
        using var fight = PreparationScenario.Create();
        fight.AdvanceTo(103);
        fight.SetProfile(new(3, 1, 9));
        fight.Step();
        Assert.Empty(fight.Rules.Rolls);
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
        fight.Step();
        Assert.Equal(107L, fight.Preparation().ImpactTick);
    }

    [Fact]
    public void A_valid_retarget_prepares_immediately_even_if_the_previous_target_died_that_tick()
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        long next = fight.Server.SpawnActor(new TileCoord(20, 19, 0), new TileActorSpawn(1000, 14, TileDirection.N));
        fight.Server.SetHealth(fight.Target, new TileHealth { Max = 1000, Current = 0 });
        TileCombatResolveTests.Lock(fight.Server, fight.Attacker, next);

        fight.Step();

        Assert.Equal(TileCombatPreparationEndReason.TargetChanged,
            Assert.Single(fight.Server.EndedCombatPreparationsThisTick).Reason);
        Assert.Equal(next, fight.Preparation().TargetNetId);
        Assert.Equal(104L, fight.Preparation().ImpactTick);
    }

    [Theory]
    [InlineData("disengage", TileCombatPreparationEndReason.Disengaged)]
    [InlineData("permission", TileCombatPreparationEndReason.PermissionRevoked)]
    [InlineData("dead", TileCombatPreparationEndReason.ParticipantUnavailable)]
    [InlineData("invalid", TileCombatPreparationEndReason.InvalidProfile)]
    public void Invalid_attempts_end_without_a_roll(string change, TileCombatPreparationEndReason reason)
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        switch (change)
        {
            case "disengage": TileCombatResolveTests.Lock(fight.Server, fight.Attacker, 0); break;
            case "permission": fight.Rules.Allowed = false; break;
            case "dead": fight.Server.SetHealth(fight.Target, new TileHealth { Max = 1000, Current = 0 }); break;
            case "invalid": fight.SetProfile(new(0, 1, 7)); break;
        }
        fight.Step();
        Assert.Equal(reason, Assert.Single(fight.Server.EndedCombatPreparationsThisTick).Reason);
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
        Assert.Empty(fight.Rules.Rolls);
        if (change == "invalid") Assert.Equal(1L, fight.Server.InvalidCombatPreparationCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Despawn_preserves_the_cancelled_record_after_the_entity_is_gone(bool attacker)
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        Assert.True(fight.Server.DespawnActor(attacker ? fight.Attacker : fight.Target));
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
        fight.Step();
        CombatPreparationEnded ended = Assert.Single(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal(fight.Attacker, ended.AttackerNetId);
        Assert.Equal(fight.Target, ended.TargetNetId);
        Assert.Equal(103L, ended.ImpactTick);
        Assert.Equal(TileCombatPreparationEndReason.ParticipantUnavailable, ended.Reason);
        fight.Step();
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
    }

    [Fact]
    public void Leaving_player_retires_preparation_before_despawn()
    {
        using var fight = PreparationScenario.Create();
        long player = fight.Server.SpawnPlayer(0, "player", "Player");
        fight.Server.SetHealth(player, new TileHealth { Current = 1000, Max = 1000 });
        fight.Server.Enqueue(0, 0, TileCommand.Attack(fight.Target, TileMoveMode.Walk));
        fight.Step();
        Assert.True(fight.Server.TryGetCombatPreparation(player, out _));
        fight.Server.Kick(0, "test-disconnect");
        fight.Step();
        Assert.False(fight.Server.Host.TryGetOwner(player, out _, out _));
        Assert.Equal(player, Assert.Single(fight.Server.EndedCombatPreparationsThisTick).AttackerNetId);
    }

    [Fact]
    public void Mutual_kills_keep_both_prepared_results_without_future_attempts()
    {
        using var fight = PreparationScenario.Create();
        fight.Rules.Damage = 1000;
        TileCombatResolveTests.Lock(fight.Server, fight.Target, fight.Attacker);
        fight.AdvanceTo(104);
        Assert.Equal(2, fight.Rules.Rolls.Count);
        Assert.Equal(2, fight.Server.PreparedCombatEventsThisTick.Count);
        Assert.All(fight.Server.PreparedCombatEventsThisTick, result => Assert.True(result.Outcome.Killed));
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Target, out _));
    }

    [Fact]
    public void Same_target_commands_keep_identity_and_buffers_are_read_only()
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        TileCombatPreparation before = fight.Preparation();
        fight.Server.Actors.Command(fight.Attacker, TileCommand.Attack(fight.Target, TileMoveMode.Walk));
        fight.Step();
        Assert.Equal(before, fight.Preparation());
        fight.AdvanceTo(104);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PreparedCombatEvent>)fight.Server.PreparedCombatEventsThisTick).Add(default));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CombatPreparationEnded>)fight.Server.EndedCombatPreparationsThisTick).Add(default));
    }

    [Fact]
    public void Disabled_mode_has_no_preparation_component_and_keeps_the_immediate_hit()
    {
        using var fight = PreparationScenario.Create(enabled: false);
        fight.Step();
        Assert.Equal(100L, Assert.Single(fight.Rules.Rolls).Tick);
        Assert.True(fight.Server.Host.TryGetOwner(fight.Attacker, out CellSim cell, out Entity e));
        Assert.False(cell.World.Has<TileCombatPreparationState>(e));
        Assert.Empty(fight.Server.PreparedCombatEventsThisTick);
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
    }

    [Theory]
    [InlineData("disengage")]
    [InlineData("profile")]
    [InlineData("rules")]
    public void An_outcome_callback_can_prevent_the_next_preparation(string change)
    {
        using var fight = PreparationScenario.Create();
        fight.Server.OnCombatEvent += outcome =>
        {
            switch (change)
            {
                case "disengage": TileCombatResolveTests.Lock(fight.Server, fight.Attacker, 0); break;
                case "profile": fight.SetProfile(new(3, 1, 9)); break;
                case "rules": fight.Server.CombatRules = null; break;
            }
        };
        fight.AdvanceTo(104);
        Assert.Single(fight.Server.PreparedCombatEventsThisTick);
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
    }

    [Fact]
    public void A_despawn_from_an_apply_callback_does_not_revoke_an_already_rolled_attack()
    {
        using var fight = PreparationScenario.Create();
        TileCombatResolveTests.Lock(fight.Server, fight.Target, fight.Attacker);
        fight.Server.OnCombatEvent += outcome =>
        {
            if (outcome.AttackerNetId == fight.Attacker) Assert.True(fight.Server.DespawnActor(fight.Target));
        };
        fight.AdvanceTo(104);
        Assert.Equal(new[] { fight.Attacker, fight.Target },
            fight.Server.PreparedCombatEventsThisTick.Select(x => x.Outcome.AttackerNetId));
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
        Assert.True(fight.Server.TryGetHealth(fight.Attacker, out TileHealth health));
        Assert.Equal(995, health.Current);
    }

    [Fact]
    public void Killing_a_target_cancels_another_attackers_future_preparation_in_the_same_tick()
    {
        using var fight = PreparationScenario.Create();
        long other = fight.Server.SpawnActor(new TileCoord(20, 22, 0), new TileActorSpawn(1000, 14, TileDirection.S));
        TileCombatResolveTests.Lock(fight.Server, other, fight.Target);
        Assert.True(fight.Server.DelayAttack(other, 5));
        fight.Rules.Damage = 1000;
        fight.Step();
        Assert.True(fight.Server.TryGetCombatPreparation(other, out TileCombatPreparation later));
        Assert.Equal(104L, later.ImpactTick);
        fight.AdvanceTo(104);
        Assert.True(Assert.Single(fight.Server.PreparedCombatEventsThisTick).Outcome.Killed);
        CombatPreparationEnded ended = Assert.Single(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal(other, ended.AttackerNetId);
        Assert.Equal(103L, ended.ServerTick);
        Assert.Equal(TileCombatPreparationEndReason.ParticipantUnavailable, ended.Reason);
        Assert.False(fight.Server.TryGetCombatPreparation(other, out _));
    }

    [Fact]
    public void Enabled_idle_participants_do_not_acquire_preparation_state()
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        Assert.True(fight.Server.Host.TryGetOwner(fight.Target, out CellSim cell, out Entity e));
        Assert.False(cell.World.Has<TileCombatPreparationState>(e));
    }

    [Fact]
    public void Identity_exhaustion_is_reported_without_creating_or_rolling_an_attempt()
    {
        using var fight = PreparationScenario.Create();
        Assert.True(fight.Server.Host.TryGetOwner(fight.Attacker, out CellSim cell, out Entity e));
        cell.World.Set(e, new TileCombatPreparationState { LastAttackId = ulong.MaxValue });
        fight.Step();
        Assert.Equal(1L, fight.Server.ExhaustedCombatPreparationIdentityCount);
        Assert.Equal(0L, fight.Server.InvalidCombatPreparationCount);
        Assert.Empty(fight.Rules.Rolls);
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
    }

    [Theory]
    [InlineData(14, 3, 103, 14)]
    [InlineData(0, 1, 101, 1)]
    public void Zero_rules_cadence_keeps_the_spawn_then_one_tick_fallback(byte spawn, byte lead, long impact, byte expectedCadence)
    {
        using var fight = PreparationScenario.Create(cadence: spawn);
        fight.Rules.Ticks = 0;
        fight.SetProfile(new(lead, 1, 7));
        fight.Step();
        Assert.Equal(impact, fight.Preparation().ImpactTick);
        Assert.Equal(expectedCadence, fight.Preparation().CadenceTicks);
    }
}
