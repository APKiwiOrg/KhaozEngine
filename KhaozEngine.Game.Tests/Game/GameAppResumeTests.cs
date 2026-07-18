using System;
using System.Collections.Generic;
using KhaozEngine.Game;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    // GameApp.Run needs a real window, so (like the rest of the loop - including OnUpdate/OnResize ordering) it is
    // sample/golden-verified, not unit-tested. The resume-detection DECISION is factored into the pure
    // GameApp.ShouldRaiseResume helper, which IS headless-testable. These drive a real GameClock frame-by-frame with
    // an injected wall clock and run that same predicate over its gap to prove the fire decision selects exactly a
    // supra-threshold gap, with the right span, and rejects the first frame / a below-threshold gap / a backward
    // clock step / a disabled threshold. The loop wiring itself (the call placed before OnUpdate, fed the ctor's
    // stored threshold) is the sample-verified three lines in GameApp.Run, not covered here.
    public class GameAppResumeTests
    {
        static readonly DateTimeOffset T0 = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);
        const double Threshold = 30.0;

        // Mirrors the GameApp.Run fire decision: for each frame's wall-clock timestamp, drive the clock and, when
        // the same predicate the loop uses says so, record the TimeSpan OnResume would be raised with. One entry
        // per fired frame.
        static List<TimeSpan> DriveResumeSpans(double thresholdSeconds, params DateTimeOffset[] frameTimes)
        {
            var fired = new List<TimeSpan>();
            DateTimeOffset now = frameTimes.Length > 0 ? frameTimes[0] : T0;
            var clock = new GameClock(() => now);
            foreach (DateTimeOffset t in frameTimes)
            {
                now = t;
                clock.Update(0.016f);
                if (GameApp.ShouldRaiseResume(clock.RealWallGapSeconds, thresholdSeconds))
                    fired.Add(TimeSpan.FromSeconds(clock.RealWallGapSeconds));
            }
            return fired;
        }

        [Fact]
        public void SupraThresholdGap_RaisesResumeOnce_WithTheGapSpan()
        {
            DateTimeOffset f1 = T0;
            DateTimeOffset f2 = T0.AddSeconds(0.02);
            DateTimeOffset f3 = f2.AddHours(2);         // a 2-hour OS suspend between f2 and f3
            DateTimeOffset f4 = f3.AddSeconds(0.02);

            List<TimeSpan> fired = DriveResumeSpans(Threshold, f1, f2, f3, f4);

            Assert.Single(fired);                                        // exactly once
            Assert.Equal(7200.0, fired[0].TotalSeconds, 3);             // with the true 2h gap span
        }

        [Fact]
        public void FirstFrame_NeverRaisesResume()
        {
            Assert.Empty(DriveResumeSpans(Threshold, T0));   // gap is 0 on the first frame
        }

        [Fact]
        public void SubThresholdGap_DoesNotRaiseResume()
        {
            Assert.Empty(DriveResumeSpans(Threshold, T0, T0.AddSeconds(29)));   // 29s < 30s
        }

        [Fact]
        public void BackwardClockStep_DoesNotRaiseResume()
        {
            Assert.Empty(DriveResumeSpans(Threshold, T0.AddHours(1), T0));   // wall clock jumps back 1h
        }

        [Fact]
        public void ThresholdZeroOrNegative_DisablesResume()
        {
            Assert.Empty(DriveResumeSpans(0.0, T0, T0.AddHours(5)));
            Assert.Empty(DriveResumeSpans(-1.0, T0, T0.AddHours(5)));
        }

        [Theory]
        [InlineData(30.0, 30.0, false)]     // exactly at threshold does not fire (strictly greater)
        [InlineData(30.0, 30.001, true)]
        [InlineData(30.0, 45.0, true)]
        [InlineData(30.0, 0.0, false)]
        [InlineData(0.0, 100.0, false)]     // threshold 0 disables
        [InlineData(-5.0, 100.0, false)]    // negative threshold disables
        public void ShouldRaiseResume_Predicate(double thresholdSeconds, double wallGapSeconds, bool expected)
            => Assert.Equal(expected, GameApp.ShouldRaiseResume(wallGapSeconds, thresholdSeconds));
    }
}
