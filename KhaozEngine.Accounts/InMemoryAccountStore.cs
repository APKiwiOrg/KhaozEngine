using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Accounts;

/// <summary>
/// The dependency-free reference <see cref="IAccountStore"/>: every account in one ordinal-sorted map behind one
/// lock. For tests, tools and a local development host. It is the REFERENCE the shared conformance suite is
/// checked against first, so a fact it fails is a defect here rather than a fact that does not apply.
/// </summary>
/// <remarks>
/// Nothing survives the process. It applies <see cref="AccountStoreRules"/> exactly as the durable engine stores
/// do, keeps a ban expiry in UTC, and keeps no claim and no timestamp from a sign-in.
/// </remarks>
public sealed class InMemoryAccountStore : IAccountStore
{
    private readonly object gate = new();
    private readonly SortedDictionary<string, AccountRecord> accounts = new(StringComparer.Ordinal);

    /// <summary>Creates an empty store.</summary>
    /// <param name="whitelistOnCreate">Whether a newly created account is past the whitelist gate. Required, with no
    /// default, because it is the highest-consequence value in an auth composition: decide it where the store is
    /// built, never from a stray variable.</param>
    public InMemoryAccountStore(bool whitelistOnCreate) => WhitelistOnCreate = whitelistOnCreate;

    /// <summary>Whether a newly created account is past the whitelist gate, as this store was built.</summary>
    public bool WhitelistOnCreate { get; }

    /// <inheritdoc />
    public Task<AccountRecord> FindOrCreateAsync(AccountSignIn signIn, CancellationToken ct = default)
    {
        string subject = AccountStoreRules.MintSubject(signIn);
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (accounts.TryGetValue(subject, out AccountRecord? existing))
            {
                // A null name means none was given this time, which never clobbers a known one.
                if (signIn.DisplayName is null || string.Equals(signIn.DisplayName, existing.DisplayName, StringComparison.Ordinal))
                    return Task.FromResult(existing);
                AccountRecord refreshed = existing with { DisplayName = signIn.DisplayName };
                accounts[subject] = refreshed;
                return Task.FromResult(refreshed);
            }

            var created = new AccountRecord(subject, signIn.DisplayName, WhitelistOnCreate, Ban: null);
            accounts.Add(subject, created);
            return Task.FromResult(created);
        }
    }

    /// <inheritdoc />
    public Task<AccountRecord?> FindAsync(string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(accounts.TryGetValue(subject, out AccountRecord? account) ? account : null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AccountRecord>> ListAsync(string? afterSubject = null, int limit = 500,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            var page = new List<AccountRecord>(Math.Min(limit, accounts.Count));
            foreach (KeyValuePair<string, AccountRecord> entry in accounts)
            {
                if (afterSubject is not null && string.CompareOrdinal(entry.Key, afterSubject) <= 0) continue;
                page.Add(entry.Value);
                if (page.Count == limit) break;
            }
            return Task.FromResult<IReadOnlyList<AccountRecord>>(page);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AccountRecord>> ListBannedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            var banned = new List<AccountRecord>();
            foreach (AccountRecord account in accounts.Values)
            {
                if (account.Ban is not null) banned.Add(account);
            }
            return Task.FromResult<IReadOnlyList<AccountRecord>>(banned);
        }
    }

    /// <inheritdoc />
    public Task<AccountRecord?> SetWhitelistedAsync(string subject, bool whitelisted, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Update(subject, account => account with { Whitelisted = whitelisted }));
    }

    /// <inheritdoc />
    public Task<AccountRecord?> BanAsync(string subject, string reason, DateTimeOffset? until,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        AccountStoreRules.ValidateBanReason(reason);
        ct.ThrowIfCancellationRequested();
        var ban = new AccountBan(reason, until?.ToUniversalTime());
        return Task.FromResult(Update(subject, account => account with { Ban = ban }));
    }

    /// <inheritdoc />
    public Task<AccountRecord?> UnbanAsync(string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Update(subject, account => account with { Ban = null }));
    }

    // Every write names an existing account and none creates one: an unknown subject is null and changes nothing.
    private AccountRecord? Update(string subject, Func<AccountRecord, AccountRecord> change)
    {
        lock (gate)
        {
            if (!accounts.TryGetValue(subject, out AccountRecord? account)) return null;
            AccountRecord updated = change(account);
            accounts[subject] = updated;
            return updated;
        }
    }
}
