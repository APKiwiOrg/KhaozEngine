namespace KhaozEngine.Commerce;

/// <summary>Outcome of a credit. <c>Replayed</c> means the idempotency key was already applied.</summary>
public readonly record struct CreditResult(bool Applied, bool Replayed, long NewBalance);

/// <summary>Outcome of a debit. <c>Insufficient</c> means the balance was too low; no row was written.</summary>
public readonly record struct DebitResult(bool Applied, bool Replayed, bool Insufficient, long NewBalance);
