using System;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// 5.x-native (MonoGame-free) game clock: separates real delta time from a scaled simulation delta. Set
    /// <see cref="TimeScale"/> for slow-mo (&lt;1), normal (1), or fast-forward (&gt;1), and
    /// <see cref="Pause"/>/<see cref="Resume"/> to freeze the sim while real time keeps running (UI,
    /// transitions). Pause is orthogonal to <see cref="TimeScale"/>: resuming restores the intended speed.
    /// Drive it once per frame from the raw frame delta (<c>AppWindow.Frame.Dt</c>). The custom-stack analogue
    /// of the 4.x <c>KhaozEngine.Time.GameClock</c>, taking a <c>float</c> dt instead of a MonoGame
    /// <c>GameTime</c>; <see cref="Paused"/>/<see cref="Resumed"/> fire on transitions.
    /// <para>Each <see cref="Update"/> also samples a UTC wall clock and reports the gap to the previous
    /// frame as <see cref="RealWallGapSeconds"/> (with <see cref="LastRealTimestamp"/>). Unlike the frame
    /// <c>dt</c> (a QueryPerformanceCounter-backed value that does not reliably advance across OS
    /// sleep/S3/hibernate), the wall gap survives a suspend, so a game can detect that a large real-time gap
    /// just happened (offline catch-up, timer re-sync). It is a separate signal and never feeds the scaled/real
    /// deltas.</para>
    /// </summary>
    public sealed class GameClock
    {
        readonly Func<DateTimeOffset> _now;
        bool _hasLastTimestamp;

        float _timeScale = 1f;
        bool _paused;
        bool _wasPaused;

        /// <summary>Normal clock: the wall gap is measured from <see cref="DateTimeOffset.UtcNow"/>.</summary>
        public GameClock() : this(static () => DateTimeOffset.UtcNow) { }

        /// <summary>Test seam: inject the wall-clock source so <see cref="RealWallGapSeconds"/> is deterministic.</summary>
        internal GameClock(Func<DateTimeOffset> nowProvider) => _now = nowProvider;

        /// <summary>Simulation speed multiplier; clamped to &gt;= 0. 0 = paused, &lt;1 = slow-mo, &gt;1 = fast-forward.</summary>
        public float TimeScale
        {
            get => _timeScale;
            set { _timeScale = value < 0f ? 0f : value; RaiseIfChanged(); }
        }

        /// <summary>True when explicitly paused or <see cref="TimeScale"/> is 0.</summary>
        public bool IsPaused => _paused || _timeScale == 0f;

        /// <summary>Last frame's unscaled delta in seconds.</summary>
        public float RealDeltaSeconds { get; private set; }

        /// <summary>Last frame's simulation delta: <see cref="RealDeltaSeconds"/> * scale, or 0 when paused.</summary>
        public float ScaledDeltaSeconds { get; private set; }

        /// <summary>Running total of real (unscaled) seconds across all <see cref="Update"/> calls.</summary>
        public float ElapsedRealSeconds { get; private set; }

        /// <summary>Running total of scaled (simulation) seconds; does not advance while paused.</summary>
        public float ElapsedScaledSeconds { get; private set; }

        /// <summary>Wall-clock seconds between the previous frame and this one, clamped to &gt;= 0.
        /// Normally ~one frame; spikes after an OS suspend/hang. Independent of the sim-delta clamp,
        /// and 0 on the first frame (no previous timestamp to diff).</summary>
        public double RealWallGapSeconds { get; private set; }

        /// <summary>UTC timestamp captured at the start of the current frame (monotonic-ish wall clock).</summary>
        public DateTimeOffset LastRealTimestamp { get; private set; }

        /// <summary>Fired when <see cref="IsPaused"/> transitions false -&gt; true.</summary>
        public event Action? Paused;

        /// <summary>Fired when <see cref="IsPaused"/> transitions true -&gt; false.</summary>
        public event Action? Resumed;

        /// <summary>Explicitly pause the simulation (independent of <see cref="TimeScale"/>).</summary>
        public void Pause() { _paused = true; RaiseIfChanged(); }

        /// <summary>Clear an explicit pause, restoring the current <see cref="TimeScale"/>.</summary>
        public void Resume() { _paused = false; RaiseIfChanged(); }

        /// <summary>Advance once per frame, before consumers read the deltas. <paramref name="dtSeconds"/> is the raw frame delta.</summary>
        public void Update(float dtSeconds)
        {
            RealDeltaSeconds = dtSeconds;
            ScaledDeltaSeconds = IsPaused ? 0f : dtSeconds * _timeScale;
            ElapsedRealSeconds += RealDeltaSeconds;
            ElapsedScaledSeconds += ScaledDeltaSeconds;

            // Wall-clock gap: a separate signal from the sim delta, robust to OS suspend. Clamp backward clock
            // steps (NTP/DST) to 0. The first Update has no previous frame, so it reports a 0 gap.
            DateTimeOffset now = _now();
            if (_hasLastTimestamp)
            {
                double gap = (now - LastRealTimestamp).TotalSeconds;
                RealWallGapSeconds = gap < 0.0 ? 0.0 : gap;
            }
            else
            {
                RealWallGapSeconds = 0.0;
                _hasLastTimestamp = true;
            }
            LastRealTimestamp = now;
        }

        void RaiseIfChanged()
        {
            bool now = IsPaused;
            if (now == _wasPaused) return;
            _wasPaused = now;
            if (now) Paused?.Invoke();
            else Resumed?.Invoke();
        }
    }
}
