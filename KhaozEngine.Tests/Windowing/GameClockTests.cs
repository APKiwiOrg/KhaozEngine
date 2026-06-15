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
    }
}
