using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record ShippedSourcePath(string Path, string Sha256);

internal sealed record ShippedSourceProvenance
{
    internal const string AggregateDefinition = "sha256 of UTF-8 lines path TAB sha256 LF, ordinal by path";
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public string SourceRepository { get; init; } = "";
    [JsonRequired]
    public string Tag { get; init; } = "";
    public string Commit { get; init; } = "";
    public string SourceEnginePin { get; init; } = "";
    public IReadOnlyList<string> ExtractionPathspec { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ShippedSourcePath> Paths { get; init; } = Array.Empty<ShippedSourcePath>();
    public string AggregateSha256 { get; init; } = "";
    public string AggregateRule { get; init; } = "";
    public JsonElement Units { get; init; }
    public string RowOrientation { get; init; } = "";
    public JsonElement Comparison { get; init; }
    public JsonElement OracleEquivalence { get; init; }

    internal static ShippedSourceProvenance Load(string path) => Parse(File.ReadAllBytes(path));

    internal static ShippedSourceProvenance Parse(byte[] bytes)
    {
        var value = JsonSerializer.Deserialize<ShippedSourceProvenance>(bytes, JsonOptions)
            ?? throw new InvalidDataException("invalid provenance");
        if (string.IsNullOrWhiteSpace(value.SourceRepository) || value.Tag is null ||
            !IsHex(value.Commit, 40) || string.IsNullOrWhiteSpace(value.SourceEnginePin) ||
            value.ExtractionPathspec is null || value.ExtractionPathspec.Count == 0 ||
            value.ExtractionPathspec.Any(p => !SafeRelativePath(p)) ||
            value.Paths is null || value.Paths.Count == 0 ||
            value.Paths.Any(p => p is null || !SafeRelativePath(p.Path) || !IsHex(p.Sha256, 64)) ||
            value.Paths.Select(p => p.Path).Distinct(StringComparer.Ordinal).Count() != value.Paths.Count ||
            !IsHex(value.AggregateSha256, 64) || value.AggregateRule != AggregateDefinition ||
            string.IsNullOrWhiteSpace(value.RowOrientation) || Missing(value.Units) ||
            Missing(value.Comparison) || Missing(value.OracleEquivalence) ||
            value.ComputeAggregate() != value.AggregateSha256)
            throw new InvalidDataException("invalid provenance");
        return value with
        {
            Paths = Array.AsReadOnly(value.Paths.ToArray()),
            ExtractionPathspec = Array.AsReadOnly(value.ExtractionPathspec.ToArray()),
        };
    }

    internal string ComputeAggregate()
    {
        var text = new StringBuilder();
        foreach (ShippedSourcePath path in Paths.OrderBy(p => p.Path, StringComparer.Ordinal))
            text.Append(path.Path).Append('\t').Append(path.Sha256).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    internal int Verify(string extractedRoot)
    {
        try
        {
            string root = PrivateOraclePaths.Canonical(extractedRoot);
            if (!Directory.Exists(root)) return 1;
            var expected = Paths.ToDictionary(p => p.Path, p => p.Sha256, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(root);
            int mismatches = 0;
            while (pending.Count != 0)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        mismatches = checked(mismatches + 1);
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                    string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (!expected.TryGetValue(relative, out string? hash))
                    {
                        mismatches = checked(mismatches + 1);
                        continue;
                    }
                    seen.Add(relative);
                    using FileStream stream = File.OpenRead(path);
                    if (Convert.ToHexStringLower(SHA256.HashData(stream)) != hash)
                        mismatches = checked(mismatches + 1);
                }
            }
            return checked(mismatches + expected.Keys.Count(path => !seen.Contains(path)));
        }
        catch (Exception) { return 1; }
    }

    static bool Missing(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
    static bool IsHex(string? value, int length) => value is not null && value.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static bool SafeRelativePath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) &&
        path.Split('/').All(part => part.Length != 0 && part is not "." and not "..");
}
