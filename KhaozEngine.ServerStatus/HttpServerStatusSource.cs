using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>Configuration for <see cref="HttpServerStatusSource"/>.</summary>
public sealed class HttpServerStatusSourceOptions
{
    /// <summary>
    /// Absolute URL of the status endpoint (e.g. <c>https://status.mygame.example.com/status</c>). A bare
    /// host without a scheme is assumed to be <c>https</c>. HTTP (non-TLS) URLs are refused.
    /// </summary>
    public required string StatusUrl { get; init; }

    /// <summary>Per-request HTTP timeout when this source owns its <see cref="HttpClient"/>.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum bytes accepted from the status response before it is rejected (fetch returns null). The
    /// payload is a small fixed-shape report, so the 64 KB default is generous headroom while still capping a
    /// hostile or compromised host that streams an unbounded body into the JSON parser (memory-exhaustion guard).
    /// </summary>
    public long MaxResponseBytes { get; init; } = 64L * 1024;

    /// <summary>Streaming buffer size for the bounded read.</summary>
    public int ReadBufferSize { get; init; } = 8192;
}

/// <summary>
/// Default <see cref="IServerStatusSource"/>: fetches the report over HTTPS from a configured URL. Enforces
/// TLS, caps the response size (memory-exhaustion guard), and swallows every transport/parse error into a
/// null result so the poller never sees an exception. Mirrors the hardening in
/// <c>KhaozEngine.Updates.HttpUpdateSource</c>.
/// </summary>
public sealed class HttpServerStatusSource : IServerStatusSource, IDisposable
{
    private readonly HttpServerStatusSourceOptions options;
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;
    private readonly ILogger log = Log.For<HttpServerStatusSource>();
    private readonly Uri? endpoint;

    /// <summary>
    /// Builds a source. Pass an existing <paramref name="httpClient"/> to share a pooled client (its lifetime
    /// is the caller's). Omit it and the source owns a client configured with <see cref="HttpServerStatusSourceOptions.HttpTimeout"/>.
    /// </summary>
    public HttpServerStatusSource(HttpServerStatusSourceOptions options, HttpClient? httpClient = null)
    {
        this.options = options;
        endpoint = ParseHttps(options.StatusUrl);
        if (httpClient is null)
        {
            this.httpClient = new HttpClient { Timeout = options.HttpTimeout };
            ownsClient = true;
        }
        else
        {
            this.httpClient = httpClient;
            ownsClient = false;
        }
    }

    /// <inheritdoc />
    public async Task<ServerStatusReport?> FetchAsync(CancellationToken cancellationToken = default)
    {
        if (endpoint is null)
        {
            log.Warn($"Status URL is not a valid absolute https URL, not polling: {options.StatusUrl}");
            return null;
        }

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(
                endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var ms = new MemoryStream();
            byte[] buffer = new byte[options.ReadBufferSize];
            long total = 0;
            int read;
            while ((read = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > options.MaxResponseBytes)
                {
                    log.Info($"Status response exceeded size cap ({options.MaxResponseBytes} bytes), aborting");
                    return null;
                }
                ms.Write(buffer, 0, read);
            }

            // TryParse is itself tolerant/non-throwing: a malformed body yields null, treated like a miss.
            return ServerStatusReport.TryParse(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            log.Info($"Status fetch failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Parses the configured URL into an absolute https Uri (bare host implies https). Null if not https/absolute.</summary>
    private static Uri? ParseHttps(string statusUrl)
    {
        if (string.IsNullOrWhiteSpace(statusUrl))
        {
            return null;
        }

        string normalized = statusUrl.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"https://{normalized}";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        return uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    /// <summary>Disposes the owned <see cref="HttpClient"/> (no-op when a client was supplied by the caller).</summary>
    public void Dispose()
    {
        if (ownsClient)
        {
            httpClient.Dispose();
        }
    }
}
