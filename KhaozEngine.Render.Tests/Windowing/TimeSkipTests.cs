using System;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>5.x Windowing.TimeSkip (MonoGame-free port of the 4.x Time.TimeSkip). Pure / headless.</summary>
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

            Assert.Equal(7200, got);
            Assert.True(result.WasCapped);
            Assert.Equal(10000, result.RequestedSimSeconds);
            Assert.Equal(7200, result.AppliedSimSeconds);
            Assert.True(result.Ran);
        }

        [Fact]
        public void AdvanceAppliesMultiplierAfterCap()
        {
            var skip = new TimeSkip { MaxSimSeconds = 7200, Multiplier = 2.0 };
            double got = -1;
            var result = skip.Advance(10000, s => got = s);

            Assert.Equal(14400, got);
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
            Assert.Equal(0, result.AppliedSimSeconds);
        }

        [Fact]
        public void AdvanceZeroOrNegativeIsNoOp()
        {
            var skip = new TimeSkip();
            int calls = 0;
            Assert.False(skip.Advance(0, _ => calls++).Ran);
            Assert.False(skip.Advance(-100, _ => calls++).Ran);
            Assert.Equal(0, calls);
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
        public void ElapsedSimSecondsAppliesTimeScaleAndClampsNegative()
        {
            var last = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            Assert.Equal(3600, TimeSkip.ElapsedSimSeconds(last, last.AddHours(1)));
            Assert.Equal(7200, TimeSkip.ElapsedSimSeconds(last, last.AddHours(1), timeScale: 2.0));
            Assert.Equal(0, TimeSkip.ElapsedSimSeconds(last, last.AddHours(-1)));
        }
    }
}
