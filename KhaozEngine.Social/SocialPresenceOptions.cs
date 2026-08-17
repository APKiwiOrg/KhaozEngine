using System;

namespace KhaozEngine.Social;

/// <summary>
/// Tuning for <see cref="SocialPresenceController"/>: how often an unchanged presence is re-published,
/// and how hard the controller tries to reach a platform client that is not up yet.
/// </summary>
/// <remarks>
/// The connect-retry defaults are shaped around the case they exist for: the player launched the game
/// before the platform client finished starting. Attempts run at roughly 0s, 3s, 9s, 21s, 45s, 1m33s,
/// 2m33s and 3m33s, so a Discord that appears anywhere in the first few minutes still gets presence,
/// and a machine with no Discord at all stops being asked after eight tries instead of polling for the
/// whole session. Every value is clamped to something usable, so a nonsensical setting degrades rather
/// than throws: every span here is pulled into [0, 1 day], which is well past anything that can serve a
/// client starting alongside the game and keeps the schedule off the date arithmetic that an unbounded
/// wait overflows.
/// </remarks>
public sealed class SocialPresenceOptions
{
    /// <summary>Minimum wall-clock time before an unchanged presence is re-published. Default 15s.</summary>
    public TimeSpan RepublishInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Wait before the second connect attempt. Each later wait multiplies it. Default 3s, clamped to
    /// [0, 1 day].
    /// </summary>
    public TimeSpan ConnectRetryDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Ceiling the growing wait stops at, so a long session never idles for minutes. Default 60s, clamped to
    /// [0, 1 day], so <see cref="TimeSpan.MaxValue"/> reads as "no cap" and degrades to the day rather than
    /// overflowing the schedule.
    /// </summary>
    public TimeSpan MaxConnectRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Multiplier applied to the wait after each failed attempt. Values below 1 are treated as 1. Default 2.</summary>
    public double ConnectRetryBackoff { get; init; } = 2.0;

    /// <summary>
    /// Total connect attempts before the controller gives up, counting the first. Set it to 1 for the
    /// old one-shot behaviour: fail once, never retry. Default 8.
    /// </summary>
    public int MaxConnectAttempts { get; init; } = 8;

    /// <summary>
    /// How long a connection has to last before the drop that ends it counts as a real session ending
    /// rather than a flap. A session that held at least this long gets a fresh
    /// <see cref="MaxConnectAttempts"/> budget on the way back, and a shorter one carries its spent
    /// attempts forward, so a platform client that accepts every connect and loses it again immediately
    /// still runs out of budget instead of cycling for the rest of the process. Default 30s, clamped to
    /// [0, 1 day]. Zero opts out: every drop is then treated as a held session.
    /// </summary>
    public TimeSpan StableConnectionSpan { get; init; } = TimeSpan.FromSeconds(30);
}
