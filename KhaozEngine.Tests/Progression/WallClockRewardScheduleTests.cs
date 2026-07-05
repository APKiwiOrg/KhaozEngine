using KhaozEngine.Progression;
using System;
using Xunit;

namespace KhaozEngine.Tests;

public class WallClockRewardScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    [Fact]
    public void NotAvailableBeforeInterval()
    {
        var s = WallClockRewardSchedule.Start(Day, T0, availableImmediately: false);

        Assert.False(s.IsAvailable(T0));
        Assert.False(s.IsAvailable(T0 + TimeSpan.FromHours(23.99)));
    }

    [Fact]
    public void AvailableAtAndAfterInterval()
    {
        var s = WallClockRewardSchedule.Start(Day, T0, availableImmediately: false);

        Assert.True(s.IsAvailable(T0 + Day));
        Assert.True(s.IsAvailable(T0 + Day + Day));
    }

    [Fact]
    public void AvailableImmediatelyWhenRequested()
    {
        var s = WallClockRewardSchedule.Start(Day, T0, availableImmediately: true);

        Assert.True(s.IsAvailable(T0));
        Assert.Equal(T0, s.NextAvailableUtc);
    }

    [Fact]
    public void FirstRunSeedsAtFullIntervalByDefault()
    {
        var s = WallClockRewardSchedule.Start(Day, T0);

        Assert.Equal(T0 + Day, s.NextAvailableUtc);
        Assert.False(s.IsAvailable(T0));
    }

    [Fact]
    public void InitialDelayKnobSeedsFirstRewardAtOffset()
    {
        // The Nullwake first-run case: a random 0..interval offset so the first reward
        // does not always land on the full-interval boundary.
        var offset = TimeSpan.FromHours(7);
        var s = WallClockRewardSchedule.Start(Day, T0, initialDelay: offset);

        Assert.Equal(T0 + offset, s.NextAvailableUtc);
        Assert.False(s.IsAvailable(T0 + TimeSpan.FromHours(6.99)));
        Assert.True(s.IsAvailable(T0 + offset));
    }

    [Fact]
    public void ClaimAdvancesByExactlyOneInterval()
    {
        var s = WallClockRewardSchedule.Start(Day, T0, availableImmediately: true);
        var claimAt = T0 + TimeSpan.FromHours(3);

        var next = s.Claim(claimAt);

        Assert.Equal(claimAt + Day, next.NextAvailableUtc);
        Assert.Equal(Day, next.Interval);
    }

    [Fact]
    public void NonStackingAfterLongAbsence()
    {
        var s = WallClockRewardSchedule.Start(Day, T0, availableImmediately: true);
        var afterTenDays = T0 + TimeSpan.FromDays(10);

        // Away for ten intervals still yields exactly one available reward.
        Assert.True(s.IsAvailable(afterTenDays));

        var claimed = s.Claim(afterTenDays);

        // Claiming consumes the single reward; the next is due one interval after the claim,
        // NOT nine more stacked up from the missed intervals.
        Assert.False(claimed.IsAvailable(afterTenDays));
        Assert.False(claimed.IsAvailable(afterTenDays + TimeSpan.FromHours(23)));
        Assert.True(claimed.IsAvailable(afterTenDays + Day));
    }

    [Fact]
    public void BackwardClockStepDoesNotBrickOrSpam()
    {
        var s = WallClockRewardSchedule.Start(Day, T0, availableImmediately: false);

        // Wall clock jumps far backward (NTP correction / user changed the system clock).
        var wayBack = T0 - TimeSpan.FromDays(100);

        Assert.False(s.IsAvailable(wayBack));
        var remaining = s.TimeUntilAvailable(wayBack);
        Assert.True(remaining > TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromDays(100) + Day, remaining);

        // Claiming at the backward instant still produces a valid forward schedule (no throw, no reset to past).
        var claimed = s.Claim(wayBack);
        Assert.Equal(wayBack + Day, claimed.NextAvailableUtc);
        Assert.False(claimed.IsAvailable(wayBack));
    }

    [Fact]
    public void ClaimClampsInsteadOfOverflowingNearMaxValue()
    {
        var s = WallClockRewardSchedule.Start(Day, T0, availableImmediately: true);
        var nearMax = DateTimeOffset.MaxValue - TimeSpan.FromHours(1);

        var claimed = s.Claim(nearMax); // nearMax + 24h would overflow DateTimeOffset.MaxValue

        Assert.Equal(DateTimeOffset.MaxValue, claimed.NextAvailableUtc);
    }

    [Fact]
    public void ConvertsLocalOffsetToUtcNeverRelabels()
    {
        // 2026-01-01T13:00:00+13:00 is the same instant as 2026-01-01T00:00:00Z.
        var withOffset = new DateTimeOffset(2026, 1, 1, 13, 0, 0, TimeSpan.FromHours(13));
        var s = WallClockRewardSchedule.Start(TimeSpan.FromHours(1), withOffset, availableImmediately: false);

        // Stored as a true UTC instant (offset zero), converted - not relabelled (which would give 13:00Z).
        Assert.Equal(TimeSpan.Zero, s.NextAvailableUtc.Offset);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero), s.NextAvailableUtc);
        Assert.True(s.IsAvailable(new DateTimeOffset(2026, 1, 1, 1, 30, 0, TimeSpan.Zero)));

        // Claim normalises too.
        var claimAt = new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.FromHours(13)); // == 01:00Z
        var next = s.Claim(claimAt);
        Assert.Equal(TimeSpan.Zero, next.NextAvailableUtc.Offset);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero), next.NextAvailableUtc);
    }

    [Fact]
    public void TimeUntilAvailableIsZeroWhenAvailableAndCountsDownBefore()
    {
        var available = WallClockRewardSchedule.Start(Day, T0, availableImmediately: true);
        Assert.Equal(TimeSpan.Zero, available.TimeUntilAvailable(T0));
        Assert.Equal(TimeSpan.Zero, available.TimeUntilAvailable(T0 + TimeSpan.FromDays(100)));

        var pending = WallClockRewardSchedule.Start(Day, T0, availableImmediately: false);
        Assert.Equal(Day, pending.TimeUntilAvailable(T0));
        Assert.Equal(TimeSpan.FromHours(1), pending.TimeUntilAvailable(T0 + TimeSpan.FromHours(23)));
    }

    [Fact]
    public void StartRejectsNonPositiveInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WallClockRewardSchedule.Start(TimeSpan.Zero, T0, availableImmediately: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => WallClockRewardSchedule.Start(TimeSpan.FromHours(-1), T0, availableImmediately: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => WallClockRewardSchedule.Start(TimeSpan.Zero, T0, initialDelay: TimeSpan.Zero));
    }

    [Fact]
    public void StartRejectsNegativeInitialDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WallClockRewardSchedule.Start(Day, T0, initialDelay: TimeSpan.FromHours(-1)));
    }
}
