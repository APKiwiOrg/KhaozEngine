using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Identity;

/// <summary>The result of a completed loopback redirect: the raw redirect URI plus its parsed query string.</summary>
public readonly record struct LoopbackResult(string RedirectUri, IReadOnlyDictionary<string, string> Query);

/// <summary>The network seam behind an interactive sign-in redirect: a short-lived local HTTP listener that
/// captures the provider's redirect back to the app. Implementations are platform-specific.</summary>
public interface ILoopbackListener : IDisposable
{
    Uri RedirectUri { get; }
    Task<LoopbackResult> WaitForRedirectAsync(CancellationToken ct);
}
