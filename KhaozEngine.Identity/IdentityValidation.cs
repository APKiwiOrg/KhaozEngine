namespace KhaozEngine.Identity;

/// <summary>How a credential validation ended.</summary>
public enum IdentityValidationOutcome
{
    /// <summary>
    /// The provider answered and the credential is not good: expired, revoked, minted for another
    /// application, or malformed. Re-running sign-in is the right response.
    /// </summary>
    Refused = 0,

    /// <summary>The provider answered and the credential names a verified subject.</summary>
    Verified = 1,

    /// <summary>
    /// The provider could not answer: an outage, a rate limit, or a request that never completed. This says
    /// nothing about the credential, so a caller should back off and retry rather than discard it and send the
    /// player back through sign-in against a provider that is already down.
    /// </summary>
    ProviderUnavailable = 2,
}

/// <summary>
/// The result of validating a provider credential. Adds the third outcome the nullable
/// <see cref="VerifiedIdentity"/> return could not express: a provider outage looks exactly like a bad
/// credential when null is the only failure channel, and the two want opposite responses from the caller.
/// </summary>
public readonly record struct IdentityValidation
{
    private IdentityValidation(IdentityValidationOutcome outcome, VerifiedIdentity? identity, string? detail)
    {
        Outcome = outcome;
        Identity = identity;
        Detail = detail;
    }

    /// <summary>How the validation ended.</summary>
    public IdentityValidationOutcome Outcome { get; }

    /// <summary>The verified subject, set only when <see cref="Outcome"/> is Verified.</summary>
    public VerifiedIdentity? Identity { get; }

    /// <summary>
    /// Optional developer-facing note about what happened (a status code, an exception message). Diagnostic
    /// only: it is never localized and never shown to a player.
    /// </summary>
    public string? Detail { get; }

    /// <summary>True when the credential verified.</summary>
    public bool IsVerified => Outcome == IdentityValidationOutcome.Verified;

    /// <summary>The provider verified the credential.</summary>
    public static IdentityValidation Verified(VerifiedIdentity identity) =>
        new(IdentityValidationOutcome.Verified, identity, null);

    /// <summary>The provider answered and the credential is not good.</summary>
    public static IdentityValidation Refused(string? detail = null) =>
        new(IdentityValidationOutcome.Refused, null, detail);

    /// <summary>The provider could not answer, so the credential's standing is unknown.</summary>
    public static IdentityValidation ProviderUnavailable(string? detail = null) =>
        new(IdentityValidationOutcome.ProviderUnavailable, null, detail);
}
