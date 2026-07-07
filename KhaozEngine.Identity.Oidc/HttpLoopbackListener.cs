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
    private readonly HttpListener listener = new();

    public Uri RedirectUri { get; }

    public HttpLoopbackListener(int port)
    {
        int boundPort = port == 0 ? GetFreePort() : port;
        RedirectUri = new Uri($"http://127.0.0.1:{boundPort}/");
        listener.Prefixes.Add(RedirectUri.ToString());
        listener.Start();
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

        byte[] body = Encoding.UTF8.GetBytes("<html><body>Sign-in complete. You may close this window.</body></html>");
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.Close();

        return new LoopbackResult(RedirectUri.ToString(), query);
    }

    private static int GetFreePort()
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
