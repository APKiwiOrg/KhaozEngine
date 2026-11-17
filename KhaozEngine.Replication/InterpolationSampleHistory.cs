using System;
using System.Collections.Generic;

namespace KhaozEngine.Replication;

internal readonly record struct InterpolationSample(double Time, byte[] Bytes, long? ServerTick);

// Time-ordered history for one component. Pruning leaves the drawn segment at the front until the next ingest
// or presentation, so a typed read uses the same endpoints without searching again or retaining extra samples.
internal sealed class InterpolationSampleHistory
{
    // ~20 s at 30 Hz is a backstop for consumers that ingest without presenting.
    private const int MaxHistorySamples = 600;
    private readonly List<InterpolationSample> samples = new();

    public IReadOnlyList<InterpolationSample> Samples => samples;
    public bool HasDrawnSegment { get; private set; }

    public void Record(double time, byte[] bytes, long? serverTick)
    {
        ForgetDrawnSegment();
        var sample = new InterpolationSample(time, bytes, serverTick);
        if (samples.Count > 0 && time <= samples[^1].Time) samples[^1] = sample;
        else samples.Add(sample);
        if (samples.Count > MaxHistorySamples) samples.RemoveRange(0, samples.Count - MaxHistorySamples);
    }

    public void ForgetDrawnSegment() => HasDrawnSegment = false;

    public (InterpolationSample From, InterpolationSample To, float Fraction, bool Held) Resolve(double renderTime)
    {
        // The last sample at or before renderTime is the lower bracket.
        int lower = -1;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Time <= renderTime) lower = i;
            else break;
        }

        if (lower < 0)
        {
            InterpolationSample oldest = samples[0];
            return (oldest, oldest, 0f, false);
        }

        InterpolationSample from = samples[lower];
        bool hasUpper = lower < samples.Count - 1;
        InterpolationSample to = hasUpper ? samples[lower + 1] : from;
        if (lower > 0) samples.RemoveRange(0, lower);
        HasDrawnSegment = hasUpper;
        if (!hasUpper) return (from, from, 0f, renderTime > from.Time + 1e-9);

        double span = to.Time - from.Time;
        float fraction = span > 0 ? (float)Math.Clamp((renderTime - from.Time) / span, 0.0, 1.0) : 1f;
        return (from, to, fraction, false);
    }

    public void SnapToNewest()
    {
        ForgetDrawnSegment();
        if (samples.Count > 1) samples.RemoveRange(0, samples.Count - 1);
    }
}
