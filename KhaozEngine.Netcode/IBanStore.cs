using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// The namespace is NetWorld on purpose. The ban seam physically lives in KhaozEngine.Netcode so the connect gate
// (KhaozEngine.Netcode.BanGateAuthenticator) and the tile server can take it without referencing NetWorld, but its
// full type names are the ones it shipped under. KhaozEngine.NetWorld type-forwards all three here (see its
// TypeForwards.cs), so a consumer that names KhaozEngine.NetWorld.IBanStore keeps compiling and binding unchanged,
// and a file importing both namespaces sees one IBanStore rather than two.
namespace KhaozEngine.NetWorld;

/// <summary>One ban: the account, why, and an optional expiry (null = permanent).</summary>
public readonly record struct BanRecord(string AccountId, string Reason, DateTimeOffset? Until);

/// <summary>
/// Generic account ban seam. A server consults <see cref="IsBanned"/> on the host thread, so it is synchronous and
/// must be cheap. Mutators are async so a database-backed store can persist honestly. Bans key on the verified
/// account id (the authenticator's subject). A guest (no stable subject) is not meaningfully bannable.
/// <para>ONE store serves both ban paths. At the DOOR, <see cref="KhaozEngine.Netcode.BanGateAuthenticator"/>
/// takes this interface and refuses a banned subject during authentication with the <c>ke:banned</c> wire reason,
/// before any join happens, so a client reads it as a refused connect. At JOIN, a <c>WorldServer</c> or
/// <c>ShardedWorldServer</c> handed the store as <c>banStore:</c> checks it after the authenticator admitted the
/// peer and kicks with a typed <c>ServerNotice(ServerNoticeKind.Banned)</c>, which is the route a game
/// banned-player banner renders. Pass the SAME instance to both and the two can never disagree about who is
/// banned. A tile server takes it as <c>TileWorldServerConfig.BanStore</c>, at the door.</para>
/// <para>Defined in the <c>KhaozEngine.Netcode</c> assembly under this namespace, and type-forwarded from
/// <c>KhaozEngine.NetWorld</c>, where it shipped.</para>
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
