namespace KhaozEngine.Commerce;

/// <summary>Outcome of a credit. <c>Replayed</c> means the same intent was already applied. <c>Conflict</c>
/// means the key belongs to another intent. Both return the original operation's historical post-balance.</summary>
public readonly record struct CreditResult(bool Applied, bool Replayed, long NewBalance)
{
    /// <summary>The idempotency key is bound to a different signed amount or ledger reason.</summary>
    public bool Conflict => !Applied && !Replayed;
}

/// <summary>Outcome of a debit. <c>Replayed</c> and <c>Conflict</c> carry the original operation's historical
/// post-balance. <c>Insufficient</c> means the balance was too low and no row was written.</summary>
public readonly record struct DebitResult(bool Applied, bool Replayed, bool Insufficient, long NewBalance)
{
    /// <summary>The idempotency key is bound to a different signed amount or ledger reason.</summary>
    public bool Conflict => !Applied && !Replayed && !Insufficient;
}
