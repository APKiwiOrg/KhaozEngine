using System;

namespace KhaozEngine.NetWorld;

/// <summary>Exponential backoff schedule for <see cref="WorldClient"/> auto-reconnect.</summary>
public sealed class ReconnectBackoff
{
    /// <summary>Delay before the first reconnect attempt, seconds.</summary>
    public float InitialSeconds { get; init; } = 0.5f;
    /// <summary>Per-attempt multiplier on the delay.</summary>
    public float Multiplier { get; init; } = 2f;
    /// <summary>Ceiling on the per-attempt delay, seconds.</summary>
    public float MaxSeconds { get; init; } = 5f;
    /// <summary>Maximum reconnect attempts before giving up (0 = unlimited).</summary>
    public int MaxAttempts { get; init; } = 0;

    public static ReconnectBackoff Default => new();

    /// <summary>The delay before attempt number <paramref name="attempt"/> (1-based), clamped to <see cref="MaxSeconds"/>.</summary>
    public float DelayForAttempt(int attempt)
    {
        double d = InitialSeconds * Math.Pow(Multiplier, Math.Max(0, attempt - 1));
        return (float)Math.Min(d, MaxSeconds);
    }
}
