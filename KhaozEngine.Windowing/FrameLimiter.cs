namespace KhaozEngine.Windowing
{
    /// <summary>
    /// Software frame-rate cap: paces a render loop to a target Hz using a monotonic clock, so a game can pin its
    /// render rate to an integer multiple of its fixed simulation/network tick regardless of whether the swapchain's
    /// vsync actually throttles on a given backend (notably the Veldrid Metal path, which could free-run well above the
    /// display refresh even with <c>SyncToVerticalBlank</c> on). Pure scheduling math driven by a caller-supplied
    /// monotonic time. <see cref="AppWindow.Run(System.Action{Frame})"/> owns the actual waiting. Deterministic and headless-testable.
    /// </summary>
    public sealed class FrameLimiter
    {
        readonly double _period;   // target seconds per frame; 0 = uncapped
        double _target;            // the ideal monotonic time the next frame should begin
        bool _primed;

        /// <summary>Cap to <paramref name="targetHz"/> frames/second. Zero or negative = uncapped (a no-op limiter).</summary>
        public FrameLimiter(int targetHz) => _period = targetHz > 0 ? 1.0 / targetHz : 0.0;

        /// <summary>True when a positive cap is active; false is a no-op limiter that never waits.</summary>
        public bool Enabled => _period > 0.0;

        /// <summary>
        /// Call once per frame (at the point pacing should occur, e.g. after present) with the current monotonic time
        /// in seconds. Returns the seconds to idle before the next frame begins so the cadence holds the target
        /// period: 0 when uncapped or already behind schedule. Small per-frame work variance is corrected by anchoring
        /// to a fixed schedule; a stall longer than one period re-anchors to <paramref name="now"/> so lost time is
        /// never reclaimed as a burst of zero-wait catch-up frames.
        /// </summary>
        public double WaitBeforeNext(double now)
        {
            if (!Enabled) return 0.0;
            if (!_primed)
            {
                _primed = true;
                _target = now + _period;   // first frame starts immediately; next is one period out
                return 0.0;
            }
            double wait = _target - now;
            if (wait <= 0.0)
            {
                // Behind schedule: re-anchor on a large overshoot (a stall) so we don't bank catch-up; otherwise just
                // advance by one period so a single slow frame is absorbed without a burst.
                _target = wait < -_period ? now + _period : _target + _period;
                return 0.0;
            }
            _target += _period;
            return wait;
        }
    }
}
