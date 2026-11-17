using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

internal enum CoverageArtifactKind { Evidence, Diagnostics }
internal sealed record CoverageArtifactHeader(CoverageArtifactKind Kind, string PayloadSchema, string Scope,
    string InputSha256, string? TableSha256, CoverageRecessionIdentity Identity,
    string ToolCodeSha256, string Render3DCodeSha256, IReadOnlyDictionary<string, long> Totals,
    bool Complete, string? ResumedEvidenceSha256 = null, string? Command = null, string? Outcome = null)
{
    readonly ImmutableDictionary<string, long> totals = Totals.ToImmutableDictionary(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, long> Totals { get => totals; init => totals = value.ToImmutableDictionary(StringComparer.Ordinal); }
}
internal sealed record CoverageArtifactPart(int Ordinal, string Path, long ByteLength, string Sha256,
    string DestinationPath, string StagePath);

internal sealed class CoverageStagedArtifact : IDisposable
{
    readonly string scratch;
    internal string RootDestinationPath { get; }
    internal string RootStagePath { get; }
    internal string RootSha256 { get; }
    internal IReadOnlyList<CoverageArtifactPart> Parts { get; }
    internal CoverageArtifactHeader Header { get; }
    /// <summary>The largest single payload write or copy buffer staging used. A producer that buffered its whole
    /// document would show its full length here.</summary>
    internal int MaximumBufferBytesForTests { get; }
    private CoverageStagedArtifact(string destination, string root, string hash, string scratch,
        CoverageArtifactHeader header, IEnumerable<CoverageArtifactPart> parts, int maximumBufferBytes)
    {
        RootDestinationPath = destination; RootStagePath = root; RootSha256 = hash; this.scratch = scratch;
        MaximumBufferBytesForTests = maximumBufferBytes;
        Header = header with { Totals = header.Totals.ToImmutableDictionary(StringComparer.Ordinal) };
        Parts = ImmutableArray.CreateRange(parts.ToArray());
    }
    internal static CoverageStagedArtifact Create(string destination, string root, string scratch,
        CoverageArtifactHeader header, IEnumerable<CoverageArtifactPart> parts, int maximumBufferBytes) =>
        new(destination, root, CoverageArtifactSet.HashFile(root), scratch, header, parts, maximumBufferBytes);

    /// <summary>The complete staged logical document: the root itself, or its parts in ordinal order.</summary>
    internal Stream OpenPayload() => Parts.Count == 0 ? File.OpenRead(RootStagePath)
        : new CoverageConcatenatedStream(Parts.Select(p => p.StagePath).ToArray());
    public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
}

internal static class CoverageArtifactSet
{
    internal const int MaxBytes = 67_108_864, MaxArtifactParts = 65_536, BufferBytes = 65_536;
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };
    internal static long MaximumPayloadBytes(long manifestBytes, int entryCount)
    {
        if (manifestBytes < 0 || manifestBytes > MaxBytes || entryCount is < 0 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(manifestBytes));
        return checked(12L * Math.Max(1, entryCount) * manifestBytes + 64L * 1024 * 1024 * 1024);
    }
    internal static string Schema(CoverageArtifactKind kind) => kind == CoverageArtifactKind.Evidence
        ? "khaozengine.coverage-evidence-set/1" : "khaozengine.coverage-diagnostics-set/1";
    /// <summary>A schema in a known set family with a version other than 1 is a future envelope, refused with its
    /// version rather than read as a legacy diagnostic. Anything else is left to the logical reader.</summary>
    internal static void CheckSetVersion(string schema)
    {
        foreach (var kind in new[] { CoverageArtifactKind.Evidence, CoverageArtifactKind.Diagnostics })
        {
            string current = Schema(kind), family = current[..^1];
            if (!schema.StartsWith(family, StringComparison.Ordinal) || schema == current) continue;
            string version = schema[family.Length..];
            if (version.Length is < 1 or > 18 || !version.All(char.IsAsciiDigit) || version[0] == '0') throw Invalid("$.schema");
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.UnsupportedVersion, "Unsupported artifact set version.")
            { Path = "$.schema", FormatVersion = long.Parse(version, System.Globalization.CultureInfo.InvariantCulture) };
        }
    }
    internal static long DescriptorCapacityBound(string rootFileName, long payloadBytes, int partBytes)
    {
        // A descriptor has four keys, three 64-character hashes (payload and part hash in its path, plus its sha256),
        // a five-digit ordinal and bounded decimal lengths.
        // 512 bytes covers all fixed syntax and widths. Only the escaped root name varies with the caller.
        int rootBytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(rootFileName));
        return checked(((payloadBytes + partBytes - 1) / partBytes) * (rootBytes + 512L));
    }
    internal static string PartPath(string root, string payloadHash, int ordinal, string hash) =>
        System.IO.Path.GetFileName(root) + ".parts/" + payloadHash + "/" + ordinal.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + "-" + hash + ".part";
    internal static string Scratch(string parent)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetFullPath(parent), "coverage-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    internal static void Capacity(long count, long limit)
    {
        if (count > limit) throw new CoverageBuildDataException(CoverageBuildInputRefusal.CapacityExceeded, "Artifact capacity exceeded.")
        { Count = count, Limit = limit };
    }
    internal static CoverageBuildDataException Invalid(string path = "$") =>
        new(CoverageBuildInputRefusal.ArtifactSetInvalid, "Artifact closure is invalid.") { Path = path };
    internal static void WriteHeader(Utf8JsonWriter writer, CoverageArtifactHeader header)
    {
        writer.WriteString("scope", header.Scope); writer.WriteBoolean("complete", header.Complete);
        writer.WriteString("inputSha256", header.InputSha256); writer.WriteString("tableSha256", header.TableSha256);
        writer.WritePropertyName("identity"); JsonSerializer.Serialize(writer, header.Identity, Options);
        writer.WriteString("toolCodeSha256", header.ToolCodeSha256); writer.WriteString("render3DCodeSha256", header.Render3DCodeSha256);
        if (header.Kind == CoverageArtifactKind.Diagnostics)
        {
            writer.WriteString("resumedEvidenceSha256", header.ResumedEvidenceSha256);
            writer.WriteString("command", header.Command); writer.WriteString("outcome", header.Outcome);
        }
        writer.WriteString("payloadSchema", header.PayloadSchema);
        writer.WriteStartObject("totals");
        foreach (var total in header.Totals.OrderBy(p => p.Key, StringComparer.Ordinal)) writer.WriteNumber(total.Key, total.Value);
        writer.WriteEndObject();
    }
}
