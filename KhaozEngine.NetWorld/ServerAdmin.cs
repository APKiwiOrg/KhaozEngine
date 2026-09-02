using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
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
public sealed partial class ServerAdmin
{
    private readonly IAdminControllable server;
    private readonly IBanStore? bans;
    private readonly IEnumerableWorldStore? accounts;
    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task<AdminActionResult>>> actions
        = new(StringComparer.Ordinal);

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

    /// <summary>Persists a ban then kicks the account if it is currently online (no-op if offline). A TOKENLESS
    /// connection's id (<see cref="ResumePositionCache.GuestAccountPrefix"/>) is refused: it names a seat rather
    /// than a person, so banning it punishes whoever is seated there next. Use <see cref="Kick"/> on the
    /// <see cref="PlayerRef.Slot"/> for a player with no durable identity.</summary>
    /// <exception cref="ArgumentException"><paramref name="accountId"/> is a tokenless connection's id.</exception>
    public async ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null, CancellationToken ct = default)
    {
        IBanStore store = bans ?? throw new NotSupportedException("No ban store configured.");
        // Both heads derive a tokenless connection's account id as guest:{slot}, and the allocator recycles that
        // slot to the next connection. A ban filed under it therefore rejects every future tokenless player seated
        // there while the one who earned it reconnects onto another slot and carries on. There is nothing durable
        // to ban, and saying so beats banning a chair.
        if (ResumePositionCache.IsGuestAccount(accountId))
            throw new ArgumentException(
                $"'{accountId}' is a tokenless connection, whose id names a seat rather than a player, so a ban on " +
                "it would land on whoever is seated there next. Kick the slot instead.", nameof(accountId));
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

    /// <summary>
    /// Registers a game-supplied admin action under <paramref name="name"/>, dispatched by the admin endpoint on
    /// <c>GET</c> / <c>POST /actions/{name}</c>. The handler runs on the caller's thread (an HTTP request thread), so it
    /// must never touch simulation state directly: enqueue mutations to the host thread and return published snapshots
    /// for reads, exactly like <see cref="IAdminControllable"/>. The cancellation token is the request's
    /// <c>RequestAborted</c>, honoured at the handler's discretion (a query may abort on client disconnect, a mutation
    /// it enqueues should not). <paramref name="name"/> must match <c>^[a-z0-9][a-z0-9-]{0,63}$</c>. Registrations
    /// normally happen before the endpoint starts, but the backing store is a <see cref="ConcurrentDictionary{TKey, TValue}"/>
    /// so registering from any thread is safe.
    /// </summary>
    /// <exception cref="ArgumentException">The name is invalid or already registered.</exception>
    public void RegisterAction(string name, Func<JsonElement?, CancellationToken, Task<AdminActionResult>> handler)
    {
        ValidateActionName(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (!actions.TryAdd(name, handler))
            throw new ArgumentException($"An admin action named '{name}' is already registered.", nameof(name));
    }

    /// <summary>
    /// Registers a synchronous admin action, a convenience wrapper over the async overload for handlers that read a
    /// published snapshot or enqueue a command without awaiting. Same name rule and threading contract apply.
    /// </summary>
    /// <exception cref="ArgumentException">The name is invalid or already registered.</exception>
    public void RegisterAction(string name, Func<JsonElement?, AdminActionResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterAction(name, (payload, _) => Task.FromResult(handler(payload)));
    }

    /// <summary>The names of every registered action, in no particular order (a snapshot of the registry).</summary>
    public IReadOnlyCollection<string> ActionNames => actions.Keys.ToArray();

    /// <summary>Looks up a registered action's handler by name.</summary>
    public bool TryGetAction(string name, [MaybeNullWhen(false)] out Func<JsonElement?, CancellationToken, Task<AdminActionResult>> handler)
        => actions.TryGetValue(name, out handler);

    private static void ValidateActionName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!ActionNamePattern().IsMatch(name))
            throw new ArgumentException(
                $"Invalid admin action name '{name}'. Names must match ^[a-z0-9][a-z0-9-]{{0,63}}$.", nameof(name));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ActionNamePattern();
}
