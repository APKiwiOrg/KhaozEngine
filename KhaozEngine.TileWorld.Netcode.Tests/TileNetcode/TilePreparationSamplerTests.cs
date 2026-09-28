using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationSamplerTests
{
    static readonly TileCombatPreparation Schedule = new(1, 2, 1, 1, 7, 100, 103, 1, 14);

    [Theory]
    [InlineData(99, TileCombatPreparationStage.Hold, 0f)]
    [InlineData(100, TileCombatPreparationStage.Prepare, 0f)]
    [InlineData(101, TileCombatPreparationStage.Prepare, .5f)]
    [InlineData(102, TileCombatPreparationStage.Strike, 0f)]
    [InlineData(102.5, TileCombatPreparationStage.Strike, .5f)]
    [InlineData(103, TileCombatPreparationStage.AwaitingOutcome, 1f)]
    [InlineData(double.MaxValue, TileCombatPreparationStage.AwaitingOutcome, 1f)]
    public void Three_tick_schedule_has_two_ticks_of_prepare_and_one_of_strike(
        double tick, TileCombatPreparationStage stage, float progress)
    {
        Assert.Equal(new TileCombatPreparationSample(stage, progress), TileCombatPreparationSampler.Sample(Schedule, tick));
    }

    [Fact]
    public void Revised_schedule_returns_to_hold_until_its_new_start()
    {
        var revised = Schedule with { Revision = 2, PrepareTick = 103, ImpactTick = 106 };
        Assert.Equal(TileCombatPreparationStage.Strike, TileCombatPreparationSampler.Sample(Schedule, 102).Stage);
        Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.Hold, 0),
            TileCombatPreparationSampler.Sample(revised, 102));
        Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.Prepare, 0),
            TileCombatPreparationSampler.Sample(revised, 103));
        Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.Strike, 0),
            TileCombatPreparationSampler.Sample(revised, 105));
    }

    [Fact]
    public void Lead_equal_to_strike_skips_prepare_without_dividing_by_zero()
    {
        var preparation = Schedule with { StrikeTicks = 3 };
        Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.Hold, 0),
            TileCombatPreparationSampler.Sample(preparation, 99));
        Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.Strike, 0),
            TileCombatPreparationSampler.Sample(preparation, 100));
        Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.Strike, .5f),
            TileCombatPreparationSampler.Sample(preparation, 101.5));
        Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.AwaitingOutcome, 1),
            TileCombatPreparationSampler.Sample(preparation, 103));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MinValue)]
    public void Non_finite_or_early_sample_time_holds_without_progress(double tick)
    {
        Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.Hold, 0),
            TileCombatPreparationSampler.Sample(Schedule, tick));
    }

    [Theory]
    [MemberData(nameof(InvalidSchedules))]
    public void Invalid_schedules_are_rejected_even_when_sample_time_is_non_finite(TileCombatPreparation preparation)
    {
        Assert.Throws<ArgumentException>(() => TileCombatPreparationSampler.Sample(preparation, 100));
        Assert.Throws<ArgumentException>(() => TileCombatPreparationSampler.Sample(preparation, double.NaN));
    }

    public static IEnumerable<object[]> InvalidSchedules()
    {
        yield return new object[] { default(TileCombatPreparation) };
        yield return new object[] { Schedule with { AttackerNetId = 0 } };
        yield return new object[] { Schedule with { TargetNetId = 0 } };
        yield return new object[] { Schedule with { AttackId = 0 } };
        yield return new object[] { Schedule with { Revision = 0 } };
        yield return new object[] { Schedule with { PrepareTick = -1 } };
        yield return new object[] { Schedule with { ImpactTick = 100 } };
        yield return new object[] { Schedule with { ImpactTick = 99 } };
        yield return new object[] { Schedule with { StrikeTicks = 0 } };
        yield return new object[] { Schedule with { StrikeTicks = 4 } };
        yield return new object[] { Schedule with { CadenceTicks = 2 } };
        yield return new object[] { Schedule with { PrepareTick = long.MinValue, ImpactTick = long.MaxValue } };
    }

    [Fact]
    public void Integer_boundaries_are_preserved_when_double_time_cannot_represent_adjacent_ticks()
    {
        var large = Schedule with { PrepareTick = 9007199254740991, ImpactTick = 9007199254740994 };
        Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.Prepare, .5f),
            TileCombatPreparationSampler.Sample(large, 9007199254740992d));
        var nearLimit = Schedule with { PrepareTick = long.MaxValue - 1022, ImpactTick = long.MaxValue - 1019 };
        Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.Hold, 0),
            TileCombatPreparationSampler.Sample(nearLimit, (double)(long.MaxValue - 1023)));
    }

    [Fact]
    public void Cadence_does_not_infer_a_different_stage_or_loop_the_attempt()
    {
        Assert.Equal(TileCombatPreparationSampler.Sample(Schedule, 101),
            TileCombatPreparationSampler.Sample(Schedule with { CadenceTicks = 255 }, 101));
        Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.AwaitingOutcome, 1),
            TileCombatPreparationSampler.Sample(Schedule, 1000));
    }
}
