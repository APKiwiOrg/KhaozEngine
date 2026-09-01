using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Identity;

/// <summary>Server-side verification of a provider credential to a <see cref="VerifiedIdentity"/> (null if invalid).</summary>
public interface IIdentityValidator
{
    string ProviderId { get; }

    Task<VerifiedIdentity?> ValidateAsync(string credentialToken, CancellationToken ct = default);

    /// <summary>
    /// Validates the credential and reports which of the three outcomes happened, so a caller can tell a
    /// refused credential from a provider that could not answer. Prefer this over
    /// <see cref="ValidateAsync"/>: a client that treats an outage as a refusal discards a good token and
    /// re-runs sign-in against a provider that is already down.
    ///
    /// <para>The default maps the existing method's null onto
    /// <see cref="IdentityValidationOutcome.Refused"/>, which is exactly what null meant before, so a
    /// validator written against the older contract keeps working with no change and callers keep their
    /// meaning. A backend that can see more (an HTTP status, a transport failure) overrides this to split the
    /// unavailable case out. Being a default interface member, it is reachable through the interface rather
    /// than through a concrete type that does not declare it.</para>
    /// </summary>
    async Task<IdentityValidation> ValidateDetailedAsync(string credentialToken, CancellationToken ct = default)
    {
        VerifiedIdentity? identity = await ValidateAsync(credentialToken, ct).ConfigureAwait(false);
        return identity is { } verified ? IdentityValidation.Verified(verified) : IdentityValidation.Refused();
    }
}
