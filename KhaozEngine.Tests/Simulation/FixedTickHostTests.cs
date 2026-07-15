using System;
using System.Collections.Generic;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public class FixedTickHostTests
{
    [Fact]
    public void Advance_OneExactTick_ProducesOneTick()
    {
        var host = new FixedTickHost(0.1f);
        var ticks = new List<long>();
        int produced = host.Advance(0.1f, ticks.Add);
        Assert.Equal(1, produced);
        Assert.Equal(new long[] { 0 }, ticks);
        Assert.Equal(1L, host.TickCount);
    }

    [Fact]
    public void Advance_AccumulatesFractionsAcrossCalls()
    {
        var host = new FixedTickHost(0.1f);
        var ticks = new List<long>();
        Assert.Equal(0, host.Advance(0.06f, ticks.Add)); // 0.06 < 0.1 -> no tick
        Assert.Equal(1, host.Advance(0.06f, ticks.Add)); // 0.12 total -> one tick, 0.02 left
        Assert.Equal(new long[] { 0 }, ticks);
    }

    [Fact]
    public void Advance_LargeElapsed_ProducesMultipleTicks_UpToCap()
    {
        var host = new FixedTickHost(0.1f);
        var ticks = new List<long>();
        int produced = host.Advance(10f, ticks.Add, maxTicksPerFrame: 4); // would be 100 ticks; capped at 4
        Assert.Equal(4, produced);
        Assert.Equal(new long[] { 0, 1, 2, 3 }, ticks);
    }

    [Fact]
    public void Advance_NegativeElapsed_IsClampedToZero()
    {
        var host = new FixedTickHost(0.1f);
        Assert.Equal(0, host.Advance(-5f, _ => { }));
        Assert.Equal(0L, host.TickCount);
    }

    [Fact]
    public void Reset_ZeroesAccumulatorAndCount()
    {
        var host = new FixedTickHost(0.1f);
        host.Advance(0.25f, _ => { }); // 2 ticks, 0.05 left over
        host.Reset();
        Assert.Equal(0L, host.TickCount);
        var ticks = new List<long>();
        Assert.Equal(0, host.Advance(0.05f, ticks.Add)); // leftover was cleared, 0.05 < 0.1
    }

    [Fact]
    public void Ctor_NonPositiveTick_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedTickHost(0f));
    }

    [Fact]
    public void SecondsUntilNextTick_IsAFullTick_RightAfterATickFiresWithNothingLeftOver()
    {
        // Right after a tick fires with a clean accumulator (nothing left over), the NEXT tick is a full
        // tickSeconds away - the accumulator has to build back up from zero.
        var host = new FixedTickHost(0.1f);
        host.Advance(0.1f, _ => { });   // exactly one tick, nothing left over
        Assert.Equal(0.1f, host.SecondsUntilNextTick, precision: 5);
    }

    [Fact]
    public void SecondsUntilNextTick_ReflectsLeftoverAccumulator()
    {
        var host = new FixedTickHost(0.1f);
        host.Advance(0.07f, _ => { });   // no tick yet, 0.07 accumulated
        Assert.Equal(0.03f, host.SecondsUntilNextTick, precision: 5);
    }

    [Fact]
    public void SecondsUntilNextTick_AfterCapClamp_NeverNegative()
    {
        var host = new FixedTickHost(0.1f);
        host.Advance(10f, _ => { }, maxTicksPerFrame: 4);   // capped: accumulator clamped to at most one tick
        Assert.True(host.SecondsUntilNextTick >= 0f);
    }

    [Theory]
    [InlineData(0.02f, 0.005f, 0f, 0.015f)]      // plenty of runway: margin subtracted, no floor triggers
    [InlineData(0.02f, 0.016f, 0f, 0.004f)]      // Windows-granularity margin still leaves a small positive wait
    [InlineData(0.01f, 0.016f, 0f, 0f)]          // margin exceeds the remaining time -> spin/yield, not sleep
    [InlineData(0.005f, 0f, 0.002f, 0.005f)]     // above the floor: wait passes through unchanged
    [InlineData(0.001f, 0f, 0.002f, 0f)]         // below the floor: too small to bother sleeping -> 0
    [InlineData(0f, 0.001f, 0f, 0f)]             // already at/past the tick -> never negative
    public void ComputeIdleWaitSeconds_MatchesExpected(
        float secondsUntilNextTick, float safetyMarginSeconds, float minimumSeconds, float expected)
    {
        float wait = FixedTickHost.ComputeIdleWaitSeconds(secondsUntilNextTick, safetyMarginSeconds, minimumSeconds);
        Assert.Equal(expected, wait, precision: 5);
    }

    [Fact]
    public void ComputeIdleWaitSeconds_NegativeSecondsUntilNextTick_ClampsToZero()
    {
        // A caller that polled slightly late might compute a negative remainder; never sleep on a negative wait.
        float wait = FixedTickHost.ComputeIdleWaitSeconds(-0.01f, safetyMarginSeconds: 0f, minimumSeconds: 0f);
        Assert.Equal(0f, wait);
    }

    [Fact]
    public void ComputeIdleWaitSeconds_NegativeMargin_TreatedAsZero()
    {
        float wait = FixedTickHost.ComputeIdleWaitSeconds(0.02f, safetyMarginSeconds: -1f, minimumSeconds: 0f);
        Assert.Equal(0.02f, wait, precision: 5);
    }
}
