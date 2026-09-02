using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// The opt-in "pause while the window is in the background" rule behind
    /// <see cref="GameAppOptions.PauseOnFocusLoss"/>. Driven once per frame off the frame snapshot's
    /// <see cref="InputState.WindowFocused"/> bit, so the decision is headless-testable without a window
    /// (see <c>GameApp.PreparePhase</c> for the one call site).
    /// <para>
    /// It only ever lifts a pause it took itself. A game that paused on its own (a pause menu, a zero
    /// <see cref="GameClock.TimeScale"/>) is left alone on the way out and, more importantly, on the way
    /// back in: coming back to the window must not resume a game the player deliberately paused. The claim
    /// is also dropped if the clock is running again while the window is still unfocused, so a game that
    /// resumes for its own reasons in the background keeps ownership from then on.
    /// </para>
    /// </summary>
    internal sealed class FocusAutoPause
    {
        readonly bool _enabled;
        bool _known;
        bool _focused = true;
        bool _pausedByUs;

        internal FocusAutoPause(bool enabled) => _enabled = enabled;

        /// <summary>True while the clock is paused because of THIS rule, rather than by the game.</summary>
        internal bool PausedByFocusLoss => _pausedByUs;

        /// <summary>
        /// Apply this frame's focus bit to <paramref name="clock"/>. Call before the clock's own update so a
        /// pausing frame already reports a zero scaled delta. A no-op when the option is off.
        /// </summary>
        internal void Update(bool windowFocused, GameClock clock)
        {
            if (!_enabled) return;

            // A window can be born behind another one, which fires no focused-to-unfocused transition at all,
            // so the first frame counts as a transition whatever it says.
            bool changed = !_known || windowFocused != _focused;
            _known = true;
            _focused = windowFocused;

            if (windowFocused)
            {
                if (!changed || !_pausedByUs) return;
                _pausedByUs = false;
                clock.Resume();
                return;
            }

            // Still in the background. If the game resumed the clock under us, it owns the pause state now.
            if (_pausedByUs && !clock.IsPaused) _pausedByUs = false;
            if (!changed || clock.IsPaused) return;

            clock.Pause();
            _pausedByUs = true;
        }
    }
}
