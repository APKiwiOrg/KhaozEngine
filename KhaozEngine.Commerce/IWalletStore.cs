using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Commerce;

/// <summary>
/// Durable, transactional wallet backing store. Credit/Debit are atomic and idempotent by
/// <c>idempotencyKey</c>, scoped per <c>(account, currency)</c>: re-applying an already-seen key
/// for the same account and currency is a no-op that reports the prior balance. The same key used
/// for a different account, or a different currency on the same account, is a distinct operation,
/// not a replay. Not a reuse of IWorldStore, which is opaque-bytes last-write-wins and cannot express
/// atomic increments or idempotency.
/// A replayed key returns the balance as of the original operation (the historical post-balance),
/// not necessarily the current balance. An idempotency key is assumed to bind a fixed
/// <c>(amount, reason)</c>: re-using the same key with a different amount or reason silently returns
/// the original result, no error (a future conflict-detecting result is a deferred follow-up).
/// </summary>
public interface IWalletStore
{
    /// <summary>Add <paramref name="amount"/> (must be &gt; 0) idempotently.</summary>
    Task<CreditResult> CreditAsync(AccountId account, CurrencyId currency, long amount,
        string idempotencyKey, LedgerReason reason, string? sourceRef, CancellationToken ct = default);

    /// <summary>Subtract <paramref name="amount"/> (must be &gt; 0) idempotently; fails (no throw) if balance is too low.</summary>
    Task<DebitResult> DebitAsync(AccountId account, CurrencyId currency, long amount,
        string idempotencyKey, LedgerReason reason, string? sourceRef, CancellationToken ct = default);

    /// <summary>Current balance for the pair (0 if none).</summary>
    Task<long> GetBalanceAsync(AccountId account, CurrencyId currency, CancellationToken ct = default);

    /// <summary>Most-recent ledger rows for the pair, newest first, up to <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(AccountId account, CurrencyId currency,
        int limit, CancellationToken ct = default);
}
