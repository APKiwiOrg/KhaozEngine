using System;

namespace KhaozEngine.Commerce;

/// <summary>An immutable ledger row. <see cref="Delta"/> is negative for a debit, positive for a credit.</summary>
public readonly record struct LedgerEntry(
    long Id, AccountId Account, CurrencyId Currency, long Delta,
    string IdempotencyKey, LedgerReason Reason, string? SourceRef, DateTimeOffset CreatedAt);
