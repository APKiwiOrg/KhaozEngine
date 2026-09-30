using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

// Every scenario locks at tick 100, so the first impact H is 100 plus the lead. Ticks are relative to H.
public class TilePreparationDeferralTests
{
    static PreparationScenario Fight(byte lead, byte strike, out long impact)
    {
        PreparationScenario fight = PreparationScenario.Create();
        fight.SetProfile(new(lead, strike, 7));
        impact = 100 + lead;
        return fight;
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    public void Out_of_reach_impact_defers_and_resolves_on_the_next_legal_tick(byte lead, byte strike)
    {
        using PreparationScenario fight = Fight(lead, strike, out long h);
        fight.TargetAwayOn(h);
        fight.AdvanceTo(h + 1);
        Assert.Empty(fight.Rules.Rolls);
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal(h, fight.Preparation().ImpactTick);

        fight.Step();

        Assert.Equal(h + 1, Assert.Single(fight.Rules.Rolls).Tick);
        PreparedCombatEvent result = Assert.Single(fight.Server.PreparedCombatEventsThisTick);
        Assert.Equal((h + 1, 1UL, 1U), (result.ImpactTick, result.AttackId, result.Revision));
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
        TileCombatPreparation next = fight.Preparation();
        Assert.Equal((h + 15 - lead, h + 15), (next.PrepareTick, next.ImpactTick));
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    public void A_deferred_tick_makes_no_roll_revision_or_terminal(byte lead, byte strike)
    {
        using PreparationScenario fight = Fight(lead, strike, out long h);
        for (long tick = h; tick < h + strike; tick++) fight.TargetAwayOn(tick);
        int outcomes = 0;
        fight.Server.OnCombatEvent += _ => outcomes++;
        fight.AdvanceTo(h);
        TileCombatPreparation scheduled = fight.Preparation();
        Assert.True(fight.Server.TryGetCombatState(fight.Attacker, out TileCombatState before));

        for (long tick = h; tick < h + strike; tick++)
        {
            fight.Step();
            Assert.Empty(fight.Rules.Rolls);
            Assert.Equal(0, outcomes);
            Assert.Empty(fight.Server.PreparedCombatEventsThisTick);
            Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
            Assert.True(fight.Server.TryGetCombatState(fight.Attacker, out TileCombatState combat));
            Assert.Equal(0, combat.CooldownRemaining);
            Assert.Equal(before.LastCombatTick, combat.LastCombatTick);
            Assert.Equal(scheduled, fight.Preparation());
            Assert.True(fight.Server.TryGetAttackReadyTick(fight.Attacker, out long ready));
            Assert.Equal(fight.Server.TickCount, ready);
        }
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    public void An_escaping_target_ends_the_attempt_after_its_strike_ticks(byte lead, byte strike)
    {
        using PreparationScenario fight = Fight(lead, strike, out long h);
        for (long tick = h; tick <= h + strike; tick++) fight.TargetAwayOn(tick);
        while (fight.Server.TickCount < h + strike)
        {
            fight.Step();
            Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
        }

        fight.Step();

        CombatPreparationEnded ended = Assert.Single(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal(TileCombatPreparationEndReason.IllegalReach, ended.Reason);
        Assert.Equal((h + strike, h, 1UL), (ended.ServerTick, ended.ImpactTick, ended.AttackId));
        Assert.Empty(fight.Rules.Rolls);
        Assert.True(fight.Server.TryGetCombatState(fight.Attacker, out TileCombatState combat));
        Assert.Equal(0, combat.CooldownRemaining);
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));

        fight.Step();

        TileCombatPreparation fresh = fight.Preparation();
        Assert.Equal((2UL, h + strike + 1, h + strike + 1 + lead), (fresh.AttackId, fresh.PrepareTick, fresh.ImpactTick));
    }

    [Theory]
    [InlineData("disengage", TileCombatPreparationEndReason.Disengaged)]
    [InlineData("retarget", TileCombatPreparationEndReason.TargetChanged)]
    [InlineData("profile", TileCombatPreparationEndReason.ProfileChanged)]
    [InlineData("invalid", TileCombatPreparationEndReason.InvalidProfile)]
    [InlineData("permission", TileCombatPreparationEndReason.PermissionRevoked)]
    [InlineData("target-dead", TileCombatPreparationEndReason.ParticipantUnavailable)]
    [InlineData("attacker-dead", TileCombatPreparationEndReason.ParticipantUnavailable)]
    [InlineData("teleport", TileCombatPreparationEndReason.Teleport)]
    [InlineData("rules-null", TileCombatPreparationEndReason.RulesUnavailable)]
    public void Non_reach_invalidity_ends_a_deferred_attempt_immediately(string change,
        TileCombatPreparationEndReason reason)
    {
        using PreparationScenario fight = Fight(4, 2, out long h);
        fight.TargetAwayOn(h);
        fight.TargetAwayOn(h + 1);
        fight.AdvanceTo(h + 1);
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal(h, fight.Preparation().ImpactTick);
        long next = Apply(fight, change);

        fight.Step();

        CombatPreparationEnded ended = Assert.Single(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal((reason, h + 1, h), (ended.Reason, ended.ServerTick, ended.ImpactTick));
        Assert.Empty(fight.Rules.Rolls);
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
        if (change != "retarget") return;

        fight.Step();

        TileCombatPreparation replacement = fight.Preparation();
        Assert.Equal((next, h + 2), (replacement.TargetNetId, replacement.PrepareTick));
    }

    [Theory]
    [InlineData("disengage", TileCombatPreparationEndReason.Disengaged)]
    [InlineData("retarget", TileCombatPreparationEndReason.TargetChanged)]
    [InlineData("profile", TileCombatPreparationEndReason.ProfileChanged)]
    [InlineData("invalid", TileCombatPreparationEndReason.InvalidProfile)]
    [InlineData("permission", TileCombatPreparationEndReason.PermissionRevoked)]
    [InlineData("target-dead", TileCombatPreparationEndReason.ParticipantUnavailable)]
    [InlineData("attacker-dead", TileCombatPreparationEndReason.ParticipantUnavailable)]
    [InlineData("teleport", TileCombatPreparationEndReason.Teleport)]
    [InlineData("rules-null", TileCombatPreparationEndReason.RulesUnavailable)]
    public void Non_reach_invalidity_on_the_impact_tick_wins_over_deferral(string change,
        TileCombatPreparationEndReason reason)
    {
        using PreparationScenario fight = Fight(4, 2, out long h);
        fight.TargetAwayOn(h);
        fight.AdvanceTo(h);
        Apply(fight, change);

        fight.Step();

        CombatPreparationEnded ended = Assert.Single(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal((reason, h, h), (ended.Reason, ended.ServerTick, ended.ImpactTick));
        Assert.Empty(fight.Rules.Rolls);
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
    }

    [Fact]
    public void A_plane_mismatch_at_impact_defers_as_reach()
    {
        using PreparationScenario fight = Fight(4, 2, out long h);
        fight.TargetAwayOn(h, new TileCoord(20, 21, 1));
        fight.AdvanceTo(h + 1);
        Assert.Empty(fight.Rules.Rolls);
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal(h, fight.Preparation().ImpactTick);

        fight.Step();

        Assert.Equal(h + 1, Assert.Single(fight.Rules.Rolls).Tick);
        Assert.Equal(h + 1, Assert.Single(fight.Server.PreparedCombatEventsThisTick).ImpactTick);
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    public void A_deferral_survives_the_post_roll_recheck_of_its_own_tick(byte lead, byte strike)
    {
        using PreparationScenario fight = Fight(lead, strike, out long h);
        long other = fight.Server.SpawnActor(new TileCoord(19, 20, 0), new TileActorSpawn(1000, 14, TileDirection.E));
        TileCombatResolveTests.Lock(fight.Server, other, fight.Attacker);
        fight.TargetAwayOn(h);
        fight.AdvanceTo(h + 1);
        // The other attacker's roll on H is what runs the post-roll recheck over the deferred attempt.
        Assert.Equal(new[] { (other, h) }, fight.Rules.Rolls.Select(x => (x.AttackerNetId, x.Tick)));
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
        TileCombatPreparation held = fight.Preparation();
        Assert.Equal((1UL, 1U, h), (held.AttackId, held.Revision, held.ImpactTick));

        fight.Step();

        Assert.Equal(new[] { h + 1 },
            fight.Rules.Rolls.Where(x => x.AttackerNetId == fight.Attacker).Select(x => x.Tick));
        PreparedCombatEvent result = Assert.Single(fight.Server.PreparedCombatEventsThisTick);
        Assert.Equal((fight.Attacker, h + 1, 1UL), (result.Outcome.AttackerNetId, result.ImpactTick, result.AttackId));
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
    }

    [Fact]
    public void A_target_killed_during_deferral_ends_it_in_that_tick()
    {
        using PreparationScenario fight = Fight(4, 2, out long h);
        fight.Rules.Damage = 1000;
        long killer = fight.Server.SpawnActor(new TileCoord(21, 21, 0), new TileActorSpawn(1000, 14, TileDirection.W));
        fight.TargetAwayOn(h);
        fight.TargetAwayOn(h + 1);
        fight.Step();
        // Locked one tick later than the first attacker, so its impact is H + 1.
        TileCombatResolveTests.Lock(fight.Server, killer, fight.Target);
        fight.AdvanceTo(h + 1);
        Assert.Empty(fight.Rules.Rolls);
        Assert.Equal(h, fight.Preparation().ImpactTick);
        Assert.True(fight.Server.TryGetCombatPreparation(killer, out TileCombatPreparation lethal));
        Assert.Equal(h + 1, lethal.ImpactTick);

        fight.Step();

        PreparedCombatEvent kill = Assert.Single(fight.Server.PreparedCombatEventsThisTick);
        Assert.Equal(killer, kill.Outcome.AttackerNetId);
        Assert.True(kill.Outcome.Killed);
        CombatPreparationEnded ended = Assert.Single(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal((fight.Attacker, TileCombatPreparationEndReason.ParticipantUnavailable, h + 1, h),
            (ended.AttackerNetId, ended.Reason, ended.ServerTick, ended.ImpactTick));
        Assert.DoesNotContain(fight.Rules.Rolls, x => x.AttackerNetId == fight.Attacker);
        Assert.False(fight.Server.TryGetCombatPreparation(fight.Attacker, out _));
    }

    [Fact]
    public void A_deferral_survives_a_real_region_handoff_and_resolves_in_the_new_cell()
    {
        using PreparationScenario fight = PreparationScenario.Create();
        const long h = 103;
        fight.SetPosition(fight.Attacker, new TileCoord(63, 20, 0));
        fight.SetTargetPosition(new TileCoord(63, 21, 0));
        fight.Server.OnAfterMovement += _ =>
        {
            if (fight.Server.TickCount == h) fight.SetTargetPosition(new TileCoord(65, 20, 0));
        };
        fight.AdvanceTo(h + 1);
        Assert.Empty(fight.Rules.Rolls);
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
        Assert.True(fight.Server.Host.TryGetOwner(fight.Attacker, out CellSim before, out Entity held));
        Assert.Equal(new CellCoord(0, 0), before.Coord);
        Assert.True(before.World.TryGet(held, out TileCombatPreparationState deferred));
        Assert.Equal(h, deferred.DeferredTick);

        fight.Step();

        Assert.True(fight.Server.Host.TryGetOwner(fight.Attacker, out CellSim after, out _));
        Assert.Equal(new CellCoord(1, 0), after.Coord);
        Assert.Equal(h + 1, Assert.Single(fight.Rules.Rolls).Tick);
        PreparedCombatEvent result = Assert.Single(fight.Server.PreparedCombatEventsThisTick);
        Assert.Equal((h + 1, 1UL, 1U), (result.ImpactTick, result.AttackId, result.Revision));
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
    }

    [Fact]
    public void An_overdue_attempt_that_was_not_deferred_ends_unavailable()
    {
        using PreparationScenario fight = PreparationScenario.Create();
        const long h = 103;
        int outcomes = 0;
        fight.Server.OnCombatEvent += _ => outcomes++;
        // Valid but not due on H, so the attempt is neither resolved nor deferred there: a missed pass.
        fight.Server.OnAfterMovement += _ =>
        {
            if (fight.Server.TickCount != h) return;
            fight.Write<TileCombatState>(fight.Attacker, combat =>
            {
                combat.CooldownRemaining = 2;
                return combat;
            });
        };
        fight.AdvanceTo(h + 1);
        Assert.Empty(fight.Rules.Rolls);
        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal(h, fight.Preparation().ImpactTick);

        fight.Step();

        CombatPreparationEnded ended = Assert.Single(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal((TileCombatPreparationEndReason.ParticipantUnavailable, h + 1, h),
            (ended.Reason, ended.ServerTick, ended.ImpactTick));
        Assert.Empty(fight.Rules.Rolls);
        Assert.Empty(fight.Server.PreparedCombatEventsThisTick);
        Assert.Equal(0, outcomes);
    }

    [Fact]
    public void An_attempt_deferred_before_the_previous_pass_ends_unavailable()
    {
        using PreparationScenario fight = Fight(4, 2, out long h);
        int outcomes = 0;
        fight.Server.OnCombatEvent += _ => outcomes++;
        fight.TargetAwayOn(h);
        // Reach is legal again on H + 1, but a forced cooldown keeps the attempt from being due there, so H + 1
        // neither resolves nor defers it. The cooldown runs out on H + 2, where reach is still legal.
        fight.Server.OnAfterMovement += _ =>
        {
            if (fight.Server.TickCount != h + 1) return;
            fight.Write<TileCombatState>(fight.Attacker, combat =>
            {
                combat.CooldownRemaining = 2;
                return combat;
            });
        };
        fight.AdvanceTo(h + 1);
        Assert.True(fight.Server.Host.TryGetOwner(fight.Attacker, out CellSim cell, out Entity e));
        Assert.True(cell.World.TryGet(e, out TileCombatPreparationState deferred));
        Assert.Equal(h, deferred.DeferredTick);

        fight.Step();

        Assert.Empty(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Empty(fight.Server.PreparedCombatEventsThisTick);
        Assert.Equal(h, fight.Preparation().ImpactTick);

        fight.Step();

        CombatPreparationEnded ended = Assert.Single(fight.Server.EndedCombatPreparationsThisTick);
        Assert.Equal((TileCombatPreparationEndReason.ParticipantUnavailable, h + 2, h, 1UL),
            (ended.Reason, ended.ServerTick, ended.ImpactTick, ended.AttackId));
        Assert.Empty(fight.Rules.Rolls);
        Assert.Empty(fight.Server.PreparedCombatEventsThisTick);
        Assert.Equal(0, outcomes);
    }

    static long Apply(PreparationScenario fight, string change)
    {
        switch (change)
        {
            case "disengage": TileCombatResolveTests.Lock(fight.Server, fight.Attacker, 0); break;
            case "retarget":
                long next = fight.Server.SpawnActor(new TileCoord(20, 19, 0), new TileActorSpawn(1000, 14, TileDirection.N));
                TileCombatResolveTests.Lock(fight.Server, fight.Attacker, next);
                return next;
            case "profile": fight.SetProfile(new(4, 2, 9)); break;
            case "invalid": fight.SetProfile(new(0, 2, 7)); break;
            case "permission": fight.Rules.Allowed = false; break;
            case "target-dead": fight.Server.SetHealth(fight.Target, new TileHealth { Max = 1000, Current = 0 }); break;
            case "attacker-dead": fight.Server.SetHealth(fight.Attacker, new TileHealth { Max = 1000, Current = 0 }); break;
            case "teleport":
                fight.Write<TileMoveState>(fight.Attacker, state =>
                {
                    state.Epoch++;
                    return state;
                });
                break;
            case "rules-null": fight.Server.CombatRules = null; break;
        }
        return 0;
    }
}
