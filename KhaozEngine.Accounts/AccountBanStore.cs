using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Accounts;

/// <summary>
/// The ONE <see cref="IBanStore"/> over an <see cref="IAccountStore"/>, so a game files every ban in the account
/// store and nowhere else. Hand this one instance to every ban consumer: <c>BanGateAuthenticator</c> at the door,
/// <c>WorldServer</c> or <c>ShardedWorldServer</c> as <c>banStore:</c>, <c>TileWorldServerConfig.BanStore</c> and
/// <c>ServerAdmin(bans:)</c>. A game on this seam does not also wire <c>WorldStoreBanStore</c>, whose
/// <c>ban:{accountId}</c> keys would be a second list.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two readers with incompatible needs.</b> <see cref="IsBanned"/> runs at the connect door on the host thread,
/// so it answers from an in-memory map of the filed bans and re-checks the expiry against the clock on every call.
/// A timed ban therefore lapses on its own, with no reload and no sweeper. The writes go to the store FIRST and the
/// map second, so a store write that throws leaves the map as it was and a ban never exists only in memory.
/// </para>
/// <para>
/// <b>Out-of-band writes.</b> A console writing SQL directly, or a second server head, changes rows this map has not
/// seen. <see cref="LoadAsync"/> is an idempotent reload a host may run at boot and on a timer: it reads
/// <see cref="IAccountStore.ListBannedAsync"/> into a new map and swaps it in whole. Writes and reloads run one at a
/// time, so a ban written while a reload is reading is never overwritten by the reload's older view.
/// </para>
/// <para>
/// <b>An unknown subject is the operator's mistake.</b> A ban or an unban naming a subject no account has throws
/// <see cref="ArgumentException"/>, which the admin endpoint renders as a 400 carrying the message, and the message
/// does not echo the subject.
/// </para>
/// </remarks>
public sealed class AccountBanStore : IBanStore
{
    private readonly IAccountStore accounts;
    private readonly TimeProvider clock;
    // Serializes the writes and the reload against each other. Readers never take it.
    private readonly SemaphoreSlim writeGate = new(1, 1);
    // Copy-on-write: a published map is never mutated again, so readers need no lock.
    private volatile Dictionary<string, AccountBan> filed = new(StringComparer.Ordinal);

    /// <summary>Creates the adapter with an empty map. Call <see cref="LoadAsync"/> before serving.</summary>
    /// <param name="accounts">The account store every ban is filed in.</param>
    /// <param name="clock">The clock a ban expiry is checked against.</param>
    public AccountBanStore(IAccountStore accounts, TimeProvider clock)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Reads every filed ban from the store into a new map and swaps it in, dropping any ban lifted out of band and
    /// adding any filed out of band. A failure throws and leaves the previous map in place, so a host that would
    /// rather start with a stale map than not start at all guards the call.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<AccountRecord> banned = await accounts.ListBannedAsync(ct).ConfigureAwait(false);
            var fresh = new Dictionary<string, AccountBan>(banned.Count, StringComparer.Ordinal);
            foreach (AccountRecord account in banned)
            {
                if (account.Ban is { } ban) fresh[account.Subject] = ban;
            }
            filed = fresh;
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <inheritdoc />
    public bool IsBanned(string accountId) =>
        !string.IsNullOrEmpty(accountId)
        && filed.TryGetValue(accountId, out AccountBan ban)
        && ban.IsActive(clock.GetUtcNow());

    /// <inheritdoc />
    /// <exception cref="ArgumentException">No account has <paramref name="accountId"/>, or the store refused the
    /// reason.</exception>
    public async ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AccountRecord? account = await accounts
                .BanAsync(accountId, reason ?? string.Empty, until, cancellationToken).ConfigureAwait(false);
            Publish(account ?? throw UnknownAccount(nameof(accountId)));
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">No account has <paramref name="accountId"/>.</exception>
    public async ValueTask UnbanAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AccountRecord? account = await accounts.UnbanAsync(accountId, cancellationToken).ConfigureAwait(false);
            Publish(account ?? throw UnknownAccount(nameof(accountId)));
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>The bans in force now, in ordinal subject order. A lapsed filing stays in the store and out of this
    /// list.</remarks>
    public IReadOnlyCollection<BanRecord> ListBans()
    {
        DateTimeOffset now = clock.GetUtcNow();
        var live = new List<BanRecord>();
        foreach (KeyValuePair<string, AccountBan> entry in filed)
        {
            if (entry.Value.IsActive(now)) live.Add(new BanRecord(entry.Key, entry.Value.Reason, entry.Value.Until));
        }
        live.Sort(static (a, b) => string.CompareOrdinal(a.AccountId, b.AccountId));
        return live;
    }

    // Mirrors the account as the store returned it. Runs under the write gate, so the copy is never raced.
    private void Publish(AccountRecord account)
    {
        var next = new Dictionary<string, AccountBan>(filed, StringComparer.Ordinal);
        if (account.Ban is { } ban) next[account.Subject] = ban;
        else next.Remove(account.Subject);
        filed = next;
    }

    private static ArgumentException UnknownAccount(string paramName) => new(
        "No account has that subject. A ban is filed only against an account that has signed in at least once, " +
        "so check the id against the account list.", paramName);
}
