using System;

namespace KhaozEngine.Netcode;

/// <summary>
/// A deterministic token-bucket rate limiter for one connection's inbound messages. The server <see cref="Refill"/>s
/// it once per poll (adding <see cref="RefillPerStep"/> tokens, clamped to <see cref="Capacity"/>) and calls
/// <see cref="TryConsume"/> once per message; an empty bucket rejects the message. There is no wall-clock
/// dependency - the budget is expressed per step, so the limiter is headless and reproducible. The bucket starts
/// full so a freshly-joined client is not throttled before its first refill. The per-second budget is converted to
/// a per-step budget by the caller (<c>RefillPerStep = MaxMessagesPerSecond * TickSeconds</c>), and
/// <see cref="Capacity"/> is the allowed burst.
/// </summary>
public sealed class RateLimiter
{
    private double tokens;

    /// <param name="capacity">Maximum tokens the bucket holds (the burst allowance). Must be &gt;= 0.</param>
    /// <param name="refillPerStep">Tokens added on each <see cref="Refill"/> call. Must be &gt;= 0.</param>
    public RateLimiter(double capacity, double refillPerStep)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (refillPerStep < 0) throw new ArgumentOutOfRangeException(nameof(refillPerStep));
        Capacity = capacity;
        RefillPerStep = refillPerStep;
        tokens = capacity;   // start full: a new connection is not throttled before its first refill
    }

    /// <summary>Maximum tokens the bucket holds (the burst allowance).</summary>
    public double Capacity { get; }

    /// <summary>Tokens added on each <see cref="Refill"/> call (the per-step budget).</summary>
    public double RefillPerStep { get; }

    /// <summary>Current available tokens.</summary>
    public double Tokens => tokens;

    /// <summary>Adds <see cref="RefillPerStep"/> tokens, clamped to <see cref="Capacity"/>. Call once per poll.</summary>
    public void Refill()
    {
        tokens += RefillPerStep;
        if (tokens > Capacity) tokens = Capacity;
    }

    /// <summary>Consumes one token. True if a whole token was available (the message is allowed); false if the
    /// bucket is empty (the message should be dropped / flagged).</summary>
    public bool TryConsume()
    {
        if (tokens >= 1.0)
        {
            tokens -= 1.0;
            return true;
        }
        return false;
    }
}
