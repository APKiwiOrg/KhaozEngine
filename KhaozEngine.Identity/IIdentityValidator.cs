using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Identity;

/// <summary>Server-side verification of a provider credential to a <see cref="VerifiedIdentity"/> (null if invalid).</summary>
public interface IIdentityValidator
{
    string ProviderId { get; }
    Task<VerifiedIdentity?> ValidateAsync(string credentialToken, CancellationToken ct = default);
}
