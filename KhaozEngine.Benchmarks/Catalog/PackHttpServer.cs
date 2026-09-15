using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// A local HTTP endpoint over the pack store, so the client fetch loop of spec section 8.7 runs over a
/// real socket rather than a method call. It is the CDN's stand-in and nothing more: content addressed
/// GETs, no range support, no caching headers.
/// </summary>
public sealed class PackHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly FileSystemPackStore _store;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public PackHttpServer(FileSystemPackStore store, int port)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        Port = port;
        BaseAddress = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
        _listener.Prefixes.Add(BaseAddress);
    }

    public int Port { get; }

    public string BaseAddress { get; }

    public long BytesServed { get; private set; }

    public int RequestsServed { get; private set; }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        if (_listener.IsListening) _listener.Stop();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* the accept loop ends with the listener */ }
        _listener.Close();
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; }
            _ = Task.Run(() => ServeAsync(context));
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? string.Empty;
            string hash = path.TrimStart('/');
            if (hash.EndsWith(".kec", StringComparison.Ordinal)) hash = hash[..^4];
            byte[]? bytes = hash.Length == 64 ? _store.Get(hash) : null;
            if (bytes is null)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
            lock (_listener)
            {
                BytesServed += bytes.Length;
                RequestsServed++;
            }
        }
        catch (HttpListenerException) { /* the client went away mid response */ }
        catch (ObjectDisposedException) { /* the listener stopped under us */ }
    }
}
