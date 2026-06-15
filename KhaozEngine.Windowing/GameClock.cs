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
    /// </summary>
    public sealed class GameClock
    {
        float _timeScale = 1f;
        bool _paused;
        bool _wasPaused;

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
