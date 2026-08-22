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
/// <para>This is the LIVE ban path, and it is one of two. <see cref="WorldServer"/> consults it in its join
/// handler, AFTER the authenticator admitted the peer, and kicks with a typed
/// <c>ServerNotice(ServerNoticeKind.Banned)</c> followed by a disconnect, so a ban applied MID-SESSION takes
/// effect on the next join and a game banned-player banner has a typed notice to render.
/// <c>KhaozEngine.Netcode.BanGateAuthenticator</c> is the other path: it refuses a subject the head ALREADY
/// knows is banned during AUTHENTICATION, with the <c>ke:banned</c> wire reason, before any join happens, so a
/// client reads it as a refused connect. It takes a <c>Func&lt;string,bool&gt;</c> rather than this interface
/// because <c>KhaozEngine.Netcode</c> cannot reference this package. A <see cref="WorldServer"/> game that wants
/// both wires the SAME store behind both, passing it as <c>banStore:</c> here and handing <see cref="IsBanned"/>
/// to the gate, so the two can never disagree about who is banned.</para>
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
