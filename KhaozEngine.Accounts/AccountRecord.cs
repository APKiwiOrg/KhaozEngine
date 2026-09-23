using System;

namespace KhaozEngine.Accounts;

/// <summary>
/// A ban filed against an account: why, and when it lapses (<c>null</c> is permanent).
/// </summary>
/// <param name="Reason">Why, for the operator reading the account later. Never null, may be empty.</param>
/// <param name="Until">The instant the ban lapses, or <c>null</c> for a permanent ban. The engine stores keep it in
/// UTC.</param>
public readonly record struct AccountBan(string Reason, DateTimeOffset? Until)
{
    /// <summary>Whether the ban is in force at <paramref name="now"/>: permanent, or lapsing strictly after it.</summary>
    /// <param name="now">The clock, passed in so expiry is testable without waiting for one.</param>
    public bool IsActive(DateTimeOffset now) => Until is not { } until || until > now;
}

/// <summary>
/// One account as the store holds it: who, the name last seen, whether it is past the whitelist gate, and the
/// filed ban.
/// </summary>
/// <remarks>
/// <see cref="Ban"/> is the FILED ban, not the active one. A timed ban stays filed after it lapses, which is what
/// lets an operator see that an account was banned last month and came back, so every gate asks
/// <see cref="IsBanActive"/> rather than testing <see cref="Ban"/> for null, and a lapsed ban admits the player
/// without anyone having to run an unban.
/// </remarks>
/// <param name="Subject">The stable verified subject the store minted. The key every other engine surface uses:
/// the <c>SignedToken</c> subject, the ban key, the persisted account id.</param>
/// <param name="DisplayName">The name last seen from the provider, or <c>null</c> when it never gave one.</param>
/// <param name="Whitelisted">Whether this account is past the whitelist gate.</param>
/// <param name="Ban">The filed ban, or <c>null</c> when none is filed.</param>
public sealed record AccountRecord(string Subject, string? DisplayName, bool Whitelisted, AccountBan? Ban)
{
    /// <summary>Whether a ban is filed and in force at <paramref name="now"/>.</summary>
    /// <param name="now">The clock, passed in so expiry is testable without waiting for one.</param>
    public bool IsBanActive(DateTimeOffset now) => Ban is { } ban && ban.IsActive(now);
}
