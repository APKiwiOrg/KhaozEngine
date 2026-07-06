using System;

namespace KhaozEngine.Commerce;

/// <summary>An immutable ledger row.</summary>
public readonly record struct LedgerEntry(
    long Id, AccountId Account, CurrencyId Currency, long Delta,
    string IdempotencyKey, LedgerReason Reason, string? SourceRef, DateTimeOffset CreatedAt);
