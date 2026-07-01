using System;
using System.Collections.Generic;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// A headless frame-time meter: feed it one <see cref="Sample"/> per frame and read back the FPS and
/// per-frame millisecond average/min/max over a rolling window (default ~1 second). Pure managed code, no
/// renderer or GPU dependency, so it constructs and unit-tests from a synthetic <c>dt</c> stream. Intended
/// to back a <c>Performance</c> section in <c>KhaozEngine.Gui.DiagnosticsOverlay</c>.
/// </summary>
public sealed class FrameStats
{
    readonly float _window;
    readonly Queue<float> _samples = new();
    float _sum;
    float _fps;
    float _avgMs;
    float _minMs;
    float _maxMs;

    /// <summary>Create a meter whose rolling window spans <paramref name="windowSeconds"/> (defaults to 1s).</summary>
    public FrameStats(float windowSeconds = 1f) =>
        _window = windowSeconds > 0f ? windowSeconds : 1f;

    /// <summary>
    /// Record one frame's delta-time (seconds) and recompute the aggregates. Non-positive, NaN, or infinite
    /// <paramref name="dt"/> values are ignored (a paused / first frame should not poison the window).
    /// </summary>
    public void Sample(float dt)
    {
        if (!(dt > 0f) || float.IsNaN(dt) || float.IsInfinity(dt)) return;

        _samples.Enqueue(dt);
        _sum += dt;

        // Keep the smallest most-recent set whose total still covers the window (always retain the newest).
        while (_samples.Count > 1 && _sum - _samples.Peek() >= _window)
            _sum -= _samples.Dequeue();

        Recompute();
    }

    void Recompute()
    {
        int n = _samples.Count;
        if (n == 0)
        {
            _fps = _avgMs = _minMs = _maxMs = 0f;
            return;
        }

        float min = float.MaxValue;
        float max = 0f;
        foreach (float dt in _samples)
        {
            if (dt < min) min = dt;
            if (dt > max) max = dt;
        }

        _fps = _sum > 0f ? n / _sum : 0f;
        _avgMs = _sum / n * 1000f;
        _minMs = min * 1000f;
        _maxMs = max * 1000f;
    }

    /// <summary>Frames per second over the window (frame count / summed dt). 0 before any sample.</summary>
    public float Fps => _fps;

    /// <summary>Mean frame time over the window, in milliseconds. 0 before any sample.</summary>
    public float FrameMsAvg => _avgMs;

    /// <summary>Fastest (smallest) frame time in the window, in milliseconds. 0 before any sample.</summary>
    public float FrameMsMin => _minMs;

    /// <summary>Slowest (largest) frame time in the window, in milliseconds. 0 before any sample.</summary>
    public float FrameMsMax => _maxMs;

    /// <summary>Current managed heap size in bytes (<see cref="GC.GetTotalMemory(bool)"/>, no collection forced).</summary>
    public long ManagedBytes => GC.GetTotalMemory(false);
}
