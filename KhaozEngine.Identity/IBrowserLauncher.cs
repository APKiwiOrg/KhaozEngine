using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Identity;

/// <summary>Opens the platform's default browser at a sign-in URL. The OS seam behind interactive provider
/// sign-in flows (OIDC authorization-code, Discord OAuth, etc.); implementations are platform-specific.</summary>
public interface IBrowserLauncher
{
    Task<bool> LaunchAsync(Uri url, CancellationToken ct = default);
}
