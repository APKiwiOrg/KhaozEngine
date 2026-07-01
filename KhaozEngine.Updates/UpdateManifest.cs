using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// A snapshot of every file in a build: relative path, SHA256, and byte size. Published builds ship
/// one of these; the client diffs the remote manifest against a local one to download only changes.
/// Wire format is stable JSON (camelCase) so it round-trips with offline manifest generators.
/// </summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("publishedAtUtc")]
    public DateTime PublishedAtUtc { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("files")]
    public List<ManifestFileEntry> Files { get; set; } = new();

    // Intentionally NOT Serialization.JsonDefaults: the update manifest is a stable camelCase wire
    // format shared with offline manifest generators (ke-updater); changing it would orphan signed manifests.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>Parses a manifest from JSON. Returns null on malformed input handled by the caller.</summary>
    public static UpdateManifest? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
    }

    /// <summary>Serializes to the canonical camelCase, indented JSON wire format.</summary>
    public string Serialize()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    /// <summary>
    /// Walks <paramref name="rootDir"/> recursively, hashing every file, and builds a manifest with
    /// forward-slash relative paths sorted ordinally. Used both to build the local manifest from an
    /// install dir and (offline) to generate a published build's manifest.
    /// </summary>
    public static UpdateManifest GenerateFromDirectory(string rootDir, string version, string platform)
    {
        string fullRoot = Path.GetFullPath(rootDir);
        var files = new List<ManifestFileEntry>();

        foreach (string filePath in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(fullRoot, filePath).Replace('\\', '/');
            var fileInfo = new FileInfo(filePath);
            files.Add(new ManifestFileEntry
            {
                Path = relativePath,
                Sha256 = ComputeSha256(filePath),
                Size = fileInfo.Length
            });
        }

        files.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));

        return new UpdateManifest
        {
            Version = version,
            Platform = platform,
            PublishedAtUtc = DateTime.UtcNow,
            Files = files
        };
    }

    /// <summary>
    /// Computes the work needed to turn <paramref name="local"/> into <paramref name="remote"/>:
    /// files new or hash-changed in remote go to <see cref="ManifestDiff.FilesToDownload"/>; files
    /// present locally but gone from remote go to <see cref="ManifestDiff.FilesToDelete"/>.
    /// </summary>
    public static ManifestDiff ComputeDiff(UpdateManifest local, UpdateManifest remote)
    {
        var localByPath = new Dictionary<string, ManifestFileEntry>(StringComparer.Ordinal);
        for (int i = 0; i < local.Files.Count; i++)
        {
            localByPath[local.Files[i].Path] = local.Files[i];
        }

        var filesToDownload = new List<ManifestFileEntry>();
        var remotePathSet = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < remote.Files.Count; i++)
        {
            ManifestFileEntry remoteFile = remote.Files[i];
            remotePathSet.Add(remoteFile.Path);

            if (!localByPath.TryGetValue(remoteFile.Path, out ManifestFileEntry? localFile)
                || !string.Equals(localFile.Sha256, remoteFile.Sha256, StringComparison.Ordinal))
            {
                filesToDownload.Add(remoteFile);
            }
        }

        var filesToDelete = new List<string>();
        for (int i = 0; i < local.Files.Count; i++)
        {
            if (!remotePathSet.Contains(local.Files[i].Path))
            {
                filesToDelete.Add(local.Files[i].Path);
            }
        }

        return new ManifestDiff(filesToDownload, filesToDelete);
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>One file in a manifest: forward-slash relative path, lowercase-hex SHA256, byte size.</summary>
public sealed class ManifestFileEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>The result of <see cref="UpdateManifest.ComputeDiff"/>: what to download and what to delete.</summary>
public sealed class ManifestDiff
{
    public IReadOnlyList<ManifestFileEntry> FilesToDownload { get; }
    public IReadOnlyList<string> FilesToDelete { get; }

    public long TotalDownloadBytes
    {
        get
        {
            long total = 0;
            for (int i = 0; i < FilesToDownload.Count; i++)
            {
                total += FilesToDownload[i].Size;
            }
            return total;
        }
    }

    public ManifestDiff(IReadOnlyList<ManifestFileEntry> filesToDownload, IReadOnlyList<string> filesToDelete)
    {
        FilesToDownload = filesToDownload;
        FilesToDelete = filesToDelete;
    }
}
