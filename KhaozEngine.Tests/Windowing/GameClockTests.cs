using System;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// 5.x-native (MonoGame-free) clock: separates real delta from a scaled simulation delta, with
    /// pause/resume and a time scale, driven by a raw <c>float</c> dt (the value <c>AppWindow.Frame.Dt</c>
    /// provides). Mirrors the proven 4.x <c>KhaozEngine.Time.GameClock</c> API. Pure, headless.
    /// </summary>
    public class GameClockTests
    {
        [Fact]
        public void DefaultsAreNormalSpeedNotPausedZeroDeltas()
        {
            var c = new GameClock();
            Assert.Equal(1f, c.TimeScale);
            Assert.False(c.IsPaused);
            Assert.Equal(0f, c.RealDeltaSeconds);
            Assert.Equal(0f, c.ScaledDeltaSeconds);
        }

        [Fact]
        public void NormalSpeed_ScaledEqualsReal()
        {
            var c = new GameClock();
            c.Update(0.5f);
            Assert.Equal(0.5f, c.RealDeltaSeconds);
            Assert.Equal(0.5f, c.ScaledDeltaSeconds);
        }

        [Fact]
        public void FastForward_ScalesSimNotReal()
        {
            var c = new GameClock { TimeScale = 2f };
            c.Update(0.5f);
            Assert.Equal(0.5f, c.RealDeltaSeconds);
            Assert.Equal(1.0f, c.ScaledDeltaSeconds);
        }

        [Fact]
        public void SlowMo_ScalesSimDown()
        {
            var c = new GameClock { TimeScale = 0.5f };
            c.Update(0.5f);
            Assert.Equal(0.25f, c.ScaledDeltaSeconds);
        }

        [Fact]
        public void NegativeTimeScale_ClampsToZeroAndPauses()
        {
            var c = new GameClock { TimeScale = -3f };
            Assert.Equal(0f, c.TimeScale);
            Assert.True(c.IsPaused);
        }

        [Fact]
        public void Pause_ZeroesScaledButNotRealDelta()
        {
            var c = new GameClock();
            c.Pause();
            c.Update(0.5f);
            Assert.True(c.IsPaused);
            Assert.Equal(0f, c.ScaledDeltaSeconds);
            Assert.Equal(0.5f, c.RealDeltaSeconds);
        }

        [Fact]
        public void Resume_RestoresPriorTimeScale()
        {
            var c = new GameClock { TimeScale = 2f };
            c.Pause();
            c.Resume();
            c.Update(0.5f);
            Assert.False(c.IsPaused);
            Assert.Equal(1.0f, c.ScaledDeltaSeconds);
        }

        [Fact]
        public void TimeScaleZero_ReportsPaused()
        {
            Assert.True(new GameClock { TimeScale = 0f }.IsPaused);
        }

        [Fact]
        public void PausedEvent_FiresOnceOnTransition()
        {
            var c = new GameClock();
            int paused = 0, resumed = 0;
            c.Paused += () => paused++;
            c.Resumed += () => resumed++;

            c.Pause();
            c.Update(0.5f);
            c.Update(0.5f);
            Assert.Equal(1, paused);
            Assert.Equal(0, resumed);

            c.Resume();
            c.Update(0.5f);
            Assert.Equal(1, resumed);
        }

        [Fact]
        public void AccumulatesElapsedRealAndScaledSeconds()
        {
            var c = new GameClock { TimeScale = 2f };
            c.Update(0.5f);
            c.Update(0.25f);
            Assert.Equal(0.75f, c.ElapsedRealSeconds, 4);     // 0.5 + 0.25
            Assert.Equal(1.5f, c.ElapsedScaledSeconds, 4);    // 1.0 + 0.5
        }

        [Fact]
        public void PausedTime_AdvancesRealButNotScaledElapsed()
        {
            var c = new GameClock();
            c.Update(0.5f);
            c.Pause();
            c.Update(0.5f);
            Assert.Equal(1.0f, c.ElapsedRealSeconds, 4);      // both frames
            Assert.Equal(0.5f, c.ElapsedScaledSeconds, 4);    // only the unpaused one
        }

        // --- Wall-clock resume-gap detection (independent of the sim-delta clamp) ---------------------
        // The gap is measured from a UTC wall clock (robust to OS sleep/suspend, which a Stopwatch/frame-dt
        // does not survive), injected here via the internal now-provider seam so tests stay deterministic.

        static readonly DateTimeOffset T0 = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void FirstUpdate_WallGapIsZero_AndTimestampCaptured()
        {
            var now = T0;
            var c = new GameClock(() => now);

            c.Update(0.016f);

            Assert.Equal(0.0, c.RealWallGapSeconds);        // no previous frame to diff against
            Assert.Equal(T0, c.LastRealTimestamp);          // captured this frame's wall clock
        }

        [Fact]
        public void WallGap_ReflectsRealTimeBetweenFrames()
        {
            var now = T0;
            var c = new GameClock(() => now);

            c.Update(0.016f);           // frame 1
            now = T0.AddSeconds(5);     // 5s of wall time elapse before...
            c.Update(0.016f);           // frame 2

            Assert.Equal(5.0, c.RealWallGapSeconds, 6);
            Assert.Equal(T0.AddSeconds(5), c.LastRealTimestamp);
        }

        [Fact]
        public void WallGap_IsIndependentOfTheSimDeltaClamp()
        {
            var now = T0;
            var c = new GameClock(() => now);

            c.Update(0.016f);                   // frame 1
            now = T0.AddHours(2);               // a 2-hour OS suspend...
            c.Update(0.016f);                   // ...but the frame dt is still a normal, clamped 16ms

            Assert.Equal(7200.0, c.RealWallGapSeconds, 3);  // full wall gap surfaced
            Assert.Equal(0.016f, c.RealDeltaSeconds);       // sim delta unaffected by the gap
        }

        [Fact]
        public void WallGap_BackwardClockStep_ClampsToZero()
        {
            var now = T0.AddSeconds(100);
            var c = new GameClock(() => now);

            c.Update(0.016f);                       // frame 1
            now = T0.AddSeconds(40);                // NTP/DST steps the wall clock backwards 60s
            c.Update(0.016f);                       // frame 2

            Assert.Equal(0.0, c.RealWallGapSeconds);            // never negative
            Assert.Equal(T0.AddSeconds(40), c.LastRealTimestamp); // still tracks the latest sample
        }

        [Fact]
        public void WallGap_IsPerFrameNotCumulative()
        {
            var now = T0;
            var c = new GameClock(() => now);

            c.Update(0.016f);                       // frame 1: gap 0
            now = T0.AddSeconds(3);
            c.Update(0.016f);                       // frame 2: gap 3
            Assert.Equal(3.0, c.RealWallGapSeconds, 6);

            now = T0.AddSeconds(3.5);
            c.Update(0.016f);                       // frame 3: gap 0.5 (not 3.5)
            Assert.Equal(0.5, c.RealWallGapSeconds, 6);
        }

        [Fact]
        public void WallGap_MeasuredEvenWhilePaused()
        {
            var now = T0;
            var c = new GameClock(() => now);
            c.Pause();                              // sim frozen, but real time keeps moving

            c.Update(0.016f);                       // frame 1
            now = T0.AddSeconds(90);
            c.Update(0.016f);                       // frame 2

            Assert.Equal(0f, c.ScaledDeltaSeconds);         // paused: no sim advance
            Assert.Equal(90.0, c.RealWallGapSeconds, 6);    // wall gap still observed
        }
    }
}
