using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationLoopbackTests
{
    [Theory]
    [InlineData(TileMoveMode.Walk, 7U)]
    [InlineData(TileMoveMode.Run, 91U)]
    public void Independent_client_clock_observes_the_server_schedule_and_real_impact(TileMoveMode mode, uint key)
    {
        var profiles = new PreparationScenario.Profiles { Current = new(3, 1, key) };
        using var h = Harness(profiles);
        var rules = new PreparationScenario.FixedRules();
        h.Server.CombatRules = rules;
        h.Frames(6);
        long player = h.Client.LocalNetId;
        Assert.True(h.Server.SetHealth(player, new TileHealth { Current = 1000, Max = 1000 }));
        long target = h.Server.SpawnActor(new TileCoord(20, 26, 0), new TileActorSpawn(1000, 14, TileDirection.S));
        long eligible = -1;
        h.Server.OnAfterMovement += _ =>
        {
            Assert.True(h.Server.TryGetPlayerState(0, out var state));
            // Independent reach observation for this open, single-tile-body fixture.
            if (eligible < 0 && state.CombatTarget == target
                && Math.Max(Math.Abs(state.Tile.X - 20), Math.Abs(state.Tile.Z - 26)) <= 1)
                eligible = h.Server.TickCount;
        };
        var authoritative = new List<PreparedCombatEvent>();
        var observed = new List<PreparedCombatEvent>();
        h.Server.OnCombatEvent += _ => authoritative.Add(h.Server.PreparedCombatEventsThisTick[^1]);
        h.Client.PreparedCombatEvent += observed.Add;
        h.Client.Queue(TileCommand.Attack(target, mode));
        TileCombatPreparation? first = null;
        for (int i = 0; i < 400 && observed.Count < 2; i++)
        {
            h.Frames(1);
            if (first is null && h.Client.TryGetCombatPreparation(player, out var preparation))
            {
                first = preparation;
                Assert.True(h.Server.TryGetCombatPreparation(player, out var server));
                Assert.Equal(server, preparation);
            }
            if (first is { } scheduled && h.Server.TickCount <= scheduled.ImpactTick)
            {
                Assert.Empty(rules.Rolls);
                Assert.Empty(authoritative);
                Assert.True(h.Server.TryGetHealth(target, out var health));
                Assert.Equal(1000, health.Current);
                Assert.True(h.Server.TryGetCombatState(player, out var attackerState));
                Assert.Equal(0, attackerState.LastCombatTick);
                Assert.True(h.Server.TryGetCombatState(target, out var targetState));
                Assert.Equal(0, targetState.LastCombatTick);
            }
        }
        Assert.NotNull(first);
        Assert.True(eligible >= 0);
        Assert.Equal(3L, first.Value.ImpactTick - eligible);
        Assert.Equal(2, observed.Count);
        Assert.Equal(authoritative, observed);
        Assert.Equal(first.Value.ImpactTick, observed[0].ImpactTick);
        Assert.Equal(14L, observed[1].ImpactTick - observed[0].ImpactTick);
        Assert.Equal(2, rules.Rolls.Count);
        Assert.Equal(rules.Rolls.Select(roll => roll.Tick), observed.Select(result => result.ImpactTick));
        Assert.All(observed, result => Assert.Equal(key, result.PresentationKey));
    }

    [Fact]
    public void Mutual_kill_and_award_callbacks_keep_one_outcome_per_roll()
    {
        using var f = new PreparationDeliveryScenario();
        var observer = f.AddClient();
        observer.Tick(.07f);
        long a = f.Client.LocalNetId, b = observer.LocalNetId;
        f.Move(b, new TileCoord(20, 21, 0));
        Assert.True(f.Server.SetHealth(a, new TileHealth { Current = 5, Max = 5 }));
        Assert.True(f.Server.SetHealth(b, new TileHealth { Current = 5, Max = 5 }));
        TileCombatResolveTests.Lock(f.Server, a, b);
        TileCombatResolveTests.Lock(f.Server, b, a);
        var awards = new List<PreparedCombatEvent>();
        var local = new List<PreparedCombatEvent>();
        var remote = new List<PreparedCombatEvent>();
        f.Server.OnCombatEvent += _ => awards.Add(f.Server.PreparedCombatEventsThisTick[^1]);
        f.Client.PreparedCombatEvent += local.Add;
        observer.PreparedCombatEvent += remote.Add;
        Frames(f, 20);
        Assert.Equal(2, f.Rules.Rolls.Count);
        Assert.Equal(2, awards.Count);
        Assert.Equal(new[] { a, b }, awards.Select(result => result.Outcome.AttackerNetId));
        Assert.Equal(awards, local);
        Assert.Equal(awards, remote);
        Assert.All(awards, result => Assert.True(result.Outcome.Killed));
        Assert.True(f.Server.TryGetHealth(a, out var hpA));
        Assert.True(f.Server.TryGetHealth(b, out var hpB));
        Assert.Equal(0, hpA.Current);
        Assert.Equal(0, hpB.Current);
        Assert.False(f.Client.TryGetCombatPreparation(a, out _));
        Assert.False(observer.TryGetCombatPreparation(b, out _));

        // The consumer resurrects the existing player bodies and clears their intent through its normal API.
        f.Server.SetPlayerState(0, TileMoveState.At(new TileCoord(20, 20, 0), TileDirection.N), teleport: true);
        f.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(20, 21, 0), TileDirection.S), teleport: true);
        Assert.True(f.Server.SetHealth(a, new TileHealth { Current = 5, Max = 5 }));
        Assert.True(f.Server.SetHealth(b, new TileHealth { Current = 5, Max = 5 }));
        Frames(f, 80);
        Assert.Equal(2, awards.Count);
        TileCombatResolveTests.Lock(f.Server, a, b);
        long restarted = f.Server.TickCount;
        Frames(f, 5);
        Assert.True(f.Client.TryGetCombatPreparation(a, out var fresh));
        Assert.Equal(2UL, fresh.AttackId);
        Assert.Equal(restarted + 3, fresh.ImpactTick);
        var ended = new List<CombatPreparationEnded>();
        f.Client.CombatPreparationEnded += ended.Add;
        f.Server.Kick(1, TileServerReason.Kicked);
        Frames(f, 5);
        Assert.False(observer.IsJoined);
        Assert.Equal(-1d, observer.CombatPresentationTick);
        Assert.False(f.Client.TryGetCombatPreparation(a, out _));
        Assert.Equal(fresh.AttackId, Assert.Single(ended).AttackId);
        Assert.Equal(2, awards.Count);
        Assert.Equal(awards, local);
    }

    [Fact]
    public void Food_revision_after_entry_survives_handoff_and_late_poll()
    {
        using var f = new PreparationDeliveryScenario(spawn: new TileCoord(63, 20, 0));
        var observer = f.AddClient();
        observer.Tick(.07f);
        (long attacker, long target) = f.Fight(new TileCoord(63, 20, 0));
        Frames(f, 5);
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var original));
        Assert.True(observer.TryGetCombatPreparation(attacker, out var entering));
        Assert.Equal(original, entering);
        Assert.True(f.Server.Host.TryGetOwner(attacker, out var initialOwner, out _));
        Assert.Equal(new CellCoord(0, 0), initialOwner.Coord);
        Assert.True(f.Server.DelayAttack(attacker, 5));
        Assert.True(f.Server.TryGetCombatPreparation(attacker, out var revised));
        Assert.Equal(original.AttackId, revised.AttackId);
        Assert.Equal(2U, revised.Revision);
        Assert.Equal(original.ImpactTick + 5, revised.ImpactTick);
        Assert.Equal(revised.ImpactTick - 3, revised.PrepareTick);
        f.Move(target, new TileCoord(65, 20, 0));
        var authoritative = new List<PreparedCombatEvent>();
        var local = new List<PreparedCombatEvent>();
        var remote = new List<PreparedCombatEvent>();
        long migratedTick = f.Server.TickCount;
        bool localSawMigratedRevision = false, observerSawMigratedRevision = false;
        f.Client.CombatPreparationsChanged += tick =>
        {
            if (tick != migratedTick) return;
            AssertMigratedRevision(f.Client);
            localSawMigratedRevision = true;
        };
        observer.CombatPreparationsChanged += tick =>
        {
            if (tick != migratedTick) return;
            AssertMigratedRevision(observer);
            observerSawMigratedRevision = true;
        };
        f.Server.OnCombatEvent += _ => authoritative.Add(f.Server.PreparedCombatEventsThisTick[^1]);
        f.Client.PreparedCombatEvent += result =>
        {
            Assert.True(localSawMigratedRevision);
            local.Add(result);
        };
        observer.PreparedCombatEvent += result =>
        {
            Assert.True(observerSawMigratedRevision);
            remote.Add(result);
        };
        Frames(f, 5, poll: false);
        Assert.True(f.Server.Host.TryGetOwner(attacker, out var migrated, out _));
        Assert.Equal(new CellCoord(1, 0), migrated.Coord);
        Assert.True(f.Server.TryGetCombatPreparation(attacker, out var afterHandoff));
        Assert.Equal(revised, afterHandoff);
        while (f.Server.TickCount <= original.ImpactTick) Frames(f, 5, poll: false);
        Assert.Empty(authoritative);
        while (f.Server.TickCount <= revised.ImpactTick) Frames(f, 5, poll: false);
        Assert.Empty(local);
        Assert.Empty(remote);
        Assert.False(localSawMigratedRevision);
        Assert.False(observerSawMigratedRevision);
        Assert.Equal(revised.ImpactTick, Assert.Single(authoritative).ImpactTick);
        f.Client.Poll();
        observer.Poll();
        Assert.Equal(authoritative, local);
        Assert.Equal(authoritative, remote);
        Assert.Equal(revised.AttackId, local[0].AttackId);
        Assert.Equal(revised.Revision, local[0].Revision);
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var successor));
        Assert.Equal(revised.AttackId + 1, successor.AttackId);

        void AssertMigratedRevision(TileWorldClient client)
        {
            Assert.True(client.TryGetCombatPreparation(attacker, out var applied));
            Assert.Equal(revised.AttackId, applied.AttackId);
            Assert.Equal(revised.Revision, applied.Revision);
            Assert.Equal(revised.PrepareTick, applied.PrepareTick);
            Assert.Equal(revised.ImpactTick, applied.ImpactTick);
        }
    }

    [Fact]
    public void Restored_rules_require_new_preparation_after_null_cancellation()
    {
        using var f = new PreparationDeliveryScenario();
        (long attacker, long target) = f.Fight();
        Frames(f, 5);
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var initial));
        var ended = new List<CombatPreparationEnded>();
        var awards = new List<PreparedCombatEvent>();
        var observed = new List<PreparedCombatEvent>();
        f.Client.CombatPreparationEnded += ended.Add;
        f.Server.OnCombatEvent += _ => awards.Add(f.Server.PreparedCombatEventsThisTick[^1]);
        f.Client.PreparedCombatEvent += observed.Add;
        f.Server.CombatRules = null;
        Frames(f, 30);
        Assert.Equal(TileCombatPreparationEndReason.RulesUnavailable, Assert.Single(ended).Reason);
        Assert.Equal(initial.AttackId, ended[0].AttackId);
        Assert.Empty(f.Rules.Rolls);
        Assert.Empty(awards);
        Assert.True(f.Server.TryGetHealth(target, out var health));
        Assert.Equal(1000, health.Current);
        Assert.False(f.Client.TryGetCombatPreparation(attacker, out _));
        f.Server.CombatRules = f.Rules;
        long eligible = f.Server.TickCount;
        Frames(f, 5);
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var restored));
        Assert.Equal(eligible + 3, restored.ImpactTick);
        Assert.Equal(initial.AttackId + 1, restored.AttackId);
        while (f.Server.TickCount <= restored.ImpactTick) Frames(f, 5);
        Assert.Equal(restored.ImpactTick, Assert.Single(observed).ImpactTick);
        Assert.Equal(awards, observed);
    }

    [Fact]
    public void Equipment_change_replaces_motion_identity_before_the_first_result()
    {
        var profiles = new PreparationScenario.Profiles();
        using var h = Harness(profiles);
        h.Server.CombatRules = new PreparationScenario.FixedRules();
        h.Frames(6);
        Assert.True(h.Server.SetHealth(h.Client.LocalNetId, new TileHealth { Current = 1000, Max = 1000 }));
        long target = h.Server.SpawnActor(new TileCoord(20, 21, 0), new TileActorSpawn(1000, 14, TileDirection.S));
        h.Client.Queue(TileCommand.Attack(target, TileMoveMode.Run));
        h.Frames(5);
        Assert.True(h.Client.TryGetCombatPreparation(h.Client.LocalNetId, out var original));
        var ended = new List<CombatPreparationEnded>();
        var results = new List<PreparedCombatEvent>();
        h.Client.CombatPreparationEnded += ended.Add;
        h.Client.PreparedCombatEvent += results.Add;
        profiles.Current = new(3, 1, 99);
        h.Frames(5);
        Assert.True(h.Client.TryGetCombatPreparation(h.Client.LocalNetId, out var replacement));
        Assert.Equal(original.AttackId + 1, replacement.AttackId);
        Assert.Equal(99U, replacement.PresentationKey);
        Assert.Equal(TileCombatPreparationEndReason.ProfileChanged, Assert.Single(ended).Reason);
        Assert.Equal(7U, ended[0].PresentationKey);
        Assert.Empty(results);
        for (int i = 0; i < 30 && results.Count == 0; i++) h.Frames(1);
        Assert.Equal(replacement.AttackId, Assert.Single(results).AttackId);
        Assert.Equal(99U, results[0].PresentationKey);
    }

    [Theory]
    [InlineData(2, 4L)]
    [InlineData(20, 20L)]
    public void Idle_food_wait_overlaps_preparation_in_the_loopback(byte delay, long expectedImpact)
    {
        using var f = new PreparationDeliveryScenario();
        Assert.Equal(1L, f.Server.TickCount);
        (long attacker, long target) = f.Fight();
        Assert.True(f.Server.DelayAttack(attacker, delay));
        var observed = new List<PreparedCombatEvent>();
        f.Client.PreparedCombatEvent += observed.Add;
        Frames(f, 5);
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var preparation));
        Assert.Equal(expectedImpact, preparation.ImpactTick);
        while (f.Server.TickCount < expectedImpact) Frames(f, 5);
        Assert.Empty(observed);
        Assert.Empty(f.Rules.Rolls);
        Assert.True(f.Server.TryGetHealth(target, out var health));
        Assert.Equal(1000, health.Current);
        Frames(f, 5);
        Assert.Equal(expectedImpact, Assert.Single(observed).ImpactTick);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Preparation_does_not_extend_logout_beyond_the_existing_lock_rule(bool holdLock, int remainingPlayers)
    {
        using var h = Harness(new PreparationScenario.Profiles());
        h.Server.CombatRules = new PreparationScenario.FixedRules();
        h.Frames(6);
        Assert.True(h.Server.SetHealth(h.Client.LocalNetId, new TileHealth { Current = 1000, Max = 1000 }));
        long target = h.Server.SpawnActor(new TileCoord(20, 21, 0), new TileActorSpawn(1000, 14, TileDirection.S));
        h.Client.Queue(TileCommand.Attack(target, TileMoveMode.Run));
        h.Frames(5);
        Assert.True(h.Client.TryGetCombatPreparation(h.Client.LocalNetId, out _));
        Assert.True(h.Server.TryGetCombatState(h.Client.LocalNetId, out var combat));
        Assert.Equal(0L, combat.LastCombatTick);
        if (!holdLock)
        {
            Assert.True(h.Server.TryGetPlayerState(0, out var state));
            state.CombatTarget = 0;
            h.Server.SetPlayerState(0, state);
        }
        int awards = 0;
        h.Server.OnCombatEvent += _ => awards++;
        h.Drop();
        h.Frames(1);
        Assert.Equal(remainingPlayers, h.Server.PlayerCount);
        Assert.Equal(0, awards);
        Assert.False(h.Client.IsJoined);
    }

    static TileCombatHarness Harness(PreparationScenario.Profiles profiles)
    {
        var spawn = new TileCoord(20, 20, 0);
        return new(TileMoveSimulatorTests.FlatWorld(), spawn,
            config: TileWorldServerTickTests.Config(spawn) with { CombatPreparationRules = profiles, CombatLogoutTicks = 50 },
            combatPreparationEnabled: true);
    }

    static void Frames(PreparationDeliveryScenario scenario, int frames, bool poll = true)
    {
        Assert.Equal(0, frames % 5);
        for (int i = 0; i < frames; i++)
        {
            foreach (var client in scenario.Clients) client.Tick(.05f);
            scenario.Server.Poll();
            if ((i + 1) % 5 == 0) scenario.Step(poll: false);
            foreach (var client in scenario.Clients)
            {
                if (poll) client.Poll();
                client.AdvancePresentation(.05f);
            }
        }
    }
}
