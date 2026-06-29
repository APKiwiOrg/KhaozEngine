using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Opt-in server-side anti-cheat / input-hardening knobs, shared by <see cref="WorldServer"/> and
/// <see cref="ShardedWorldServer"/>. Every field is off by default, so an existing consumer's behaviour is
/// unchanged until it tightens these; the per-18-byte move wire format and the authoritative movement model are
/// untouched. The signals surface through the server's <c>OnSuspiciousActivity</c> hook (the engine signals, the
/// game decides policy). NaN/Inf packet rejection is always on (in the move decode) and needs no configuration.
/// </summary>
public sealed class AntiCheatConfig
{
    // --- Per-connection message rate limit (flood protection). Disabled by default. ---

    /// <summary>Sustained inbound message budget per connection, in messages per second. 0 (default) = unlimited
    /// (no rate limiting). Over-budget messages are dropped and flagged <see cref="SuspiciousReason.RateLimited"/>.</summary>
    public float MaxMessagesPerSecond { get; init; }

    /// <summary>Burst allowance: the maximum number of messages a connection may send back-to-back before the
    /// per-second budget throttles it. 0 (default) means one second of <see cref="MaxMessagesPerSecond"/>. Ignored
    /// when rate limiting is disabled.</summary>
    public float MessageBurst { get; init; }

    /// <summary>When true, a connection that trips the rate limit is disconnected (in addition to dropping the
    /// message and raising the signal). Default false: signal only, so the game owns the kick/ban policy.</summary>
    public bool DisconnectOnRateLimit { get; init; }

    // --- Movement-correction anomaly. Disabled by default. ---

    /// <summary>Per-tick authoritative correction distance (world units) above which a tick counts as "corrected":
    /// the client's intended move was denied at least this far by the slope gate, static collision, or play-area
    /// bound. 0 (default) = disabled. A legitimate player brushing a wall produces brief, isolated corrections; a
    /// cheat that hammers the constraints produces a sustained streak.</summary>
    public float MaxCorrectionDistance { get; init; }

    /// <summary>Number of consecutive corrected ticks before <see cref="SuspiciousReason.MovementCorrection"/> is
    /// raised (then the streak resets, so the signal is not re-raised every tick). Only used when
    /// <see cref="MaxCorrectionDistance"/> &gt; 0. Default 10.</summary>
    public int CorrectionStreak { get; init; } = 10;

    /// <summary>True when the per-connection message rate limit is active.</summary>
    public bool RateLimitEnabled => MaxMessagesPerSecond > 0f;

    /// <summary>True when movement-correction anomaly detection is active.</summary>
    public bool CorrectionEnabled => MaxCorrectionDistance > 0f && CorrectionStreak > 0;

    /// <summary>Builds a fresh per-connection token bucket for the configured rate, or null when disabled. The
    /// per-second budget is converted to a per-poll (per-tick) refill via <paramref name="tickSeconds"/>.</summary>
    internal RateLimiter? CreateLimiter(float tickSeconds)
    {
        if (!RateLimitEnabled) return null;
        double refillPerStep = MaxMessagesPerSecond * tickSeconds;
        double capacity = MessageBurst > 0f ? MessageBurst : MaxMessagesPerSecond;
        if (capacity < 1.0) capacity = 1.0;   // always allow at least a single message in a bucket
        return new RateLimiter(capacity, refillPerStep);
    }
}
