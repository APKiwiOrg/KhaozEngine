using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public class WorldClockTests
{
    [Theory]
    [InlineData(1.25f, 0.25f)]
    [InlineData(-0.25f, 0.75f)]
    public void Constructor_WrapsStartTimeIntoNormalizedDay(float startTimeOfDay, float expected)
    {
        WorldClock clock = new(dayLengthSeconds: 600f, startTimeOfDay);

        Assert.Equal(expected, clock.TimeOfDay, 4);
    }

    [Fact]
    public void Advance_AppliesTimeScaleAndWraps()
    {
        WorldClock clock = new(dayLengthSeconds: 600f, startTimeOfDay: 0.25f);
        clock.TimeScale = 2f;

        clock.Advance(75f);

        Assert.Equal(0.5f, clock.TimeOfDay, 4);
    }

    [Fact]
    public void Advance_WithZeroScaleLeavesTimeUnchanged()
    {
        WorldClock clock = new(dayLengthSeconds: 600f, startTimeOfDay: 0.25f);
        clock.TimeScale = 0f;

        clock.Advance(300f);

        Assert.Equal(0.25f, clock.TimeOfDay, 4);
    }

    [Fact]
    public void TimeScale_NegativeValueClampsToZero()
    {
        WorldClock clock = new(dayLengthSeconds: 600f, startTimeOfDay: 0.25f);

        clock.TimeScale = -2f;

        Assert.Equal(0f, clock.TimeScale);
    }

    [Fact]
    public void DayLengthSeconds_ValidAssignmentChangesAdvanceRate()
    {
        WorldClock clock = new(dayLengthSeconds: 600f, startTimeOfDay: 0.25f);

        clock.DayLengthSeconds = 300f;
        clock.Advance(75f);

        Assert.Equal(300f, clock.DayLengthSeconds);
        Assert.Equal(0.5f, clock.TimeOfDay, 4);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Advance_NonFiniteDeltaRetainsTime(float invalidDelta)
    {
        WorldClock clock = new(dayLengthSeconds: 600f, startTimeOfDay: 0.25f);

        clock.Advance(invalidDelta);

        Assert.Equal(0.25f, clock.TimeOfDay, 4);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void SetTimeOfDay_NonFiniteValueReturnsFalseAndRetainsTime(float invalidTime)
    {
        WorldClock clock = new(dayLengthSeconds: 600f, startTimeOfDay: 0.25f);

        bool changed = clock.SetTimeOfDay(invalidTime);

        Assert.False(changed);
        Assert.Equal(0.25f, clock.TimeOfDay, 4);
    }

    [Fact]
    public void SetTimeOfDay_FiniteValueReturnsTrueAndWraps()
    {
        WorldClock clock = new(dayLengthSeconds: 600f);

        bool changed = clock.SetTimeOfDay(-0.25f);

        Assert.True(changed);
        Assert.Equal(0.75f, clock.TimeOfDay, 4);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void TimeScale_NonFiniteAssignmentRetainsLastValidValue(float invalidScale)
    {
        WorldClock clock = new(dayLengthSeconds: 600f) { TimeScale = 2f };

        clock.TimeScale = invalidScale;

        Assert.Equal(2f, clock.TimeScale);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void DayLengthSeconds_InvalidAssignmentRetainsLastValidValue(float invalidLength)
    {
        WorldClock clock = new(dayLengthSeconds: 600f);

        clock.DayLengthSeconds = invalidLength;

        Assert.Equal(600f, clock.DayLengthSeconds);
    }

    [Fact]
    public void Snapshot_CapturesAllCurrentValues()
    {
        WorldClock clock = new(dayLengthSeconds: 600f, startTimeOfDay: 0.25f)
        {
            TimeScale = 2f,
        };
        clock.DayLengthSeconds = 300f;

        WorldClockState snapshot = clock.Snapshot;

        Assert.Equal(new WorldClockState(0.25f, 300f, 2f), snapshot);
    }
}
