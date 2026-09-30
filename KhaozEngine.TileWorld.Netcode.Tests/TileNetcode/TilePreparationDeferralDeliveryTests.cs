using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

// Profile 4/2 with cadence 14. H is the first attempt's scheduled impact, and every tick is relative to it.
public class TilePreparationDeferralDeliveryTests
{
    const byte Cadence = 14;

    sealed class Sequence
    {
        public readonly List<(long Tick, TileCombatPreparation? Value, TileCombatPreparationSample? Sample, double Clock)> States = new();
        public readonly List<PreparedCombatEvent> Results = new();
        public readonly List<CombatPreparationEnded> Ended = new();
        public readonly List<string> Order = new();

        public Sequence(TileWorldClient client, long attacker)
        {
            client.CombatPreparationsChanged += tick =>
            {
                TileCombatPreparation? value = client.TryGetCombatPreparation(attacker, out TileCombatPreparation held)
                    ? held : null;
                double clock = client.CombatPresentationTick;
                TileCombatPreparationSample? sample = value is { } v ? TileCombatPreparationSampler.Sample(v, clock) : null;
                States.Add((tick, value, sample, clock));
                Order.Add($"state {tick}");
            };
            client.PreparedCombatEvent += result =>
            {
                if (result.Outcome.AttackerNetId != attacker) return;
                Results.Add(result);
                Order.Add($"result {result.ImpactTick}");
            };
            client.CombatPreparationEnded += ended =>
            {
                if (ended.AttackerNetId != attacker) return;
                Ended.Add(ended);
                Order.Add($"ended {ended.ServerTick}");
            };
        }

        public (TileCombatPreparation? Value, TileCombatPreparationSample? Sample, double Clock) At(long tick)
        {
            var match = States.Where(x => x.Tick == tick).ToArray();
            Assert.Single(match);
            return (match[0].Value, match[0].Sample, match[0].Clock);
        }
    }

    static PreparationDeliveryScenario Scenario() => new(profile: new TileCombatPreparationProfile(4, 2, 7));

    // Starts the fight and serves the first preparation, returning its schedule as the client applied it.
    static TileCombatPreparation Begin(PreparationDeliveryScenario f, out long attacker, out long target)
    {
        (attacker, target) = f.Fight();
        f.Step();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out TileCombatPreparation scheduled));
        Assert.Equal((1U, 4L, (byte)2), (scheduled.Revision, scheduled.ImpactTick - scheduled.PrepareTick, scheduled.StrikeTicks));
        return scheduled;
    }

    // The target leaves legal reach for the combat pass of one tick only, as PreparationScenario.TargetAwayOn does.
    static void TargetAwayOn(PreparationDeliveryScenario f, long target, long tick)
    {
        Assert.True(f.Server.TryGetActorState(target, out TileMoveState state));
        TileCoord home = state.Tile, away = new(home.X + 1, home.Z + 1, home.Plane);
        f.Server.OnAfterMovement += _ =>
        {
            if (f.Server.TickCount == tick) f.Move(target, away);
        };
        f.Server.OnBeforeTick += _ =>
        {
            if (f.Server.TickCount == tick + 1) f.Move(target, home);
        };
    }

    [Fact]
    public void A_deferred_impact_keeps_one_client_schedule_until_its_result()
    {
        using PreparationDeliveryScenario f = Scenario();
        (long attacker, long target) = f.Fight();
        var seen = new Sequence(f.Client, attacker);
        f.Step();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out TileCombatPreparation scheduled));
        long h = scheduled.ImpactTick;
        Assert.Equal((1U, 4L), (scheduled.Revision, h - scheduled.PrepareTick));
        TargetAwayOn(f, target, h);
        TargetAwayOn(f, target, h + 1);

        f.Through(h + 2);

        Assert.Equal(Enumerable.Range(0, (int)(h + 2 - scheduled.PrepareTick) + 1).Select(i => scheduled.PrepareTick + i),
            seen.States.Select(x => x.Tick));
        for (long tick = h - 1; tick <= h + 1; tick++) Assert.Equal(scheduled, seen.At(tick).Value);
        for (long tick = h; tick <= h + 1; tick++)
        {
            (_, TileCombatPreparationSample? sample, double clock) = seen.At(tick);
            Assert.True(clock >= h);
            Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.AwaitingOutcome, 1f), sample);
        }
        PreparedCombatEvent result = Assert.Single(seen.Results);
        Assert.Equal((scheduled.AttackId, 1U, h + 2), (result.AttackId, result.Revision, result.ImpactTick));
        Assert.Empty(seen.Ended);
        Assert.All(seen.States, x => Assert.True(x.Value is not { } v || v.Revision <= 1));
        Assert.Equal(new[] { $"state {h + 2}", $"result {h + 2}" }, seen.Order.TakeLast(2));
        TileCombatPreparation successor = seen.At(h + 2).Value!.Value;
        Assert.Equal((scheduled.AttackId + 1, h + 2 + Cadence), (successor.AttackId, successor.ImpactTick));
        Assert.Equal(0, f.Client.RejectedCombatPreparationFrameCount);
    }

    [Fact]
    public void An_expired_deferral_delivers_one_illegal_reach_terminal()
    {
        using PreparationDeliveryScenario f = Scenario();
        TileCombatPreparation scheduled = Begin(f, out long attacker, out long target);
        long h = scheduled.ImpactTick;
        var seen = new Sequence(f.Client, attacker);
        for (long tick = h; tick <= h + 2; tick++) TargetAwayOn(f, target, tick);

        f.Through(h + 1);

        Assert.Empty(seen.Ended);
        Assert.Equal(scheduled, seen.At(h + 1).Value);

        f.Step();

        CombatPreparationEnded ended = Assert.Single(seen.Ended);
        Assert.Equal((TileCombatPreparationEndReason.IllegalReach, h + 2, h, scheduled.AttackId, 1U),
            (ended.Reason, ended.ServerTick, ended.ImpactTick, ended.AttackId, ended.Revision));
        Assert.Empty(seen.Results);
        Assert.Null(seen.At(h + 2).Value);
        Assert.Equal(new[] { $"state {h + 2}", $"ended {h + 2}" }, seen.Order.TakeLast(2));
        Assert.Empty(f.Rules.Rolls);
        Assert.Equal(0, f.Client.RejectedCombatPreparationFrameCount);
    }

    [Fact]
    public void Burst_delivery_of_a_deferral_applies_each_tick_in_order()
    {
        using PreparationDeliveryScenario f = Scenario();
        TileCombatPreparation scheduled = Begin(f, out long attacker, out long target);
        long h = scheduled.ImpactTick;
        TargetAwayOn(f, target, h);
        TargetAwayOn(f, target, h + 1);
        f.Through(h - 1);
        var seen = new Sequence(f.Client, attacker);

        f.Through(h + 2, poll: false);
        Assert.Empty(seen.Order);
        f.Client.Poll();

        Assert.Equal(new[] { $"state {h}", $"state {h + 1}", $"state {h + 2}", $"result {h + 2}" }, seen.Order);
        Assert.Equal(scheduled, seen.At(h).Value);
        Assert.Equal(scheduled, seen.At(h + 1).Value);
        Assert.Equal(scheduled.AttackId + 1, seen.At(h + 2).Value!.Value.AttackId);
        PreparedCombatEvent result = Assert.Single(seen.Results);
        Assert.Equal((scheduled.AttackId, 1U, h + 2), (result.AttackId, result.Revision, result.ImpactTick));
        Assert.Empty(seen.Ended);
        Assert.Equal(0, f.Client.RejectedCombatPreparationFrameCount);
    }

    [Fact]
    public void An_observer_entering_interest_during_deferral_samples_awaiting_outcome()
    {
        using PreparationDeliveryScenario f = Scenario();
        TileWorldClient observer = f.AddClient();
        f.Move(observer.LocalNetId, new TileCoord(50, 20, 0));
        f.Step();
        TileCombatPreparation scheduled = Begin(f, out long attacker, out long target);
        long h = scheduled.ImpactTick;
        TargetAwayOn(f, target, h);
        TargetAwayOn(f, target, h + 1);
        var seen = new Sequence(observer, attacker);
        f.Through(h);
        Assert.False(observer.View.Entities.ContainsKey(attacker));
        Assert.False(observer.TryGetCombatPreparation(attacker, out _));
        Assert.All(seen.States, x => Assert.Null(x.Value));

        f.Move(observer.LocalNetId, new TileCoord(25, 20, 0));
        f.Step();

        Assert.True(observer.View.Entities.ContainsKey(attacker));
        (TileCombatPreparation? entered, TileCombatPreparationSample? sample, double clock) = seen.At(h + 1);
        Assert.Equal(scheduled, entered);
        Assert.True(clock >= h);
        Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.AwaitingOutcome, 1f), sample);
        Assert.True(observer.TryGetCombatPreparation(attacker, out TileCombatPreparation held));
        Assert.Equal(TileCombatPreparationStage.AwaitingOutcome,
            TileCombatPreparationSampler.Sample(held, observer.CombatPresentationTick).Stage);
        Assert.Empty(seen.Results);

        f.Step();

        PreparedCombatEvent result = Assert.Single(seen.Results);
        Assert.Equal((scheduled.AttackId, 1U, h + 2), (result.AttackId, result.Revision, result.ImpactTick));
        Assert.Empty(seen.Ended);
        f.Step();
        Assert.Single(seen.Results);
        Assert.Equal(0, observer.RejectedCombatPreparationFrameCount);
        Assert.Equal(0, f.Client.RejectedCombatPreparationFrameCount);
    }
}
