using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.NetWorld;

/// <summary>One ban: the account, why, and an optional expiry (null = permanent).</summary>
public readonly record struct BanRecord(string AccountId, string Reason, DateTimeOffset? Until);

/// <summary>
/// Generic account ban seam. A server consults <see cref="IsBanned"/> on the host thread at connect (alongside the
/// <see cref="KhaozEngine.Netcode.IConnectionAuthenticator"/>), so it is synchronous and must be cheap. Mutators are
/// async so a database-backed store can persist honestly. Bans key on the verified account id (the authenticator's
/// subject); a guest (no stable subject) is not meaningfully bannable.
/// </summary>
public interface IBanStore
{
    /// <summary>True if <paramref name="accountId"/> is currently banned (honoring expiry). Synchronous and fast.</summary>
    bool IsBanned(string accountId);

    /// <summary>Records (or refreshes) a ban. <paramref name="until"/> null = permanent.</summary>
    ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null, CancellationToken cancellationToken = default);

    /// <summary>Removes any ban on <paramref name="accountId"/>.</summary>
    ValueTask UnbanAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>The current (non-expired) bans, for an admin list view.</summary>
    IReadOnlyCollection<BanRecord> ListBans();
}
