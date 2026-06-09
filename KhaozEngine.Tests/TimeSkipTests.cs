using System;
using KhaozEngine.Time;
using Xunit;

namespace KhaozEngine.Tests;

public class TimeSkipTests
{
    [Fact]
    public void AdvanceUncappedPassesRequestedSecondsToStep()
    {
        var skip = new TimeSkip();
        double got = -1;
        var result = skip.Advance(7200, s => got = s);

        Assert.Equal(7200, got);
        Assert.True(result.Ran);
        Assert.Equal(7200, result.RequestedSimSeconds);
        Assert.Equal(7200, result.AppliedSimSeconds);
        Assert.False(result.WasCapped);
    }

    [Fact]
    public void AdvanceClampsToMaxSimSeconds()
    {
        var skip = new TimeSkip { MaxSimSeconds = 7200 };
        double got = -1;
        var result = skip.Advance(10000, s => got = s);

        Assert.Equal(7200, got);                       // step receives the capped value
        Assert.True(result.WasCapped);
        Assert.Equal(10000, result.RequestedSimSeconds);  // original request retained
        Assert.Equal(7200, result.AppliedSimSeconds);
        Assert.True(result.Ran);
    }

    [Fact]
    public void AdvanceAppliesMultiplierAfterCap()
    {
        var skip = new TimeSkip { MaxSimSeconds = 7200, Multiplier = 2.0 };
        double got = -1;
        var result = skip.Advance(10000, s => got = s);

        Assert.Equal(14400, got);                      // capped 7200 * 2
        Assert.Equal(14400, result.AppliedSimSeconds);
        Assert.True(result.WasCapped);
    }

    [Fact]
    public void AdvanceBelowMinIsNoOp()
    {
        var skip = new TimeSkip { MinSimSeconds = 60 };
        bool called = false;
        var result = skip.Advance(30, _ => called = true);

        Assert.False(called);
        Assert.False(result.Ran);
        Assert.Equal(30, result.RequestedSimSeconds);
        Assert.Equal(0, result.AppliedSimSeconds);
        Assert.False(result.WasCapped);
    }

    [Fact]
    public void AdvanceZeroOrNegativeIsNoOpEvenWithDefaultMin()
    {
        var skip = new TimeSkip();   // MinSimSeconds defaults to 0
        int calls = 0;
        var zero = skip.Advance(0, _ => calls++);
        var neg = skip.Advance(-100, _ => calls++);

        Assert.Equal(0, calls);
        Assert.False(zero.Ran);
        Assert.False(neg.Ran);
    }

    [Fact]
    public void CompletedFiresWithSameResultThatWasReturned()
    {
        var skip = new TimeSkip();
        TimeSkipResult? fired = null;
        skip.Completed += r => fired = r;

        var returned = skip.Advance(100, _ => { });

        Assert.NotNull(fired);
        Assert.Equal(returned.AppliedSimSeconds, fired!.Value.AppliedSimSeconds);
        Assert.Equal(returned.Ran, fired!.Value.Ran);
    }

    [Fact]
    public void CompletedFiresEvenOnNoOp()
    {
        var skip = new TimeSkip { MinSimSeconds = 60 };
        int fired = 0;
        skip.Completed += _ => fired++;

        skip.Advance(10, _ => { });

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ElapsedSimSecondsReturnsWallSecondsByDefault()
    {
        var last = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = last.AddHours(1);

        Assert.Equal(3600, TimeSkip.ElapsedSimSeconds(last, now));
    }

    [Fact]
    public void ElapsedSimSecondsAppliesTimeScale()
    {
        var last = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = last.AddHours(1);

        Assert.Equal(7200, TimeSkip.ElapsedSimSeconds(last, now, timeScale: 2.0));
    }

    [Fact]
    public void ElapsedSimSecondsClampsNegativeSpanToZero()
    {
        var last = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
        var now = last.AddHours(-1);   // now is BEFORE last (clock skew)

        Assert.Equal(0, TimeSkip.ElapsedSimSeconds(last, now));
    }
}
