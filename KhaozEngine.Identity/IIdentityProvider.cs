using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Identity;

/// <summary>Client-side sign-in for one provider. Produces a <see cref="ProviderCredential"/> the server verifies.</summary>
public interface IIdentityProvider
{
    string ProviderId { get; }
    Task<ProviderCredential> SignInAsync(CancellationToken ct = default);
    Task<ProviderCredential?> RefreshAsync(ProviderCredential expired, CancellationToken ct = default);
}
