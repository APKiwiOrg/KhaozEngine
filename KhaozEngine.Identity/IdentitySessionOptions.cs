using System;

namespace KhaozEngine.Identity;

/// <summary>Tuning knobs for <see cref="IdentitySession"/>.</summary>
public sealed class IdentitySessionOptions
{
    /// <summary>How long after <c>LastAuthenticatedUtc</c> an expired session token is still tolerated offline
    /// (<see cref="IdentityStatus.OfflineGrace"/>) before requiring an interactive sign-in.</summary>
    public TimeSpan OfflineGraceWindow { get; init; } = TimeSpan.FromDays(14);
}
