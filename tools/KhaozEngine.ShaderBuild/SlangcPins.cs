using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KhaozEngine.ShaderBuild;

/// <summary>The pinned slangc version and its per-host release archives.</summary>
public sealed record SlangcPins(string Version, IReadOnlyDictionary<string, SlangcAsset> Assets)
{
    /// <summary>Reads and validates a pin file such as build/slangc.pins.json.</summary>
    public static SlangcPins Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var version = RequiredString(root, "version");
            var assets = new Dictionary<string, SlangcAsset>(StringComparer.Ordinal);
            if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("'assets' must be an object");
            foreach (var asset in assetsElement.EnumerateObject())
                assets.Add(asset.Name, ReadAsset(asset.Name, asset.Value));
            if (assets.Count == 0)
                throw new FormatException("'assets' lists no host");
            return new SlangcPins(version, assets);
        }
        catch (Exception problem) when (problem is FormatException or JsonException or InvalidOperationException)
        {
            throw new ShaderBuildException(
                ShaderBuildFailure.ManifestInvalid, $"The slangc pin file {path} is invalid: {problem.Message}", innerException: problem);
        }
    }

    private static SlangcAsset ReadAsset(string rid, JsonElement element)
    {
        if (!Uri.TryCreate(RequiredString(element, "url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new FormatException($"'{rid}' url must be an absolute https url");
        var sha256 = RequiredString(element, "sha256");
        if (!Regex.IsMatch(sha256, "^[0-9a-f]{64}$"))
            throw new FormatException($"'{rid}' sha256 must be 64 lowercase hex digits");
        var executable = RequiredString(element, "executable");
        if (Path.IsPathRooted(executable) || executable.Split('/', '\\').AsSpan().Contains(".."))
            throw new FormatException($"'{rid}' executable must be a path inside the archive");
        return new SlangcAsset(url, sha256, executable);
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"'{name}' must be a non-empty string");
        return value.GetString()!;
    }
}

/// <summary>One host's slangc release archive, its SHA-256 and the executable's path inside it.</summary>
public sealed record SlangcAsset(Uri Url, string Sha256, string Executable);
