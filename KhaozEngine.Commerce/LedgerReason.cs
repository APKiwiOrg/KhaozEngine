namespace KhaozEngine.Commerce;

/// <summary>Why a ledger row exists. It does not affect balance math, but a stored value participates in
/// idempotency conflict detection.</summary>
public enum LedgerReason
{
    /// <summary>A server-authorized free grant (e.g. a daily reward).</summary>
    Grant,
    /// <summary>A credit from a validated purchase or entitlement.</summary>
    Purchase,
    /// <summary>A player spend (debit).</summary>
    Spend,
    /// <summary>An admin or promo or compensating adjustment.</summary>
    Adjustment,
}
