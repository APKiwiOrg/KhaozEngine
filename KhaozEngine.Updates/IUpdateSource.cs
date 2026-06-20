using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// What the server returns for "is there a newer build?": the available version, an opaque build
/// label, the absolute URL of that build's manifest, and whether the update is mandatory.
/// </summary>
public sealed record LatestVersionInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("buildVersion")] string BuildVersion,
    [property: JsonPropertyName("manifestUrl")] string ManifestUrl,
    [property: JsonPropertyName("required")] bool Required);

/// <summary>
/// Host-agnostic transport for the update pipeline. The default <see cref="HttpUpdateSource"/> speaks
/// HTTP against a configurable endpoint (SpaceGame points it at Azure Blob Storage); a game with a
/// different backend (GitHub Releases, an S3 bucket, a LAN share) implements this interface instead.
/// Returning null from the check/manifest calls signals "no answer" and keeps the game offline-safe.
/// </summary>
public interface IUpdateSource
{
    /// <summary>Queries the latest published build for a platform. Returns null when unreachable.</summary>
    Task<LatestVersionInfo?> CheckLatestVersionAsync(string platform, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the raw bytes at <paramref name="url"/> (the manifest or its detached signature).
    /// Returns null on any transport/IO error. Implementations MUST reject a URL that is not https
    /// or not same-host with their configured base (see <see cref="HttpUpdateSource"/>).
    /// </summary>
    Task<byte[]?> DownloadBytesAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a single file to <paramref name="destPath"/>, reporting cumulative bytes, aborting if
    /// more than <paramref name="maxBytes"/> arrive (a hostile/oversized payload guard). Returns false
    /// on any transport/IO error or overrun so the caller can retry.
    /// </summary>
    Task<bool> DownloadFileAsync(string fileUrl, string destPath, long maxBytes, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the absolute URL of one manifest file given the build's <see cref="LatestVersionInfo"/>
    /// and the file's forward-slash relative path. The default layout places files as siblings of the
    /// manifest.
    /// </summary>
    string ResolveFileUrl(LatestVersionInfo latest, string relativePath);
}
