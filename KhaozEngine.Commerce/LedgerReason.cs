namespace KhaozEngine.Commerce;

/// <summary>Why a ledger row exists. Purely descriptive; does not affect balance math.</summary>
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
