using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Primitives
{
    /// <summary>One timed trail sample: a world <see cref="Position"/> captured at a monotonic
    /// <see cref="TimeSeconds"/> clock value. Age at query time is <c>nowSeconds - TimeSeconds</c>.</summary>
    public readonly record struct TrailPoint(Vector3 Position, float TimeSeconds);

    /// <summary>
    /// Pure, render-free ring of timed trail samples bounded by a max age and a max count: feed it the moving
    /// emitter's world position each frame (a sword tip, a thruster nozzle, a projectile), and read back the live
    /// tail as an oldest-first span to hand to <c>Scene3D.DrawTrail</c>. Older-than-<see cref="MaxAgeSeconds"/>
    /// samples and any beyond <see cref="MaxCount"/> are evicted from the oldest end on <see cref="Add"/> (and aged
    /// out by <see cref="Prune"/> when the emitter idles). No GPU or render dependency; fully headless-testable.
    /// Samples must be added with a non-decreasing time.
    /// </summary>
    public sealed class TrailSampler
    {
        readonly List<TrailPoint> _points = new();

        /// <summary>Create a sampler keeping samples younger than <paramref name="maxAgeSeconds"/> and no more than
        /// <paramref name="maxCount"/> of them (whichever bound is tighter each frame).</summary>
        public TrailSampler(float maxAgeSeconds, int maxCount)
        {
            MaxAgeSeconds = maxAgeSeconds;
            MaxCount = maxCount;
        }

        /// <summary>Samples older than this (age in seconds strictly greater) are evicted.</summary>
        public float MaxAgeSeconds { get; }

        /// <summary>The most samples retained; the oldest are evicted once this is exceeded.</summary>
        public int MaxCount { get; }

        /// <summary>Live sample count.</summary>
        public int Count => _points.Count;

        /// <summary>The live samples, oldest-first (tail to head). A view over internal storage; valid until the
        /// next mutating call.</summary>
        public ReadOnlySpan<TrailPoint> Samples => CollectionsMarshal.AsSpan(_points);

        /// <summary>Append a sample at <paramref name="nowSeconds"/> (must be &gt;= the last added time), then evict
        /// any now older than <see cref="MaxAgeSeconds"/> and any beyond <see cref="MaxCount"/>, oldest first.</summary>
        public void Add(Vector3 position, float nowSeconds)
        {
            Debug.Assert(_points.Count == 0 || nowSeconds >= _points[^1].TimeSeconds,
                "TrailSampler.Add requires a non-decreasing nowSeconds");
            _points.Add(new TrailPoint(position, nowSeconds));
            int drop = Math.Max(AgedPrefix(nowSeconds), _points.Count - MaxCount);
            if (drop > 0) _points.RemoveRange(0, drop);
        }

        /// <summary>Evict samples aged out against <paramref name="nowSeconds"/> without adding one (call while the
        /// emitter idles so the tail still decays). Returns the surviving <see cref="Count"/>.</summary>
        public int Prune(float nowSeconds)
        {
            int drop = AgedPrefix(nowSeconds);
            if (drop > 0) _points.RemoveRange(0, drop);
            return _points.Count;
        }

        /// <summary>Drop every sample (e.g. when the swing ends or the emitter teleports).</summary>
        public void Clear() => _points.Clear();

        // Count of leading (oldest) samples whose age exceeds MaxAge. Times are non-decreasing, so ages are
        // non-increasing along the list: the aged samples are always a contiguous prefix.
        int AgedPrefix(float nowSeconds)
        {
            int i = 0;
            while (i < _points.Count && (nowSeconds - _points[i].TimeSeconds) > MaxAgeSeconds) i++;
            return i;
        }
    }
}
