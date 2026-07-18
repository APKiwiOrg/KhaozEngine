using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Updates;

namespace KhaozEngine.Tests.Updates;

/// <summary>
/// In-memory <see cref="IUpdateSource"/> for headless tests. Serves a fixed latest-version answer and
/// manifest, streams file bytes from a dictionary, and can serve corrupt bytes (once or always) to
/// exercise the SHA256 verify/retry path. File "URLs" are just the relative paths.
/// </summary>
public sealed class FakeUpdateSource : IUpdateSource
{
    public LatestVersionInfo? Latest;
    public UpdateManifest? RemoteManifest;
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
    public readonly HashSet<string> CorruptFirstAttempt = new(StringComparer.Ordinal);
    public readonly HashSet<string> AlwaysCorrupt = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> Attempts = new(StringComparer.Ordinal);
    public int DownloadCalls;

    /// <summary>Number of times the version check has been invoked (lets a test count Tick-driven re-checks).</summary>
    public int CheckCalls;

    /// <summary>
    /// When set, <see cref="CheckLatestVersionAsync"/> parks on this gate before answering, so a test can
    /// hold one check "in flight" (the service stays in <see cref="UpdateState.Checking"/>) and drive Tick
    /// against it. Complete it to let the check finish.
    /// </summary>
    public TaskCompletionSource<bool>? CheckGate;

    public async Task<LatestVersionInfo?> CheckLatestVersionAsync(string platform, CancellationToken cancellationToken = default)
    {
        CheckCalls++;
        TaskCompletionSource<bool>? gate = CheckGate;
        if (gate is not null)
        {
            await gate.Task.ConfigureAwait(false);
        }
        return Latest;
    }

    /// <summary>Raw bytes keyed by URL: the manifest JSON and its ".sig" live here.</summary>
    public readonly Dictionary<string, byte[]> Bytes = new(StringComparer.Ordinal);

    public Task<byte[]?> DownloadBytesAsync(string url, long maxBytes, CancellationToken cancellationToken = default)
        => Task.FromResult(Bytes.TryGetValue(url, out byte[]? b) ? b : null);

    public string ResolveFileUrl(LatestVersionInfo latest, string relativePath) => relativePath;

    public Task<bool> DownloadFileAsync(string fileUrl, string destPath, long maxBytes, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default)
    {
        DownloadCalls++;
        if (!Files.TryGetValue(fileUrl, out byte[]? bytes))
        {
            return Task.FromResult(false);
        }

        int attempt = Attempts.TryGetValue(fileUrl, out int n) ? n : 0;
        Attempts[fileUrl] = attempt + 1;

        byte[] toWrite = bytes;
        if (AlwaysCorrupt.Contains(fileUrl) || (CorruptFirstAttempt.Contains(fileUrl) && attempt == 0))
        {
            toWrite = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        }

        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(destPath, toWrite);
        bytesProgress?.Report(toWrite.Length);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Publishes a signed manifest: stores its raw JSON bytes at <paramref name="manifestUrl"/> and a
    /// detached signature at "<paramref name="manifestUrl"/>.sig", and sets <see cref="Latest"/>.
    /// </summary>
    public void PublishSigned(UpdateManifest manifest, string manifestUrl, string privateKeyPem, bool required = false)
    {
        byte[] manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifest.Serialize());
        Bytes[manifestUrl] = manifestBytes;
        Bytes[manifestUrl + ".sig"] = ManifestSigner.Sign(manifestBytes, privateKeyPem);
        RemoteManifest = manifest;
        Latest = new LatestVersionInfo(manifest.Version, manifest.Version, manifestUrl, required);
    }

    /// <summary>Adds a file's correct bytes and returns its lowercase-hex SHA256 for manifest entries.</summary>
    public string Add(string relativePath, string content)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        Files[relativePath] = bytes;
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string Sha(string content)
        => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
}
