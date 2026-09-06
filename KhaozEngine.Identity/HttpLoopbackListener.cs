using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
namespace KhaozEngine.Identity.Interactive;

/// <summary>Loopback redirect capture on http://127.0.0.1:&lt;port&gt;/ for the auth-code flow: binds a short-lived
/// socket, accepts the provider's single redirect request, and returns its parsed query.
///
/// The bind is a plain <see cref="TcpListener"/> rather than an <c>HttpListener</c> because
/// <c>HttpListener</c> cannot be given port 0: an OS-assigned port had to be read off a throwaway probe socket
/// and then bound after that socket was released, and another listener on the host can take the port inside
/// that window. On Windows the collision is not even refused, since an http.sys prefix and an ordinary socket on
/// the same port do not exclude each other, so the two split the incoming connections and each side hangs on a
/// protocol it cannot parse (#720). A <see cref="TcpListener"/> binds port 0 atomically and reports the port the
/// OS actually gave it, so there is no window at all. What it costs is the request parsing below, which for a
/// redirect capture is one request line and one static page.</summary>
public sealed class HttpLoopbackListener : KhaozEngine.Identity.ILoopbackListener
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

    // A redirect is one small GET, so a connection that has not produced a complete request head within this
    // budget is not the redirect (a browser pre-connect that never sends, a port scanner) and is dropped. The
    // listener keeps accepting, so dropping one costs the real redirect nothing.
    private static readonly TimeSpan RequestHeadBudget = TimeSpan.FromSeconds(15);

    // Ceiling on the request head we will buffer before dropping a connection. A browser redirect head runs to a
    // few hundred bytes; anything past this is not one.
    private const int MaxRequestHeadBytes = 16 * 1024;

    private readonly TcpListener listener;
    private readonly string completionPage;

    /// <summary>The redirect URI the provider must send the browser back to. It carries the port that is actually
    /// bound, so for an OS-assigned port (0) it is only meaningful after the constructor has returned.</summary>
    public Uri RedirectUri { get; }

    /// <summary>
    /// Binds the loopback listener. Pass 0 for an OS-assigned port, or a fixed port when the provider requires a
    /// pre-registered redirect URI. <paramref name="completionPageHtml"/> overrides the completion page shown to
    /// the browser after sign-in (for localization or branding), defaulting to
    /// <see cref="DefaultCompletionPageHtml"/>. A fixed port already in use throws <see cref="SocketException"/>.
    /// </summary>
    public HttpLoopbackListener(int port, string? completionPageHtml = null)
    {
        completionPage = completionPageHtml ?? DefaultCompletionPageHtml;
        listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        RedirectUri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
    }

    /// <summary>
    /// Accepts connections until one delivers a well-formed HTTP request, serves it the completion page, and
    /// returns that request's parsed query. Connections that deliver nothing usable are dropped without ending
    /// the wait. Cancelling ends the wait by faulting, never by returning an empty result.
    /// </summary>
    public async Task<KhaozEngine.Identity.LoopbackResult> WaitForRedirectAsync(CancellationToken ct)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        TaskCompletionSource<KhaozEngine.Identity.LoopbackResult> captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task accepting = AcceptUntilCapturedAsync(captured, stop.Token);
            if (await Task.WhenAny(captured.Task, accepting).ConfigureAwait(false) == accepting)
            {
                // The accept loop only ends early by faulting (cancelled, or the listener disposed under it), so
                // awaiting it surfaces that. When it ended because the capture completed, this returns at once.
                await accepting.ConfigureAwait(false);
            }

            return await captured.Task.ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
        }
    }

    private async Task AcceptUntilCapturedAsync(
        TaskCompletionSource<KhaozEngine.Identity.LoopbackResult> captured,
        CancellationToken ct)
    {
        while (!captured.Task.IsCompleted)
        {
            TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);

            // Each connection is served off the accept loop, so a peer that connects and then says nothing
            // cannot hold the real redirect behind it for the length of its budget.
            _ = RespondAsync(client, captured, ct);
        }
    }

    private async Task RespondAsync(
        TcpClient client,
        TaskCompletionSource<KhaozEngine.Identity.LoopbackResult> captured,
        CancellationToken ct)
    {
        using (client)
        using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            budget.CancelAfter(RequestHeadBudget);
            try
            {
                NetworkStream stream = client.GetStream();
                string? head = await ReadRequestHeadAsync(stream, budget.Token).ConfigureAwait(false);
                if (head is null || !TryParseTarget(head, out string target))
                    return;

                byte[] body = Encoding.UTF8.GetBytes(completionPage);
                byte[] header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                    $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, budget.Token).ConfigureAwait(false);
                await stream.WriteAsync(body, budget.Token).ConfigureAwait(false);
                await stream.FlushAsync(budget.Token).ConfigureAwait(false);

                captured.TrySetResult(new KhaozEngine.Identity.LoopbackResult(RedirectUri.ToString(), ParseQuery(target)));

                // Half-close so the browser sees the end of the page before the socket goes away. Best-effort:
                // a peer that has already hung up is not a failure, the redirect is captured either way.
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { /* peer gone */ }
            }
            catch
            {
                // A connection that dies, stalls out its budget, or is cancelled mid-request is not the redirect.
                // The accept loop stays up for the one that is.
            }
        }
    }

    /// <summary>
    /// Reads until the end of the request head (the blank line after the headers) and returns it without that
    /// terminator, or null when the peer closed first or ran past <see cref="MaxRequestHeadBytes"/>. Latin-1
    /// keeps the bytes intact through the decode: a request head is ASCII by spec, and percent-encoding in the
    /// query is decoded as UTF-8 later by the query parser.
    /// </summary>
    private static async Task<string?> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[1024];
        string head = string.Empty;
        while (head.Length < MaxRequestHeadBytes)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                return null;

            head += Encoding.Latin1.GetString(buffer, 0, read);
            int end = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
                return head[..end];
        }

        return null;
    }

    /// <summary>Pulls the request target out of the request line (<c>GET /?code=... HTTP/1.1</c>), rejecting a
    /// head that does not open with one so a garbage connection is dropped rather than answered.</summary>
    private static bool TryParseTarget(string head, out string target)
    {
        int lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
        string requestLine = lineEnd >= 0 ? head[..lineEnd] : head;
        string[] parts = requestLine.Split(' ');
        target = parts.Length == 3 ? parts[1] : string.Empty;
        return target.Length > 0;
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string target)
    {
        int mark = target.IndexOf('?');
        NameValueCollection parsed = HttpUtility.ParseQueryString(mark >= 0 ? target[mark..] : string.Empty);
        Dictionary<string, string> query = new(StringComparer.Ordinal);
        foreach (string? key in parsed.AllKeys)
        {
            if (key is not null)
                query[key] = parsed[key] ?? string.Empty;
        }

        return query;
    }

    public void Dispose()
    {
        try { listener.Dispose(); } catch { /* best-effort teardown */ }
    }
}
