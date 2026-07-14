namespace KhaozEngine.Identity;

/// <summary>Whether an <see cref="IdentitySession.RefreshCredentialAsync"/> call renewed the provider
/// credential or hit a definitively dead refresh chain.</summary>
public enum CredentialRefreshOutcome
{
    /// <summary>The provider issued a fresh credential. The rotated credential has already been persisted to
    /// the token cache and <see cref="IdentitySession.Current"/> now carries it.</summary>
    Refreshed,

    /// <summary>The provider rejected the refresh chain as dead (revoked or expired). The token cache and
    /// <see cref="IdentitySession.Current"/> are left untouched, so the consumer must fall back to an
    /// interactive sign-in.</summary>
    Rejected,
}

/// <summary>The result of <see cref="IdentitySession.RefreshCredentialAsync"/>. It carries the
/// <see cref="Outcome"/> and the resulting <see cref="IdentityState"/> (the updated state after a refresh, or
/// the unchanged current state after a rejection). A transient provider failure does not produce a result at
/// all: it propagates as an exception so the consumer retries later rather than forcing a sign-in.</summary>
public readonly record struct CredentialRefreshResult(CredentialRefreshOutcome Outcome, IdentityState State)
{
    /// <summary>True when the credential was refreshed (see <see cref="CredentialRefreshOutcome.Refreshed"/>),
    /// false when the refresh chain was rejected.</summary>
    public bool IsRefreshed => Outcome == CredentialRefreshOutcome.Refreshed;
}
