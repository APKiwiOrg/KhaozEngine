using System;

#nullable enable

namespace KhaozEngine.Updates;

public sealed partial class UpdateService
{
    // RecheckInterval resolved to seconds once at construction. <= 0 means periodic recheck is off
    // (RecheckInterval null or non-positive). Cached so the hot Tick path is field reads only.
    private readonly double recheckIntervalSeconds;

    // Seconds of contiguous Idle time observed by Tick since the last check. Owned by the game-loop
    // thread that calls Tick. CheckForUpdateAsync also zeroes it so a manual check restarts the clock.
    private double recheckAccumulator;

    // Set by Dispose so a late Tick from the game loop becomes a no-op.
    private bool disposed;

    /// <summary>
    /// Advances the opt-in periodic recheck clock by <paramref name="dtSeconds"/> and fires one re-check
    /// when it reaches <see cref="UpdateServiceOptions.RecheckInterval"/>. No-op unless that interval is
    /// set to a positive value and the service is still live. Time only accrues while the service is
    /// <see cref="UpdateState.Idle"/>: any other state (an in-flight, offered, downloading, ready, applying,
    /// or failed update) zeroes the clock, so a fresh full interval is required after the flow returns to
    /// Idle rather than an instant re-probe. On reaching the interval it resets the clock and starts a
    /// fire-and-forget <see cref="CheckForUpdateAsync"/> with the usual failure-swallowing semantics (a
    /// down feed just rests at Idle). Call once per frame from the game loop thread. When
    /// <see cref="UpdateServiceOptions.RecheckInterval"/> is set, call <see cref="CheckForUpdateAsync"/>
    /// from that same thread too: the recheck clock is game-loop-thread-owned and a manual check resets it.
    /// Allocation-free while accumulating. A negative or NaN <paramref name="dtSeconds"/> is treated as zero.
    /// </summary>
    public void Tick(float dtSeconds)
    {
        if (disposed || recheckIntervalSeconds <= 0.0)
        {
            return;
        }

        // Only accrue contiguous Idle time. Any excursion zeroes the clock.
        if (state != UpdateState.Idle)
        {
            recheckAccumulator = 0.0;
            return;
        }

        // A negative or NaN delta (first frame, a paused step, a stall) counts as zero: NaN and negatives
        // both fail this comparison, so they never advance or poison the clock.
        if (dtSeconds > 0f)
        {
            recheckAccumulator += dtSeconds;
        }

        if (recheckAccumulator >= recheckIntervalSeconds)
        {
            recheckAccumulator = 0.0;
            // Fire-and-forget. CheckForUpdateAsync synchronously moves Idle -> Checking before its first
            // await (verified), so the next Tick observes a non-Idle state and this can never start two
            // overlapping checks. Failures are swallowed inside CheckForUpdateAsync (offline-safe -> Idle).
            _ = CheckForUpdateAsync();
        }
    }
}
