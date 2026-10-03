using System;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The fixed replication cadence at the configured tick length, driven by elapsed host time. Independent of render
/// frames, of how often the host calls in and of whether a simulation sub-tick ran. Short calls accumulate their
/// elapsed time. A call that crosses one or more cadence boundaries grants exactly one send allowance and advances
/// <see cref="Tick"/> by every boundary crossed, so deadlines see the skipped time while no burst of sends is
/// manufactured. A zero, negative or non-finite elapsed time is a drain call and consumes nothing.
/// </summary>
internal sealed class ReplicationCadence
{
    private readonly double tickSeconds;
    private double accumulated;

    /// <param name="tickSeconds">The cadence length in seconds. Positive and finite.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tickSeconds"/> is not positive and finite.</exception>
    internal ReplicationCadence(float tickSeconds)
    {
        if (!float.IsFinite(tickSeconds) || tickSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), tickSeconds, "Must be positive and finite.");
        this.tickSeconds = tickSeconds;
    }

    /// <summary>Cadence boundaries crossed so far, skipped ones included.</summary>
    internal long Tick { get; private set; }

    /// <summary>Adds elapsed host time.</summary>
    /// <param name="elapsedSeconds">Time since the previous call.</param>
    /// <returns>True when this call crossed at least one boundary and grants one allowance.</returns>
    internal bool Advance(float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds <= 0f) return false;
        accumulated += elapsedSeconds;
        double boundaries = Math.Floor(accumulated / tickSeconds);
        if (boundaries < 1.0) return false;
        Tick += (long)boundaries;
        accumulated -= boundaries * tickSeconds;
        if (accumulated < 0.0) accumulated = 0.0;
        return true;
    }
}
