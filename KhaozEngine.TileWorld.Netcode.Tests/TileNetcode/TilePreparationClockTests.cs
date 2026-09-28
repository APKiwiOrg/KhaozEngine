using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationClockTests
{
    [Fact]
    public void Clock_is_monotonic_bounded_and_reset_on_disconnect()
    {
        var clock = new TileCombatPresentationClock();
        Assert.Equal(-1d, clock.Tick);
        clock.Advance(1, .25f);
        clock.Observe(-1);
        Assert.Equal(-1d, clock.Tick);
        clock.Observe(100);
        clock.Advance(.125f, .25f);
        Assert.Equal(100.5d, clock.Tick);
        clock.Observe(100);
        clock.Observe(99);
        Assert.Equal(100.5d, clock.Tick);
        clock.Advance(float.MaxValue, .25f);
        Assert.Equal(101d, clock.Tick);
        clock.Observe(101);
        Assert.Equal(101d, clock.Tick);
        clock.Advance(.125f, .25f);
        clock.Observe(104);
        Assert.Equal(104d, clock.Tick);
        clock.Clear();
        Assert.Equal(-1d, clock.Tick);
        clock.Observe(0);
        Assert.Equal(0d, clock.Tick);

        using var f = new PreparationDeliveryScenario();
        f.Client.AdvancePresentation(.125f);
        Assert.Equal(.5d, f.Client.CombatPresentationTick);
        bool disconnected = false;
        f.Client.Disconnected += () =>
        {
            disconnected = true;
            Assert.Equal(-1d, f.Client.CombatPresentationTick);
        };
        f.Server.Kick(0, TileServerReason.Kicked);
        f.Client.Poll();
        f.Client.AdvancePresentation(1);
        Assert.True(disconnected);
        Assert.Equal(-1d, f.Client.CombatPresentationTick);
        var reconnected = f.AddClient();
        Assert.Equal(f.Server.TickCount - 1d, reconnected.CombatPresentationTick);
    }

    [Theory]
    [InlineData(.25f)]
    [InlineData(1f / 6f)]
    [InlineData(.1f)]
    public void Fractional_progress_uses_the_configured_tick_length(float tickSeconds)
    {
        var clock = new TileCombatPresentationClock();
        clock.Observe(100);
        clock.Advance(tickSeconds / 2, tickSeconds);
        Assert.Equal(100.5d, clock.Tick, 6);
        clock.Advance(tickSeconds * 10, tickSeconds);
        Assert.Equal(101d, clock.Tick);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-1f)]
    [InlineData(0f)]
    public void Invalid_elapsed_time_advances_neither_clock_nor_client(float dt)
    {
        var clock = new TileCombatPresentationClock();
        clock.Observe(100);
        clock.Advance(dt, .25f);
        Assert.Equal(100d, clock.Tick);
        using var f = new PreparationDeliveryScenario();
        f.Client.AdvancePresentation(dt);
        Assert.Equal(0d, f.Client.CombatPresentationTick);
    }

    [Fact]
    public void Client_stays_unanchored_until_a_valid_applied_snapshot_and_resets_on_dispose()
    {
        var spawn = new TileCoord(20, 20, 0);
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), spawn,
            config: TileWorldServerTickTests.Config(spawn) with { CombatPreparationRules = new PreparationScenario.Profiles() },
            combatPreparationEnabled: true);
        Assert.Equal(-1d, h.Client.CombatPresentationTick);
        h.Client.AdvancePresentation(1);
        Assert.Equal(-1d, h.Client.CombatPresentationTick);
        h.Frames(6);
        Assert.InRange(h.Client.CombatPresentationTick, 0d, 1d);
        h.Client.Dispose();
        Assert.Equal(-1d, h.Client.CombatPresentationTick);
    }

    [Fact]
    public void Older_or_malformed_snapshots_do_not_move_the_combat_anchor()
    {
        using var f = new PreparationDeliveryScenario();
        byte[] old = f.Payloads().First(x => x[0] == 0);
        f.Step();
        f.Client.AdvancePresentation(.125f);
        Assert.Equal(1.5d, f.Client.CombatPresentationTick);
        f.Inject(old);
        f.Client.Poll();
        Assert.Equal(1.5d, f.Client.CombatPresentationTick);
        f.Inject(TileProtocol.EncodeSnapshotFrame(f.Client.LocalNetId, 0, 500, Array.Empty<byte>()));
        f.Client.Poll();
        Assert.Equal(1, f.Client.DroppedSnapshotCount);
        Assert.Equal(1.5d, f.Client.CombatPresentationTick);
    }

    [Fact]
    public void Preparation_frames_do_not_anchor_the_clock_without_a_movement_snapshot()
    {
        using var f = new PreparationDeliveryScenario();
        f.Client.AdvancePresentation(.125f);
        var preparation = new TileCombatPreparation(100, 101, 1, 1, 7, 100, 103, 1, 14);
        f.Inject(TileProtocol.EncodePreparationChunk(new(100, 0, 1), new[] { preparation }, 0, 1));
        f.Client.Poll();
        Assert.Equal(.5d, f.Client.CombatPresentationTick);
    }

    [Fact]
    public void Late_samples_enter_the_current_stage_without_creating_an_outcome()
    {
        using var f = new PreparationDeliveryScenario();
        (long attacker, _) = f.Fight();
        f.Step();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var preparation));
        var generatedOutcomes = new List<PreparedCombatEvent>();
        f.Client.PreparedCombatEvent += generatedOutcomes.Add;
        Assert.Equal(TileCombatPreparationStage.Strike,
            TileCombatPreparationSampler.Sample(preparation, preparation.ImpactTick - .5d).Stage);
        Assert.Equal(TileCombatPreparationStage.AwaitingOutcome,
            TileCombatPreparationSampler.Sample(preparation, preparation.ImpactTick + 100d).Stage);
        f.Client.AdvancePresentation(float.MaxValue);
        Assert.Empty(generatedOutcomes);
        f.Through(preparation.ImpactTick, poll: false);
        Assert.Empty(generatedOutcomes);
        f.Client.Poll();
        Assert.Equal((double)preparation.ImpactTick, f.Client.CombatPresentationTick);
        Assert.Equal(preparation.AttackId, Assert.Single(generatedOutcomes).AttackId);
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var successor));
        Assert.Equal(TileCombatPreparationStage.Hold,
            TileCombatPreparationSampler.Sample(successor, f.Client.CombatPresentationTick).Stage);
        foreach (byte[] frame in f.Payloads().Where(x => x[0] == 5)) f.Inject(frame);
        f.Client.Poll();
        Assert.Single(generatedOutcomes);
    }

    [Theory]
    [InlineData(9007199254740994L, 9007199254740994d)]
    [InlineData(long.MaxValue - 14, 9223372036854774784d)]
    public void Rounded_clock_never_exceeds_the_exact_one_tick_bound(long anchor, double expected)
    {
        var clock = new TileCombatPresentationClock();
        clock.Observe(anchor);
        clock.Advance(.25f, .25f);
        Assert.Equal(expected, clock.Tick);
        Assert.True(new BigInteger(clock.Tick) <= new BigInteger(anchor) + 1);
        clock.Advance(float.MaxValue, .25f);
        Assert.Equal(expected, clock.Tick);
    }

    [Theory]
    [InlineData(9007199254740988L)]
    [InlineData(long.MaxValue - 15)]
    public void Adjacent_large_anchors_remain_monotonic_and_mathematically_bounded(long first)
    {
        var clock = new TileCombatPresentationClock();
        double previous = -1d;
        for (int i = 0; i < 16; i++)
        {
            long anchor = first + i;
            clock.Observe(anchor);
            Assert.True(clock.Tick >= previous);
            Assert.True(new BigInteger(clock.Tick) <= new BigInteger(anchor) + 1);
            previous = clock.Tick;
            clock.Advance(.125f, .25f);
            Assert.True(clock.Tick >= previous);
            Assert.True(new BigInteger(clock.Tick) <= new BigInteger(anchor) + 1);
            previous = clock.Tick;
            clock.Advance(.125f, .25f);
            Assert.True(clock.Tick >= previous);
            Assert.True(new BigInteger(clock.Tick) <= new BigInteger(anchor) + 1);
            previous = clock.Tick;
            clock.Observe(anchor - 1);
            clock.Observe(anchor);
            Assert.Equal(previous, clock.Tick);
        }
    }

    [Theory]
    [InlineData(9007199254740994L, TileCombatPreparationStage.Prepare)]
    [InlineData(long.MaxValue - 14, TileCombatPreparationStage.Hold)]
    public void Large_client_clock_does_not_sample_an_impact_two_ticks_away(long anchor, TileCombatPreparationStage expected)
    {
        using var f = new PreparationDeliveryScenario();
        byte[] initial = f.Payloads().First(x => x[0] == TileProtocol.ServerFrameSnapshot);
        Assert.True(TileProtocol.TryDecodeSnapshotFrame(initial, out long local, out int ack, out _, out byte[] body));
        var preparation = new TileCombatPreparation(100, 101, 1, 1, 7, anchor, anchor + 2, 1, 2);
        f.Inject(TileProtocol.EncodeSnapshotFrame(local, ack, anchor, body));
        f.Inject(TileProtocol.EncodePreparationChunk(new(anchor, 0, 1), new[] { preparation }, 0, 1));
        f.Client.Poll();
        f.Client.AdvancePresentation(.25f);
        Assert.True(f.Client.TryGetCombatPreparation(100, out var received));
        Assert.Equal(new TileCombatPreparationSample(expected, 0),
            TileCombatPreparationSampler.Sample(received, f.Client.CombatPresentationTick));
        Assert.True(new BigInteger(f.Client.CombatPresentationTick) <= new BigInteger(anchor) + 1);
    }

    [Fact]
    public void Disabled_preparation_leaves_the_clock_read_unavailable()
    {
        using var f = new PreparationDeliveryScenario(enabled: false);
        f.Step();
        f.Client.AdvancePresentation(1);
        Assert.Equal(-1d, f.Client.CombatPresentationTick);
    }
}
