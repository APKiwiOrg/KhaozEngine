using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Identity;

/// <summary>Client-side sign-in for one provider. Produces a <see cref="ProviderCredential"/> the server verifies.</summary>
public interface IIdentityProvider
{
    /// <summary>The stable id of this provider (for example "discord" or "oidc").</summary>
    string ProviderId { get; }

    /// <summary>Runs the interactive sign-in and returns the resulting provider credential.</summary>
    Task<ProviderCredential> SignInAsync(CancellationToken ct = default);

    /// <summary>Exchanges the <paramref name="expired"/> credential's refresh token for a fresh credential. The
    /// return value carries the outcome as a contract the caller must honour:
    /// <list type="bullet">
    /// <item>a non-null <see cref="ProviderCredential"/> is the renewed credential (with a possibly rotated
    /// refresh token that the caller must persist)</item>
    /// <item>null means the refresh chain is definitively dead (revoked or expired, an empty stored refresh
    /// token, or the token endpoint answering 400/401 on the refresh grant). Interactive sign-in is required</item>
    /// <item>a thrown exception (a transient 5xx or a transport fault) means retry later, NOT sign-in-required</item>
    /// </list></summary>
    Task<ProviderCredential?> RefreshAsync(ProviderCredential expired, CancellationToken ct = default);
}
