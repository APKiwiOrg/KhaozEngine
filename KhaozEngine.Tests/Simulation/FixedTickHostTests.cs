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
}
