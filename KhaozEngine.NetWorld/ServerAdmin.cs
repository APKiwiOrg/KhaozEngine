using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Transport-agnostic admin facade composing the three admin capabilities: live commands over any
/// <see cref="IAdminControllable"/> (WorldServer or ShardedWorldServer), an optional <see cref="IBanStore"/>, and an
/// optional <see cref="IEnumerableWorldStore"/> for listing persisted accounts. This is the in-process embodiment of
/// the admin surface; <c>KhaozEngine.Server.Admin</c> is a thin HTTPS shell over it. Banning an online account here
/// persists the ban and then kicks the player. Capabilities not wired (null bans/accounts) throw
/// <see cref="NotSupportedException"/> so a caller can feature-detect via <see cref="BansSupported"/> /
/// <see cref="AccountsSupported"/>.
/// </summary>
public sealed class ServerAdmin
{
    private readonly IAdminControllable server;
    private readonly IBanStore? bans;
    private readonly IEnumerableWorldStore? accounts;

    public ServerAdmin(IAdminControllable server, IBanStore? bans = null, IEnumerableWorldStore? accounts = null)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        this.bans = bans;
        this.accounts = accounts;
    }

    /// <summary>True if a ban store was wired (ban/unban/list-bans are available).</summary>
    public bool BansSupported => bans is not null;
    /// <summary>True if an enumerable account store was wired (list-accounts is available).</summary>
    public bool AccountsSupported => accounts is not null;

    public IReadOnlyList<OnlinePlayer> ListOnline() => server.ListOnline();
    public void Teleport(PlayerRef target, Vector3 position) => server.Teleport(target, position);
    public void Kick(PlayerRef target, string reason) => server.Kick(target, reason);
    public void Broadcast(string text) => server.Broadcast(text);

    /// <summary>Persists a ban then kicks the account if it is currently online (no-op if offline).</summary>
    public async ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null, CancellationToken ct = default)
    {
        IBanStore store = bans ?? throw new NotSupportedException("No ban store configured.");
        await store.BanAsync(accountId, reason, until, ct).ConfigureAwait(false);
        server.Kick(PlayerRef.Account(accountId), reason);
    }

    public ValueTask UnbanAsync(string accountId, CancellationToken ct = default)
        => (bans ?? throw new NotSupportedException("No ban store configured.")).UnbanAsync(accountId, ct);

    public IReadOnlyCollection<BanRecord> ListBans()
        => (bans ?? throw new NotSupportedException("No ban store configured.")).ListBans();

    /// <summary>Materializes <see cref="IEnumerableWorldStore.EnumerateAsync"/> into a list (admin "list accounts").</summary>
    public async Task<IReadOnlyList<WorldStoreEntry>> ListAccountsAsync(string? keyPrefix = null, CancellationToken ct = default)
    {
        IEnumerableWorldStore store = accounts ?? throw new NotSupportedException("No enumerable account store configured.");
        var list = new List<WorldStoreEntry>();
        await foreach (WorldStoreEntry e in store.EnumerateAsync(keyPrefix, ct).ConfigureAwait(false)) list.Add(e);
        return list;
    }
}
