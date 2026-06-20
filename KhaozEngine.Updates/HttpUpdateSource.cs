using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>Configuration for <see cref="HttpUpdateSource"/>.</summary>
public sealed class HttpUpdateSourceOptions
{
    /// <summary>
    /// Base URL of the update API (e.g. <c>https://my-server.example.com/</c>). A bare host without a
    /// scheme is assumed to be <c>https</c>.
    /// </summary>
    public required string ServerBaseUrl { get; init; }

    /// <summary>
    /// Relative path/query of the "latest version" endpoint. <c>{platform}</c> is replaced with the
    /// URL-escaped platform string. Defaults to SpaceGame's <c>api/updates/latest?platform={platform}</c>.
    /// </summary>
    public string LatestVersionPath { get; init; } = "api/updates/latest?platform={platform}";

    /// <summary>Per-request HTTP timeout when this source owns its <see cref="HttpClient"/>.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Streaming buffer size for file downloads.</summary>
    public int DownloadBufferSize { get; init; } = 81920;
}

/// <summary>
/// Default <see cref="IUpdateSource"/>: HTTP transport against a configurable endpoint, with the
/// build's files laid out as siblings of its manifest (SpaceGame's Azure Blob layout, but the base
/// URL and endpoint path are configuration so any host works).
/// </summary>
public sealed class HttpUpdateSource : IUpdateSource, IDisposable
{
    private readonly HttpUpdateSourceOptions options;
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;
    private readonly ILogger log = Log.For<HttpUpdateSource>();

    public HttpUpdateSource(HttpUpdateSourceOptions options, HttpClient? httpClient = null)
    {
        this.options = options;
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

    public async Task<LatestVersionInfo?> CheckLatestVersionAsync(string platform, CancellationToken cancellationToken = default)
    {
        Uri? endpoint = BuildLatestVersionUrl(options.ServerBaseUrl, options.LatestVersionPath, platform);
        if (endpoint is null)
        {
            return null;
        }

        try
        {
            return await httpClient.GetFromJsonAsync<LatestVersionInfo>(endpoint, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            log.Info($"Version check failed: {ex.Message}");
            return null;
        }
    }

    public async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedOrigin(url))
        {
            log.Info($"Refusing off-origin or non-https URL: {url}");
            return null;
        }

        try
        {
            return await httpClient.GetByteArrayAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            log.Info($"Download failed ({url}): {ex.Message}");
            return null;
        }
    }

    public async Task<bool> DownloadFileAsync(string fileUrl, string destPath, long maxBytes, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedOrigin(fileUrl))
        {
            log.Info($"Refusing off-origin or non-https file URL: {fileUrl}");
            return false;
        }

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(
                fileUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            string? destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            byte[] buffer = new byte[options.DownloadBufferSize];
            long totalBytesRead = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                totalBytesRead += bytesRead;
                if (totalBytesRead > maxBytes)
                {
                    log.Info($"File exceeded size cap ({maxBytes} bytes), aborting: {fileUrl}");
                    return false;
                }
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                bytesProgress?.Report(totalBytesRead);
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            log.Info($"File download failed ({fileUrl}): {ex.Message}");
            return false;
        }
    }

    /// <summary>Default layout: each file lives next to the manifest under the same build directory.</summary>
    public string ResolveFileUrl(LatestVersionInfo latest, string relativePath)
    {
        string manifestUrl = latest.ManifestUrl;
        int lastSlash = manifestUrl.LastIndexOf('/');
        string dirUrl = lastSlash >= 0 ? manifestUrl[..lastSlash] : manifestUrl;
        return $"{dirUrl}/{relativePath}";
    }

    /// <summary>
    /// Builds the absolute "latest version" endpoint URL from a base URL and a path template. A base
    /// without a scheme is treated as <c>https</c>; a trailing slash is trimmed; <c>{platform}</c> in
    /// the template is URL-escaped. Returns null for an empty base or an unparseable result.
    /// </summary>
    public static Uri? BuildLatestVersionUrl(string serverBaseUrl, string latestVersionPath, string platform)
    {
        if (string.IsNullOrWhiteSpace(serverBaseUrl))
        {
            return null;
        }

        string normalized = serverBaseUrl.Trim().TrimEnd('/');
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"https://{normalized}";
        }

        string path = latestVersionPath.Replace("{platform}", Uri.EscapeDataString(platform));
        return Uri.TryCreate($"{normalized}/{path}", UriKind.Absolute, out Uri? resolved) ? resolved : null;
    }

    // Implemented in Task 4. Placeholder admits all so Task 3 is purely the transport-shape change.
    private bool IsAllowedOrigin(string url) => true;

    public void Dispose()
    {
        if (ownsClient)
        {
            httpClient.Dispose();
        }
    }
}
