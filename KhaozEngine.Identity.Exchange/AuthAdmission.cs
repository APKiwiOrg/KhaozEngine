using System;
using KhaozEngine.Accounts;

namespace KhaozEngine.Identity.Exchange;

/// <summary>
/// The fixed gate every exchange runs, public so a game's own issuer (a local development profile, say) admits in
/// the same order.
/// </summary>
/// <remarks>
/// A ban in force refuses first and a missing whitelist flag second. The order is a disclosure property: a banned
/// player learns only that they are banned, never whether they were also whitelisted. That is why no policy can
/// reorder it.
/// </remarks>
public static class AuthAdmission
{
    /// <summary>
    /// Decides whether <paramref name="account"/> may have a token at <paramref name="now"/>.
    /// </summary>
    /// <param name="account">The caller's own account.</param>
    /// <param name="now">The clock a timed ban's expiry is read against. A lapsed ban admits.</param>
    /// <param name="requireWhitelist">Whether an unwhitelisted account is refused.</param>
    /// <returns><see cref="AuthExchangeOutcome.Banned"/>, <see cref="AuthExchangeOutcome.NotWhitelisted"/> or
    /// <see cref="AuthExchangeOutcome.Ok"/>, and nothing else.</returns>
    public static AuthExchangeOutcome Decide(AccountRecord account, DateTimeOffset now, bool requireWhitelist)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.IsBanActive(now)) return AuthExchangeOutcome.Banned;
        if (requireWhitelist && !account.Whitelisted) return AuthExchangeOutcome.NotWhitelisted;
        return AuthExchangeOutcome.Ok;
    }
}
