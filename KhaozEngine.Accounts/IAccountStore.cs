using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Accounts;

/// <summary>
/// The account registry: resolve a verified sign-in to its account, read accounts, and the operator writes over
/// them. The ONE place a whitelist flag or a ban is filed, so the exchange, the connect door, a join check and an
/// admin console all read the same row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Find-or-create is the only way a row comes to exist.</b> Every write names an existing account and returns
/// it as it stands afterwards, or <c>null</c> for a subject no account has, and none of them creates a row: a
/// whitelist or a ban filed against a subject nobody ever signed in as is a typo becoming policy.
/// </para>
/// <para>
/// <b>Subjects compare by code point.</b> Two subjects differing only in case are two accounts, and listings run in
/// ordinal order, on every backend.
/// </para>
/// <para>
/// <b>Refusals are <see cref="ArgumentException"/>.</b> A null or empty subject, a sign-in the store cannot mint a
/// subject from, a value over the store's length limits, or a non-positive page size throws before anything is
/// written. The engine stores apply <see cref="AccountStoreRules"/>, and their messages name the rule and never the
/// value. Cancellation throws <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// The shared conformance suite in <c>KhaozEngine.Accounts.Tests</c> (<c>AccountStoreConformance</c>) is the
/// executable form of this contract, and every engine backend runs it unchanged.
/// </para>
/// </remarks>
public interface IAccountStore
{
    /// <summary>
    /// Finds the account for a verified sign-in, creating it on the first one. A new account is whitelisted when the
    /// store was built to whitelist on create. A repeat refreshes the stored display name when
    /// <see cref="AccountSignIn.DisplayName"/> is set, and keeps the whitelist flag and any ban. Never returns
    /// <c>null</c>: whether the account may play is what the returned record says. Concurrent first sign-ins for one
    /// subject produce one account.
    /// </summary>
    Task<AccountRecord> FindOrCreateAsync(AccountSignIn signIn, CancellationToken ct = default);

    /// <summary>Reads one account without creating it. <c>null</c> when no account has <paramref name="subject"/>.</summary>
    Task<AccountRecord?> FindAsync(string subject, CancellationToken ct = default);

    /// <summary>
    /// One page of accounts in ordinal subject order, starting strictly after <paramref name="afterSubject"/>
    /// (<c>null</c> for the first page), at most <paramref name="limit"/> long. The next page starts after the last
    /// subject returned, and an empty page is the end. <paramref name="afterSubject"/> need not name an account.
    /// </summary>
    Task<IReadOnlyList<AccountRecord>> ListAsync(string? afterSubject = null, int limit = 500, CancellationToken ct = default);

    /// <summary>
    /// Every account with a FILED ban, lapsed timed bans included, in ordinal subject order. What
    /// <see cref="AccountBanStore.LoadAsync"/> reads.
    /// </summary>
    Task<IReadOnlyList<AccountRecord>> ListBannedAsync(CancellationToken ct = default);

    /// <summary>Sets the whitelist flag. The account afterwards, or <c>null</c> for an unknown subject.</summary>
    Task<AccountRecord?> SetWhitelistedAsync(string subject, bool whitelisted, CancellationToken ct = default);

    /// <summary>
    /// Files a ban, replacing any filed one. <paramref name="until"/> <c>null</c> is permanent. The account
    /// afterwards, or <c>null</c> for an unknown subject.
    /// </summary>
    Task<AccountRecord?> BanAsync(string subject, string reason, DateTimeOffset? until, CancellationToken ct = default);

    /// <summary>
    /// Lifts the filed ban, clearing its reason and expiry with it. The account afterwards (unchanged when no ban was
    /// filed), or <c>null</c> for an unknown subject.
    /// </summary>
    Task<AccountRecord?> UnbanAsync(string subject, CancellationToken ct = default);
}
