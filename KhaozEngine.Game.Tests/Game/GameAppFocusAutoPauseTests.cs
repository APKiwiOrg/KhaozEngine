using KhaozEngine.Game;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    // GameApp.Run needs a real window, so the auto-pause DECISION is factored into the pure FocusAutoPause
    // helper the loop drives off frame.Input.WindowFocused, and that helper is what these cover. The loop
    // wiring itself (one call before the clock update, fed the ctor's stored option) is the sample-verified
    // line in GameApp.PreparePhase.
    public class GameAppFocusAutoPauseTests
    {
        // A focus history driven into the helper, returning the clock's paused flag after each frame.
        static bool[] Drive(bool enabled, params bool[] focusPerFrame)
        {
            var clock = new GameClock();
            var autoPause = new FocusAutoPause(enabled);
            var seen = new bool[focusPerFrame.Length];
            for (int i = 0; i < focusPerFrame.Length; i++)
            {
                autoPause.Update(focusPerFrame[i], clock);
                clock.Update(1f / 60f);
                seen[i] = clock.IsPaused;
            }
            return seen;
        }

        [Fact]
        public void Disabled_NeverTouchesTheClock()
        {
            Assert.Equal(new[] { false, false, false, false }, Drive(false, true, false, false, true));
        }

        [Fact]
        public void Enabled_PausesOnFocusLossAndResumesOnFocusGain()
        {
            Assert.Equal(
                new[] { false, true, true, false, false },
                Drive(true, true, false, false, true, true));
        }

        [Fact]
        public void PausedFrameYieldsAZeroDelta()
        {
            var clock = new GameClock();
            var autoPause = new FocusAutoPause(enabled: true);

            autoPause.Update(windowFocused: false, clock);
            clock.Update(1f / 60f);
            Assert.Equal(0f, clock.ScaledDeltaSeconds);

            autoPause.Update(windowFocused: true, clock);
            clock.Update(1f / 60f);
            Assert.Equal(1f / 60f, clock.ScaledDeltaSeconds, 5);
        }

        [Fact]
        public void AGamePauseTakenBeforeFocusLossSurvivesTheRefocus()
        {
            // The game's own pause menu is not the auto-pause's to lift: it never claimed that pause, so
            // coming back to the window must leave the game paused where the player left it.
            var clock = new GameClock();
            var autoPause = new FocusAutoPause(enabled: true);
            clock.Pause();

            autoPause.Update(windowFocused: false, clock);
            autoPause.Update(windowFocused: true, clock);

            Assert.True(clock.IsPaused);
        }

        [Fact]
        public void AGameResumeTakenWhileUnfocusedDropsTheClaim()
        {
            // A game that deliberately resumes while backgrounded (an offline catch-up, a headless replay)
            // owns the clock from then on. Regaining focus must not double-resume on top of that.
            var clock = new GameClock();
            var autoPause = new FocusAutoPause(enabled: true);

            autoPause.Update(windowFocused: false, clock);
            Assert.True(clock.IsPaused);

            clock.Resume();
            autoPause.Update(windowFocused: false, clock);
            clock.Pause();
            autoPause.Update(windowFocused: true, clock);

            Assert.True(clock.IsPaused);
        }

        [Fact]
        public void AZeroTimeScaleCountsAsAlreadyPaused()
        {
            // GameClock.IsPaused folds in a zero TimeScale, so a game already frozen that way was not paused
            // by us and must not be handed back a live clock on refocus.
            var clock = new GameClock { TimeScale = 0f };
            var autoPause = new FocusAutoPause(enabled: true);

            autoPause.Update(windowFocused: false, clock);
            autoPause.Update(windowFocused: true, clock);

            Assert.True(clock.IsPaused);
            Assert.Equal(0f, clock.TimeScale);
        }

        [Fact]
        public void StartingUnfocusedPausesOnTheFirstFrame()
        {
            // A window launched behind another one never fires a focused-to-unfocused transition, so the
            // helper has to treat the first frame it sees as a transition of its own.
            Assert.Equal(new[] { true, true }, Drive(true, false, false));
        }

        [Fact]
        public void OptionDefaultsOff()
        {
            Assert.False(default(GameAppOptions).PauseOnFocusLoss);
            Assert.False(GameAppOptions.For("t", 320, 200).PauseOnFocusLoss);
        }
    }
}
