using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Identity;

namespace KhaozEngine.Identity.Oidc;

/// <summary>
/// Source-compatible forwarding wrapper for the shared
/// <see cref="KhaozEngine.Identity.Interactive.SystemBrowserLauncher"/>. New consumers can import
/// <c>KhaozEngine.Identity.Interactive</c> directly.
/// </summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    private readonly KhaozEngine.Identity.Interactive.SystemBrowserLauncher inner = new();

    public Task<bool> LaunchAsync(Uri url, CancellationToken ct = default) => inner.LaunchAsync(url, ct);
}
