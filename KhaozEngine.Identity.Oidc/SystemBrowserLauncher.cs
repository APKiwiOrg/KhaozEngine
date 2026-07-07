using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Identity;

namespace KhaozEngine.Identity.Oidc;

/// <summary>Opens the sign-in URL in the system default browser via <see cref="KhaozEngine.Platform.Browser"/>.</summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public Task<bool> LaunchAsync(Uri url, CancellationToken ct = default)
        => KhaozEngine.Platform.Browser.LaunchBrowserAsync(url, ct);
}
