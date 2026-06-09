using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Time;

/// <summary>
/// A game-agnostic clock that separates real delta time from a scaled simulation delta.
/// Set <see cref="TimeScale"/> for slow-mo (&lt;1), normal (1), or fast-forward (&gt;1), and
/// <see cref="Pause"/>/<see cref="Resume"/> to freeze the sim while real time keeps running
/// (UI, transitions, notifications). Pause is orthogonal to <see cref="TimeScale"/>: resuming
/// restores the intended speed. <see cref="Paused"/>/<see cref="Resumed"/> fire on transitions.
/// </summary>
public sealed class GameClock
{
    private float _timeScale = 1f;
    private bool _paused;
    private bool _wasPaused;   // last observed IsPaused, for edge-triggered events

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

    /// <summary>Fired when <see cref="IsPaused"/> transitions false -&gt; true.</summary>
    public event Action? Paused;

    /// <summary>Fired when <see cref="IsPaused"/> transitions true -&gt; false.</summary>
    public event Action? Resumed;

    /// <summary>Explicitly pause the simulation (independent of <see cref="TimeScale"/>).</summary>
    public void Pause() { _paused = true; RaiseIfChanged(); }

    /// <summary>Clear an explicit pause, restoring the current <see cref="TimeScale"/>.</summary>
    public void Resume() { _paused = false; RaiseIfChanged(); }

    /// <summary>Advance once per frame, before consumers read the deltas.</summary>
    public void Update(GameTime gameTime)
    {
        RealDeltaSeconds = (float)gameTime.ElapsedGameTime.TotalSeconds;
        ScaledDeltaSeconds = IsPaused ? 0f : RealDeltaSeconds * _timeScale;
        RaiseIfChanged();
    }

    private void RaiseIfChanged()
    {
        bool now = IsPaused;
        if (now == _wasPaused) return;
        _wasPaused = now;
        if (now) Paused?.Invoke();
        else Resumed?.Invoke();
    }
}
