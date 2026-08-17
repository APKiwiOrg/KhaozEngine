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
/// than throws.
/// </remarks>
public sealed class SocialPresenceOptions
{
    /// <summary>Minimum wall-clock time before an unchanged presence is re-published. Default 15s.</summary>
    public TimeSpan RepublishInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Wait before the second connect attempt. Each later wait multiplies it. Default 3s.</summary>
    public TimeSpan ConnectRetryDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Ceiling the growing wait stops at, so a long session never idles for minutes. Default 60s.</summary>
    public TimeSpan MaxConnectRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Multiplier applied to the wait after each failed attempt. Values below 1 are treated as 1. Default 2.</summary>
    public double ConnectRetryBackoff { get; init; } = 2.0;

    /// <summary>
    /// Total connect attempts before the controller gives up, counting the first. Set it to 1 for the
    /// old one-shot behaviour: fail once, never retry. Default 8.
    /// </summary>
    public int MaxConnectAttempts { get; init; } = 8;
}
