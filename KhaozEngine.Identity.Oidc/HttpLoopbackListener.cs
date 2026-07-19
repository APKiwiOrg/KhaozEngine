using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using KhaozEngine.Identity;

namespace KhaozEngine.Identity.Oidc;

/// <summary>Loopback redirect capture on http://127.0.0.1:&lt;port&gt;/ for the auth-code flow: binds a short-lived
/// <see cref="HttpListener"/>, accepts the provider's single redirect request, and returns its parsed query.</summary>
public sealed class HttpLoopbackListener : ILoopbackListener
{
    /// <summary>
    /// The built-in sign-in completion page served to the browser after a successful redirect, used when the
    /// caller passes no page of its own. This is the one intentionally raw (non-localized) player-facing string
    /// in the identity stack: the identity packages do not reference the localization catalog
    /// (<c>KhaozEngine.App</c>), so a consumer that wants a localized or branded page supplies it through the
    /// constructor (typically from its <c>ILoopbackListener</c> factory). Named so the raw default stays greppable.
    /// </summary>
    public const string DefaultCompletionPageHtml =
        "<html><body>Sign-in complete. You may close this window.</body></html>";

    // Cap on probe-then-bind retries when an OS-assigned ephemeral port is requested (port 0). Matches
    // LiveSocketSupport.TryBindServer's default so both socket families behave the same under contention.
    private const int MaxBindAttempts = 16;

    private readonly HttpListener listener = new();
    private readonly string completionPage;

    public Uri RedirectUri { get; }

    /// <summary>
    /// Binds the loopback listener. <paramref name="completionPageHtml"/> overrides the completion page shown to
    /// the browser after sign-in (for localization or branding), defaulting to <see cref="DefaultCompletionPageHtml"/>.
    /// </summary>
    public HttpLoopbackListener(int port, string? completionPageHtml = null)
    {
        completionPage = completionPageHtml ?? DefaultCompletionPageHtml;
        RedirectUri = Bind(listener, port);
    }

    public async Task<LoopbackResult> WaitForRedirectAsync(CancellationToken ct)
    {
        using CancellationTokenRegistration registration = ct.Register(static state =>
        {
            HttpListener target = (HttpListener)state!;
            try { target.Stop(); } catch { /* best-effort cancellation */ }
        }, listener);

        HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);

        System.Collections.Specialized.NameValueCollection parsed =
            HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
        Dictionary<string, string> query = new(StringComparer.Ordinal);
        foreach (string? key in parsed.AllKeys)
        {
            if (key is not null)
                query[key] = parsed[key] ?? string.Empty;
        }

        byte[] body = Encoding.UTF8.GetBytes(completionPage);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.Close();

        return new LoopbackResult(RedirectUri.ToString(), query);
    }

    /// <summary>
    /// Binds the listener and returns the redirect Uri. A caller-supplied fixed port is bound directly. For an
    /// OS-assigned ephemeral port (<paramref name="port"/> == 0) a free port is probed on a throwaway socket then
    /// bound, and because that probe-release-bind gap is a race (another process can grab the port in between),
    /// the bind is retried across fresh ports up to <see cref="MaxBindAttempts"/> times, mirroring
    /// <c>LiveSocketSupport.TryBindServer</c>. The final attempt's bind failure propagates.
    /// </summary>
    private static Uri Bind(HttpListener listener, int port)
    {
        if (port != 0)
        {
            Uri fixedUri = new($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add(fixedUri.ToString());
            listener.Start();
            return fixedUri;
        }

        for (int attempt = 0; ; attempt++)
        {
            Uri candidate = new($"http://127.0.0.1:{ProbeFreePort()}/");
            listener.Prefixes.Add(candidate.ToString());
            try
            {
                listener.Start();
                return candidate;
            }
            catch (HttpListenerException) when (attempt < MaxBindAttempts - 1)
            {
                // Lost the probe-to-bind race, so drop the dead prefix and try a fresh port.
                listener.Prefixes.Remove(candidate.ToString());
            }
        }
    }

    private static int ProbeFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return freePort;
    }

    public void Dispose()
    {
        try { listener.Close(); } catch { /* best-effort teardown */ }
    }
}
