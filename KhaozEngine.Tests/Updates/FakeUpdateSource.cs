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
internal sealed class FakeUpdateSource : IUpdateSource
{
    public LatestVersionInfo? Latest;
    public UpdateManifest? RemoteManifest;
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
    public readonly HashSet<string> CorruptFirstAttempt = new(StringComparer.Ordinal);
    public readonly HashSet<string> AlwaysCorrupt = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> Attempts = new(StringComparer.Ordinal);
    public int DownloadCalls;

    public Task<LatestVersionInfo?> CheckLatestVersionAsync(string platform, CancellationToken cancellationToken = default)
        => Task.FromResult(Latest);

    public Task<UpdateManifest?> DownloadManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
        => Task.FromResult(RemoteManifest);

    public string ResolveFileUrl(LatestVersionInfo latest, string relativePath) => relativePath;

    public Task<bool> DownloadFileAsync(string fileUrl, string destPath, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default)
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
