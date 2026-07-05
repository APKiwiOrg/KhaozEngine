using System;

namespace KhaozEngine.Progression;

/// <summary>
/// A generic wall-clock periodic-reward availability schedule: "every N of <em>real-world</em> time a
/// reward becomes available and stays available until claimed."
/// </summary>
/// <remarks>
/// <para>
/// Pure value type. It carries no clock and holds no game state: callers pass the current UTC instant
/// in explicitly (production hands it <see cref="DateTimeOffset.UtcNow"/>; tests hand it a fixed
/// instant), so availability is measured against real wall-clock time and is completely immune to game
/// <c>TimeScale</c> and to offline / time-skip catch-up caps.
/// </para>
/// <para>
/// It is <b>persistence-agnostic</b>: a consumer stores the two plain, serializable values
/// (<see cref="Interval"/> and <see cref="NextAvailableUtc"/>) in its own save - typically only
/// <see cref="NextAvailableUtc"/>, with the interval coming from config - and reconstructs the struct on
/// load. All timestamps are normalised to UTC (a local-offset instant is <em>converted</em>, never
/// relabelled), so a schedule round-trips through serialization without drifting.
/// </para>
/// <para>
/// Availability is <b>non-stacking</b>: at most one reward is available at a time regardless of how long
/// the player was away, and <see cref="Claim(DateTimeOffset)"/> schedules the next one exactly one
/// <see cref="Interval"/> after the claim instant. The model is a persistent boolean threshold, not an
/// event, so a backward wall-clock step or an implausibly far-future timestamp can neither brick the
/// schedule nor spam multiple rewards.
/// </para>
/// <para>
/// Support N independent rewards by holding one <see cref="WallClockRewardSchedule"/> per reward id, each
/// with its own <see cref="Interval"/>.
/// </para>
/// </remarks>
public readonly struct WallClockRewardSchedule
{
    /// <summary>The real-world recurrence period between successive availabilities. Always positive for a schedule built via <see cref="Start(TimeSpan, DateTimeOffset, TimeSpan)"/>.</summary>
    public TimeSpan Interval { get; init; }

    /// <summary>
    /// The UTC instant at which the reward becomes (or became) claimable. Availability is
    /// <c>nowUtc &gt;= NextAvailableUtc</c>. This is the single value a consumer needs to persist.
    /// </summary>
    public DateTimeOffset NextAvailableUtc { get; init; }

    /// <summary>True when the reward is claimable at <paramref name="nowUtc"/> (i.e. wall-clock time has reached <see cref="NextAvailableUtc"/>).</summary>
    /// <param name="nowUtc">The current instant. Any offset is honoured by instant; pass <see cref="DateTimeOffset.UtcNow"/> in production.</param>
    public bool IsAvailable(DateTimeOffset nowUtc) => nowUtc >= NextAvailableUtc;

    /// <summary>
    /// The wall-clock time remaining until the reward is available, clamped to
    /// <see cref="TimeSpan.Zero"/> once available (never negative). Handy for a HUD countdown.
    /// </summary>
    /// <param name="nowUtc">The current instant.</param>
    public TimeSpan TimeUntilAvailable(DateTimeOffset nowUtc)
    {
        TimeSpan remaining = NextAvailableUtc - nowUtc;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Consume the currently-available reward and schedule the next one exactly one <see cref="Interval"/>
    /// after <paramref name="nowUtc"/>. Non-stacking: being away for many intervals still yields a single
    /// reward, and the next is due one interval after this claim - not one per elapsed interval. Call only
    /// when <see cref="IsAvailable(DateTimeOffset)"/> is true.
    /// </summary>
    /// <param name="nowUtc">The claim instant (converted to UTC for storage).</param>
    public WallClockRewardSchedule Claim(DateTimeOffset nowUtc)
        => this with { NextAvailableUtc = AddClamped(nowUtc.ToUniversalTime(), Interval) };

    /// <summary>
    /// Seed a fresh schedule whose first reward is available <paramref name="initialDelay"/> after
    /// <paramref name="nowUtc"/>. This is the first-run knob: pass <see cref="TimeSpan.Zero"/> for an
    /// immediate welcome reward, <paramref name="interval"/> to wait a full period, or a random
    /// <c>0..interval</c> offset so the first reward does not always land on the full-interval boundary.
    /// </summary>
    /// <param name="interval">The recurrence period. Must be positive.</param>
    /// <param name="nowUtc">The seeding instant (converted to UTC for storage).</param>
    /// <param name="initialDelay">Delay from <paramref name="nowUtc"/> until the first availability. Must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is not positive, or <paramref name="initialDelay"/> is negative.</exception>
    public static WallClockRewardSchedule Start(TimeSpan interval, DateTimeOffset nowUtc, TimeSpan initialDelay)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Interval must be positive.");
        if (initialDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialDelay), initialDelay, "Initial delay must be non-negative.");

        return new WallClockRewardSchedule
        {
            Interval = interval,
            NextAvailableUtc = AddClamped(nowUtc.ToUniversalTime(), initialDelay),
        };
    }

    /// <summary>
    /// Convenience seeding overload: the first reward is available immediately when
    /// <paramref name="availableImmediately"/> is true, otherwise after a full <paramref name="interval"/>.
    /// </summary>
    /// <param name="interval">The recurrence period. Must be positive.</param>
    /// <param name="nowUtc">The seeding instant (converted to UTC for storage).</param>
    /// <param name="availableImmediately">True to make the first reward available at <paramref name="nowUtc"/>; false to wait one full interval.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is not positive.</exception>
    public static WallClockRewardSchedule Start(TimeSpan interval, DateTimeOffset nowUtc, bool availableImmediately = false)
        => Start(interval, nowUtc, availableImmediately ? TimeSpan.Zero : interval);

    /// <summary>
    /// Adds <paramref name="delta"/> to <paramref name="utc"/>, saturating at
    /// <see cref="DateTimeOffset.MaxValue"/> / <see cref="DateTimeOffset.MinValue"/> instead of throwing on
    /// overflow, so an implausible interval or a near-boundary claim can never brick a schedule with an
    /// <see cref="OverflowException"/>. <paramref name="utc"/> is expected already normalised to UTC, and
    /// the result keeps that zero offset.
    /// </summary>
    private static DateTimeOffset AddClamped(DateTimeOffset utc, TimeSpan delta)
    {
        long maxTicks = DateTimeOffset.MaxValue.UtcTicks;
        long ticks = utc.UtcTicks;
        long d = delta.Ticks;

        if (d >= 0)
        {
            if (ticks > maxTicks - d)
                return DateTimeOffset.MaxValue;
        }
        else
        {
            if (ticks < -d)
                return DateTimeOffset.MinValue;
        }

        return utc.AddTicks(d);
    }
}
