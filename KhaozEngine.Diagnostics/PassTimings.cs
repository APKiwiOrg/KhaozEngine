using System;
using System.Collections.Generic;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// A headless per-pass CPU-encode timing meter: feed it one named <see cref="Sample"/> per pass per frame and
/// read back the rolling avg/min/max milliseconds for that pass over a window (default ~1 second), same shape
/// as <see cref="FrameStats"/> but keyed by pass name instead of a single whole-frame number. Pure managed code,
/// no renderer or GPU dependency, so it constructs and unit-tests from a synthetic millisecond stream.
/// <para>
/// What this measures: CPU time spent RECORDING (encoding) commands for a pass, i.e. the wall-clock span a
/// <c>Stopwatch</c> bracket placed around that pass's command-list calls observes. It is NOT true GPU execution
/// time: the GPU pipeline is asynchronous, so a fast encode can precede slow GPU work (or vice versa) and this
/// meter cannot see that. The engine's GPU seam exposes no timestamp-query API, so true per-pass GPU
/// timestamps are out of scope until one is added, see
/// <c>docs/USING-KHAOZENGINE.md</c> for the full explanation of what is and is not measured.
/// </para>
/// <para>
/// Intended to back a "Pass timings" section in <c>KhaozEngine.Gui.DiagnosticsOverlay</c>
/// (<see cref="KhaozEngine.Diagnostics"/> stays GPU-free; the renderer that owns the pass boundaries feeds this
/// type from its own public per-pass millisecond readout, e.g. <c>Scene3D</c>'s pass-timing properties).
/// </para>
/// </summary>
public sealed class PassTimings
{
    readonly float _window;
    readonly Dictionary<string, PassMeter> _passes = new(StringComparer.Ordinal);
    // Preserves first-seen order so a section populator lists passes in a stable, meaningful order
    // (e.g. shadow -> model -> transparents -> post -> present) rather than dictionary-iteration order.
    readonly List<string> _order = new();

    /// <summary>Create a meter whose rolling window spans <paramref name="windowSeconds"/> (defaults to 1s).</summary>
    public PassTimings(float windowSeconds = 1f) =>
        _window = windowSeconds > 0f ? windowSeconds : 1f;

    /// <summary>
    /// Record one pass's elapsed time (milliseconds) for this frame and recompute that pass's aggregates.
    /// Non-positive, NaN, or infinite <paramref name="ms"/> values are ignored. A never-sampled
    /// <paramref name="pass"/> name is fine: first use creates its meter (order-of-first-use is preserved in
    /// <see cref="PassNames"/>).
    /// </summary>
    public void Sample(string pass, float ms)
    {
        if (string.IsNullOrEmpty(pass)) return;
        if (!(ms > 0f) || float.IsNaN(ms) || float.IsInfinity(ms)) return;

        if (!_passes.TryGetValue(pass, out PassMeter? meter))
        {
            meter = new PassMeter(_window);
            _passes[pass] = meter;
            _order.Add(pass);
        }
        meter.Sample(ms);
    }

    /// <summary>Pass names in first-sampled order (stable across frames once every pass has been seen once).</summary>
    public IReadOnlyList<string> PassNames => _order;

    /// <summary>Mean pass time over the window, in milliseconds. 0 for a pass never sampled.</summary>
    public float AvgMs(string pass) => _passes.TryGetValue(pass, out var m) ? m.AvgMs : 0f;

    /// <summary>Fastest (smallest) pass time in the window, in milliseconds. 0 for a pass never sampled.</summary>
    public float MinMs(string pass) => _passes.TryGetValue(pass, out var m) ? m.MinMs : 0f;

    /// <summary>Slowest (largest) pass time in the window, in milliseconds. 0 for a pass never sampled.</summary>
    public float MaxMs(string pass) => _passes.TryGetValue(pass, out var m) ? m.MaxMs : 0f;

    /// <summary>Forget every pass and sample (as if freshly constructed). Does not change the window length.</summary>
    public void Reset()
    {
        _passes.Clear();
        _order.Clear();
    }

    // One pass's rolling window, same trim/recompute shape as FrameStats but without the FPS derivation
    // (a per-pass "frames per second" is meaningless).
    sealed class PassMeter
    {
        readonly float _window;
        readonly Queue<float> _samples = new();
        float _sum;
        float _avgMs, _minMs, _maxMs;

        public PassMeter(float window) => _window = window;

        public void Sample(float ms)
        {
            _samples.Enqueue(ms);
            _sum += ms;
            while (_samples.Count > 1 && _sum - _samples.Peek() >= _window * 1000f)
                _sum -= _samples.Dequeue();
            Recompute();
        }

        void Recompute()
        {
            int n = _samples.Count;
            if (n == 0) { _avgMs = _minMs = _maxMs = 0f; return; }

            float min = float.MaxValue;
            float max = 0f;
            foreach (float ms in _samples)
            {
                if (ms < min) min = ms;
                if (ms > max) max = ms;
            }
            _avgMs = _sum / n;
            _minMs = min;
            _maxMs = max;
        }

        public float AvgMs => _avgMs;
        public float MinMs => _minMs;
        public float MaxMs => _maxMs;
    }
}
