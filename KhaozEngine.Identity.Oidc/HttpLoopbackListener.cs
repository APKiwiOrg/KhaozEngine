using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Identity;

namespace KhaozEngine.Identity.Oidc;

/// <summary>
/// Source-compatible forwarding wrapper for the shared
/// <see cref="KhaozEngine.Identity.Interactive.HttpLoopbackListener"/>. New consumers can import
/// <c>KhaozEngine.Identity.Interactive</c> directly.
/// </summary>
public sealed class HttpLoopbackListener : ILoopbackListener
{
    private readonly KhaozEngine.Identity.Interactive.HttpLoopbackListener inner;

    public const string DefaultCompletionPageHtml =
        KhaozEngine.Identity.Interactive.HttpLoopbackListener.DefaultCompletionPageHtml;

    public HttpLoopbackListener(int port, string? completionPageHtml = null)
    {
        inner = new KhaozEngine.Identity.Interactive.HttpLoopbackListener(port, completionPageHtml);
    }

    public Uri RedirectUri => inner.RedirectUri;

    public Task<LoopbackResult> WaitForRedirectAsync(CancellationToken ct) => inner.WaitForRedirectAsync(ct);

    public void Dispose() => inner.Dispose();
}
