using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationDelayTests
{
    [Fact]
    public void Initial_preparation_delay_works_with_zero_ordinary_cooldown()
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        TileCombatPreparation original = fight.Preparation();
        Assert.True(fight.Server.TryGetCombatState(fight.Attacker, out TileCombatState combat));
        Assert.Equal(0, combat.CooldownRemaining);

        Assert.True(fight.Server.DelayAttack(fight.Attacker, 3));

        TileCombatPreparation delayed = fight.Preparation();
        Assert.Equal((103L, 105L, 106L), (delayed.PrepareTick, delayed.StrikeTick, delayed.ImpactTick));
        Assert.Equal(original.AttackId, delayed.AttackId);
        Assert.Equal(2U, delayed.Revision);
        Assert.Equal(106L, Ready(fight.Server, fight.Attacker));
        fight.AdvanceTo(106);
        Assert.Empty(fight.Rules.Rolls);
        fight.Step();
        Assert.Equal(106L, Assert.Single(fight.Rules.Rolls).Tick);
        Assert.Equal(2U, Assert.Single(fight.Server.PreparedCombatEventsThisTick).Revision);
    }

    [Theory]
    [InlineData(104, 3, 120)]
    [InlineData(114, 3, 120)]
    [InlineData(116, 1, 118)]
    [InlineData(117, 1, 118)]
    public void Delay_during_hold_prepare_or_strike_adds_only_its_accepted_ticks(long at, byte delay, long expected)
    {
        using var fight = PreparationScenario.Create();
        fight.AdvanceTo(at);
        TileCombatPreparation original = fight.Preparation();

        Assert.True(fight.Server.DelayAttack(fight.Attacker, delay));

        Assert.Equal(expected, fight.Preparation().ImpactTick);
        Assert.Equal(original.AttackId, fight.Preparation().AttackId);
        Assert.Equal(2U, fight.Preparation().Revision);
        fight.AdvanceTo(expected + 1);
        Assert.Equal(new long[] { 103, expected }, fight.Rules.Rolls.Select(x => x.Tick));
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    public void Delay_during_a_deferral_revises_from_the_current_tick(byte lead, byte strike)
    {
        using var fight = PreparationScenario.Create();
        fight.SetProfile(new(lead, strike, 7));
        long h = 100 + lead;
        fight.TargetAwayOn(h);
        fight.AdvanceTo(h + 1);
        Assert.Equal((1UL, 1U, h), (fight.Preparation().AttackId, fight.Preparation().Revision, fight.Preparation().ImpactTick));

        Assert.True(fight.Server.DelayAttack(fight.Attacker, 2));

        TileCombatPreparation delayed = fight.Preparation();
        Assert.Equal((1UL, 2U, h + 3 - lead, h + 3),
            (delayed.AttackId, delayed.Revision, delayed.PrepareTick, delayed.ImpactTick));
        Assert.Equal(h + 3, Ready(fight.Server, fight.Attacker));
        fight.Step();
        Assert.Empty(fight.Rules.Rolls);
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
        fight.AdvanceTo(h + 3);
        Assert.Empty(fight.Rules.Rolls);
        fight.Step();
        Assert.Equal(h + 3, Assert.Single(fight.Rules.Rolls).Tick);
        PreparedCombatEvent result = Assert.Single(fight.Server.PreparedCombatEventsThisTick);
        Assert.Equal((h + 3, 1UL, 2U), (result.ImpactTick, result.AttackId, result.Revision));
    }

    [Fact]
    public void Delay_after_cancellation_charges_retained_readiness()
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        TileCombatResolveTests.Lock(fight.Server, fight.Attacker, 0);
        fight.Step();
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
        Assert.True(fight.Server.TryGetCombatState(fight.Attacker, out TileCombatState combat));
        Assert.Equal(0, combat.CooldownRemaining);
        Assert.Equal(103L, Ready(fight.Server, fight.Attacker));

        Assert.True(fight.Server.DelayAttack(fight.Attacker, 3));

        Assert.Equal(106L, Ready(fight.Server, fight.Attacker));
        TileCombatResolveTests.Lock(fight.Server, fight.Attacker, fight.Target);
        fight.Step();
        Assert.Equal(106L, fight.Preparation().ImpactTick);
        Assert.Equal(2UL, fight.Preparation().AttackId);
    }

    [Theory]
    [InlineData(6, 105, 102, 105)]
    [InlineData(2, 101, 100, 103)]
    public void Idle_delay_overlaps_new_preparation(byte delay, long ready, long prepare, long impact)
    {
        using var fight = PreparationScenario.Create();
        Assert.True(fight.Server.DelayAttack(fight.Attacker, delay));
        Assert.Equal(ready, Ready(fight.Server, fight.Attacker));
        fight.Step();
        Assert.Equal(prepare, fight.Preparation().PrepareTick);
        Assert.Equal(impact, fight.Preparation().ImpactTick);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Readiness_agrees_across_the_decrement_seam(bool enabled)
    {
        using var fight = PreparationScenario.Create(enabled: enabled);
        TileCombatResolveTests.Lock(fight.Server, fight.Attacker, 0);
        long other = fight.Server.SpawnActor(new TileCoord(20, 22, 0), new TileActorSpawn(1000, 14, TileDirection.S));
        TileCombatResolveTests.Lock(fight.Server, other, fight.Target);
        Assert.True(fight.Server.DelayAttack(fight.Attacker, 6));
        var before = new List<long>();
        var afterMovement = new List<long>();
        var callbacks = new List<long>();
        var between = new List<long>();
        fight.Server.OnBeforeTick += dt => before.Add(Ready(fight.Server, fight.Attacker));
        fight.Server.OnAfterMovement += dt => afterMovement.Add(Ready(fight.Server, fight.Attacker));
        fight.Server.OnCombatEvent += outcome => callbacks.Add(Ready(fight.Server, fight.Attacker));
        while (fight.Server.TickCount < 104)
        {
            fight.Step();
            between.Add(Ready(fight.Server, fight.Attacker));
        }
        Assert.Equal(4, before.Count);
        Assert.Equal(4, afterMovement.Count);
        Assert.Single(callbacks);
        Assert.Equal(4, between.Count);
        Assert.All(before.Concat(afterMovement).Concat(callbacks).Concat(between), tick => Assert.Equal(105L, tick));
    }

    [Fact]
    public void Profile_reads_see_each_participants_own_countdown_progress()
    {
        var profiles = new ObservingProfiles();
        var hub = new InMemoryTransportHub();
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var server = new TileWorldServer(hub.Server,
            TileWorldServerTickTests.Config(new TileCoord(20, 20, 0)) with { CombatPreparationRules = profiles },
            TileMoveSimulatorTests.Bake(doc));
        server.CombatRules = new PreparationScenario.FixedRules();
        while (server.TickCount < 100) server.Tick(0.25f);
        long first = server.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(1000, 14, TileDirection.N));
        long target = server.SpawnActor(new TileCoord(20, 21, 0), new TileActorSpawn(1000, 14, TileDirection.S));
        long last = server.SpawnActor(new TileCoord(20, 22, 0), new TileActorSpawn(1000, 14, TileDirection.S));
        TileCombatResolveTests.Lock(server, first, target);
        TileCombatResolveTests.Lock(server, last, target);
        Assert.True(server.DelayAttack(first, 6));
        Assert.True(server.DelayAttack(last, 6));
        var readings = new List<long>();
        profiles.Read = actor =>
        {
            readings.Add(Ready(server, first));
            readings.Add(Ready(server, last));
        };

        server.Tick(0.25f);

        Assert.Equal(new long[] { 105, 105, 105, 105 }, readings);
    }

    [Fact]
    public void Outcome_callback_delay_applies_to_the_next_attempt()
    {
        using var fight = PreparationScenario.Create();
        fight.Server.OnCombatEvent += outcome =>
        {
            if (fight.Server.TickCount != 103) return;
            Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
            Assert.Equal(117L, Ready(fight.Server, fight.Attacker));
            Assert.True(fight.Server.DelayAttack(fight.Attacker, 3));
            Assert.Equal(120L, Ready(fight.Server, fight.Attacker));
        };
        fight.AdvanceTo(104);
        Assert.Equal(103L, Assert.Single(fight.Server.PreparedCombatEventsThisTick).ImpactTick);
        Assert.Equal(120L, fight.Preparation().ImpactTick);
        fight.AdvanceTo(121);
        Assert.Equal(new long[] { 103, 120 }, fight.Rules.Rolls.Select(x => x.Tick));
    }

    [Fact]
    public void A_callback_delay_cannot_revoke_another_attack_already_rolled_in_that_tick()
    {
        using var fight = PreparationScenario.Create();
        TileCombatResolveTests.Lock(fight.Server, fight.Target, fight.Attacker);
        fight.Server.OnCombatEvent += outcome =>
        {
            if (fight.Server.TickCount != 103 || outcome.AttackerNetId != fight.Attacker) return;
            Assert.Equal(117L, Ready(fight.Server, fight.Target));
            Assert.True(fight.Server.DelayAttack(fight.Target, 3));
            Assert.Equal(120L, Ready(fight.Server, fight.Target));
            Assert.True(fight.Server.TryGetCombatPreparation(fight.Target, out TileCombatPreparation stillRolled));
            Assert.Equal(103L, stillRolled.ImpactTick);
            Assert.Equal(1U, stillRolled.Revision);
        };
        fight.AdvanceTo(104);
        Assert.Equal(2, fight.Server.PreparedCombatEventsThisTick.Count);
        Assert.All(fight.Server.PreparedCombatEventsThisTick, result => Assert.Equal(103L, result.ImpactTick));
        Assert.True(fight.Server.TryGetCombatPreparation(fight.Target, out TileCombatPreparation next));
        Assert.Equal(120L, next.ImpactTick);
        fight.AdvanceTo(121);
        Assert.Equal(new long[] { 103, 120 }, fight.Rules.Rolls.Where(x => x.AttackerNetId == fight.Target).Select(x => x.Tick));
    }

    [Fact]
    public void Repeated_delays_add_and_zero_changes_nothing()
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        Assert.True(fight.Server.DelayAttack(fight.Attacker, 3));
        Assert.True(fight.Server.DelayAttack(fight.Attacker, 2));
        Assert.Equal(108L, fight.Preparation().ImpactTick);
        Assert.Equal(3U, fight.Preparation().Revision);
        TileCombatPreparation before = fight.Preparation();
        Assert.True(fight.Server.TryGetCombatState(fight.Attacker, out TileCombatState combat));
        Assert.True(fight.Server.DelayAttack(fight.Attacker, 0));
        Assert.Equal(before, fight.Preparation());
        Assert.True(fight.Server.TryGetCombatState(fight.Attacker, out TileCombatState after));
        Assert.Equal(combat, after);
    }

    [Fact]
    public void Delay_saturates_without_identity_wrap()
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        long reference = fight.Server.TickCount;
        Assert.True(fight.Server.DelayAttack(fight.Attacker, 255));
        Assert.Equal(255L, Ready(fight.Server, fight.Attacker) - reference);
        TileCombatPreparation saturated = fight.Preparation();
        Assert.True(fight.Server.DelayAttack(fight.Attacker, 200));
        Assert.Equal(saturated, fight.Preparation());
        Assert.Equal(2U, saturated.Revision);
    }

    [Fact]
    public void Unknown_or_removed_attackers_are_refused_but_an_idle_live_player_has_a_ready_tick()
    {
        using var fight = PreparationScenario.Create();
        Assert.False(fight.Server.TryGetAttackReadyTick(long.MaxValue, out _));
        Assert.False(fight.Server.DelayAttack(long.MaxValue, 3));
        long player = fight.Server.SpawnPlayer(0, "idle", "Idle");
        Assert.Equal(100L, Ready(fight.Server, player));
        Assert.False(fight.Server.TryGetCombatState(player, out _));
        Assert.True(fight.Server.DelayAttack(player, 0));
        Assert.False(fight.Server.TryGetCombatState(player, out _));
        Assert.True(fight.Server.DespawnActor(fight.Target));
        Assert.False(fight.Server.TryGetAttackReadyTick(fight.Target, out _));
    }

    [Fact]
    public void Revision_exhaustion_replaces_identity_without_losing_delay()
    {
        TileCombatPreparationState state = ActiveState();
        state.Active = state.Active with { Revision = uint.MaxValue };
        Assert.True(TileCombatPreparationScheduler.TryDelay(ref state, 100, 3, 100,
            out byte accepted, out TilePreparationFailure failure));
        Assert.Equal(3, accepted);
        Assert.Equal(TilePreparationFailure.None, failure);
        Assert.Equal(2UL, state.Active.AttackId);
        Assert.Equal(2UL, state.LastAttackId);
        Assert.Equal(1U, state.Active.Revision);
        Assert.Equal((103L, 106L), (state.Active.PrepareTick, state.Active.ImpactTick));
    }

    [Fact]
    public void Exhausted_identity_refuses_a_revision_atomically()
    {
        TileCombatPreparationState state = ActiveState();
        state.LastAttackId = ulong.MaxValue;
        state.Active = state.Active with { AttackId = ulong.MaxValue, Revision = uint.MaxValue };
        TileCombatPreparationState before = state;
        Assert.False(TileCombatPreparationScheduler.TryDelay(ref state, 100, 3, 100,
            out byte accepted, out TilePreparationFailure failure));
        Assert.Equal(TilePreparationFailure.IdentityExhausted, failure);
        Assert.Equal(0, accepted);
        Assert.Equal(before, state);
    }

    [Fact]
    public void The_server_refuses_an_unrepresentable_identity_revision_without_charging_the_cooldown()
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        Assert.True(fight.Server.Host.TryGetOwner(fight.Attacker, out CellSim cell, out Entity e));
        Assert.True(cell.World.TryGet(e, out TileCombatPreparationState state));
        state.LastAttackId = ulong.MaxValue;
        state.Active = state.Active with { AttackId = ulong.MaxValue, Revision = uint.MaxValue };
        cell.World.Set(e, state);
        Assert.True(fight.Server.TryGetCombatState(fight.Attacker, out TileCombatState before));

        Assert.False(fight.Server.DelayAttack(fight.Attacker, 3));

        Assert.Equal(state.Active, fight.Preparation());
        Assert.Equal(103L, Ready(fight.Server, fight.Attacker));
        Assert.Equal(1L, fight.Server.ExhaustedCombatPreparationIdentityCount);
        Assert.True(fight.Server.TryGetCombatState(fight.Attacker, out TileCombatState after));
        Assert.Equal(before, after);
    }

    [Fact]
    public void A_revision_rollover_reaches_the_server_outcome_with_its_new_identity()
    {
        using var fight = PreparationScenario.Create();
        fight.Step();
        Assert.True(fight.Server.Host.TryGetOwner(fight.Attacker, out CellSim cell, out Entity e));
        Assert.True(cell.World.TryGet(e, out TileCombatPreparationState state));
        state.Active = state.Active with { Revision = uint.MaxValue };
        cell.World.Set(e, state);

        Assert.True(fight.Server.DelayAttack(fight.Attacker, 3));

        Assert.Equal(2UL, fight.Preparation().AttackId);
        Assert.Equal(1U, fight.Preparation().Revision);
        fight.AdvanceTo(107);
        PreparedCombatEvent result = Assert.Single(fight.Server.PreparedCombatEventsThisTick);
        Assert.Equal((106L, 2UL, 1U), (result.ImpactTick, result.AttackId, result.Revision));
    }

    [Fact]
    public void Unrepresentable_revised_cadence_is_refused_atomically()
    {
        TileCombatPreparationState state = ActiveState(long.MaxValue - 18);
        TileCombatPreparationState before = state;
        Assert.False(TileCombatPreparationScheduler.TryDelay(ref state, long.MaxValue - 18, 2, 0,
            out byte accepted, out TilePreparationFailure failure));
        Assert.Equal(TilePreparationFailure.TickOverflow, failure);
        Assert.Equal(0, accepted);
        Assert.Equal(before, state);
    }

    [Fact]
    public void The_pure_delay_uses_the_later_boundary_and_returns_only_effective_delay()
    {
        var state = new TileCombatPreparationState { ReadyNotBeforeTick = 103 };
        Assert.True(TileCombatPreparationScheduler.TryDelay(ref state, 100, 3, 110, out byte first, out _));
        Assert.Equal(3, first);
        Assert.Equal(113L, state.ReadyNotBeforeTick);
        Assert.True(TileCombatPreparationScheduler.TryDelay(ref state, 100, 255, 110, out byte capped, out _));
        Assert.Equal(242, capped);
        Assert.Equal(355L, state.ReadyNotBeforeTick);
        TileCombatPreparationState saturated = state;
        Assert.True(TileCombatPreparationScheduler.TryDelay(ref state, 100, 255, 110, out byte none, out _));
        Assert.Equal(0, none);
        Assert.Equal(saturated, state);
    }

    static TileCombatPreparationState ActiveState(long tick = 100)
    {
        TileCombatPreparationState state = default;
        Assert.True(TileCombatPreparationScheduler.TryCreate(ref state, tick, 10, 20,
            new(3, 1, 7), 14, tick, 0, 0, out _));
        return state;
    }

    static long Ready(TileWorldServer server, long attacker)
    {
        Assert.True(server.TryGetAttackReadyTick(attacker, out long tick));
        return tick;
    }

    sealed class ObservingProfiles : ITileCombatPreparationRules
    {
        public Action<long>? Read;
        public TileCombatPreparationProfile ProfileFor(long attackerNetId)
        {
            Read?.Invoke(attackerNetId);
            return new(3, 1, 7);
        }
    }
}
