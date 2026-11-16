using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Opens legacy evidence or a complete hashed set. Every open verifies fresh bytes to EOF. A root with an
/// incomplete publication refuses with <see cref="CoverageBuildValidationRefusal.OutputRecoveryRequired"/>.</summary>
public static class CoverageArtifactReader
{
    public static CoverageArtifactReadSession Open(string rootPath, string scratchDirectory)
    {
        string root = Path.GetFullPath(rootPath);
        // A root of an incomplete publication is never admitted, whichever bytes it currently holds.
        CoveragePublicationJournal.EnsureReadable(root);
        string scratch = CoverageArtifactSet.Scratch(scratchDirectory);
        try
        {
            string snapshot = Path.Combine(scratch, "root"), payload = Path.Combine(scratch, "payload");
            using (var input = File.OpenRead(root))
            using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                CoverageArtifactSet.Capacity(input.Length, CoverageArtifactSet.MaxBytes);
                Copy(input, output, CoverageArtifactSet.MaxBytes);
            }
            string hash = CoverageArtifactSet.HashFile(snapshot), schema;
            using (var input = File.OpenRead(snapshot)) schema = CoverageLogicalDocumentReader.ProbeSchema(input) ?? "";
            CoverageArtifactKind kind;
            CoverageArtifactHeader? header = null;
            if (schema is "khaozengine.coverage-evidence-set/1" or "khaozengine.coverage-diagnostics-set/1")
            {
                kind = schema == CoverageArtifactSet.Schema(CoverageArtifactKind.Evidence) ? CoverageArtifactKind.Evidence : CoverageArtifactKind.Diagnostics;
                header = ReadSet(snapshot, root, payload, kind);
            }
            else
            {
                kind = schema == "" ? CoverageArtifactKind.Evidence : CoverageArtifactKind.Diagnostics;
                File.Move(snapshot, payload);
            }
            using (var input = File.OpenRead(payload))
            {
                var inspection = CoverageLogicalDocumentReader.Validate(input, kind);
                if (header != null) inspection.Match(header);
            }
            return CoverageArtifactReadSession.Create(scratch, payload, hash);
        }
        catch (JsonException error)
        {
            Directory.Delete(scratch, true);
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.InvalidJson, "Malformed artifact index.", error) { Path = error.Path ?? "$" };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(scratch, true);
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.InputIo, "Cannot read artifact.", error) { Identity = root };
        }
        catch { Directory.Delete(scratch, true); throw; }
    }
    static CoverageArtifactHeader ReadSet(string snapshot, string rootPath, string payload, CoverageArtifactKind kind)
    {
        using var document = CoverageBuildInputJson.Document(File.ReadAllBytes(snapshot));
        var root = document.RootElement;
        var members = new List<string> { "schema", "scope", "complete", "inputSha256", "tableSha256", "identity", "toolCodeSha256",
            "render3DCodeSha256", "payloadSchema", "totals", "payloadByteLength", "payloadSha256", "partCount", "parts" };
        if (kind == CoverageArtifactKind.Diagnostics) members.AddRange(["resumedEvidenceSha256", "command", "outcome"]);
        CoverageBuildInputJson.Members(root, "$", members.ToArray());
        foreach (string member in members) CoverageBuildInputJson.Get(root, member, "$");
        var identity = root.GetProperty("identity");
        string[] identities = ["hostSha256", "hostMapSha256", "configurationSha256", "refinementSha256", "arithmeticSha256", "stateSchemaSha256"];
        CoverageBuildInputJson.Members(identity, "$.identity", identities);
        string Identity(string name) => CoverageBuildInputJson.Digest(CoverageBuildInputJson.Get(identity, name, "$.identity"), "$.identity." + name);
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        var declaredTotals = root.GetProperty("totals");
        if (declaredTotals.ValueKind != JsonValueKind.Object) throw CoverageArtifactSet.Invalid("$.totals");
        foreach (var total in declaredTotals.EnumerateObject()) totals.Add(total.Name, Long(total.Value, "$.totals." + total.Name));
        var header = new CoverageArtifactHeader(kind, Text(root, "payloadSchema"), Text(root, "scope"), Digest(root, "inputSha256"),
            NullableDigest(root, "tableSha256"), new(Identity(identities[0]), Identity(identities[1]), Identity(identities[2]), Identity(identities[3]), Identity(identities[4]), Identity(identities[5])),
            Digest(root, "toolCodeSha256"), Digest(root, "render3DCodeSha256"), totals.ToImmutableDictionary(StringComparer.Ordinal), Boolean(root.GetProperty("complete")),
            kind == CoverageArtifactKind.Diagnostics ? NullableDigest(root, "resumedEvidenceSha256") : null,
            kind == CoverageArtifactKind.Diagnostics ? Text(root, "command") : null, kind == CoverageArtifactKind.Diagnostics ? Text(root, "outcome") : null);
        ReadParts(root, rootPath, payload);
        return header;
    }
    static void ReadParts(JsonElement root, string rootPath, string payload)
    {
        string payloadHash = Digest(root, "payloadSha256");
        long expectedLength = Long(root.GetProperty("payloadByteLength"), "$.payloadByteLength");
        var parts = root.GetProperty("parts");
        if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0 || parts.GetArrayLength() > CoverageArtifactSet.MaxArtifactParts
            || parts.GetArrayLength() != Long(root.GetProperty("partCount"), "$.partCount")) throw CoverageArtifactSet.Invalid("$.parts");
        byte[] buffer = new byte[CoverageArtifactSet.BufferBytes];
        using var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        long total = 0, target = 0; int index = 0;
        foreach (var part in parts.EnumerateArray())
        {
            string path = "$.parts[" + index + "]";
            CoverageBuildInputJson.Members(part, path, "ordinal", "path", "byteLength", "sha256");
            if (Long(CoverageBuildInputJson.Get(part, "ordinal", path), path + ".ordinal") != index) throw CoverageArtifactSet.Invalid(path + ".ordinal");
            long length = Long(CoverageBuildInputJson.Get(part, "byteLength", path), path + ".byteLength");
            string hash = CoverageBuildInputJson.Digest(CoverageBuildInputJson.Get(part, "sha256", path), path + ".sha256");
            string relative = CoverageBuildInputJson.Text(part, "path", path);
            if (length is < 1 or > CoverageArtifactSet.MaxBytes || relative != CoverageArtifactSet.PartPath(rootPath, payloadHash, index, hash)) throw CoverageArtifactSet.Invalid(path);
            if (index == 0) target = length;
            if (length > target || index < parts.GetArrayLength() - 1 && length != target) throw CoverageArtifactSet.Invalid(path + ".byteLength");
            string source;
            try { source = CoverageInputPath.ResolveContained(Path.GetDirectoryName(rootPath)!, relative, new(path + ".path", rootPath, index)); }
            catch (CoverageBuildDataException error) when (error.Reason is CoverageBuildInputRefusal.PackedPathEscape or CoverageBuildInputRefusal.PackedPathLink)
            { throw new CoverageBuildDataException(CoverageBuildInputRefusal.ArtifactSetInvalid, "Invalid artifact part path.", error) { Path = path + ".path", ChunkIndex = index, Identity = relative }; }
            catch (CoverageBuildDataException error) when (error.Reason == CoverageBuildInputRefusal.PackedChunkMissing)
            { throw PartError(CoverageBuildInputRefusal.ArtifactPartMissing, path, index, relative); }
            using var input = File.OpenRead(source);
            if (input.Length != length) throw PartError(CoverageBuildInputRefusal.ArtifactPartLengthMismatch, path, index, source, length, input.Length);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0; int count;
            while ((count = input.Read(buffer)) != 0)
            {
                copied += count;
                if (copied > length) throw PartError(CoverageBuildInputRefusal.ArtifactPartLengthMismatch, path, index, source, length, copied);
                digest.AppendData(buffer.AsSpan(0, count)); output.Write(buffer, 0, count);
            }
            if (copied != length) throw PartError(CoverageBuildInputRefusal.ArtifactPartLengthMismatch, path, index, source, length, copied);
            string actual = Convert.ToHexStringLower(digest.GetHashAndReset());
            if (actual != hash) throw PartError(CoverageBuildInputRefusal.ArtifactPartHashMismatch, path, index, source, expectedHash: hash, actualHash: actual);
            total += copied; index++;
        }
        output.Flush();
        if (total != expectedLength) throw CoverageArtifactSet.Invalid("$.payloadByteLength");
        output.Close();
        if (CoverageArtifactSet.HashFile(payload) != payloadHash) throw CoverageArtifactSet.Invalid("$.payloadSha256");
    }
    static CoverageBuildDataException PartError(CoverageBuildInputRefusal reason, string path, int index, string identity,
        long? expected = null, long? actual = null, string? expectedHash = null, string? actualHash = null) =>
        new(reason, "Artifact part verification failed.")
        {
            Path = path + ".path", ChunkIndex = index, Identity = identity, ExpectedLength = expected, ActualLength = actual,
            ExpectedSha256 = expectedHash, ActualSha256 = actualHash,
        };
    static void Copy(Stream input, Stream output, long limit)
    {
        byte[] buffer = new byte[CoverageArtifactSet.BufferBytes]; long length = 0; int count;
        while ((count = input.Read(buffer)) != 0)
        {
            length += count; CoverageArtifactSet.Capacity(length, limit); output.Write(buffer, 0, count);
        }
    }
    static long Long(JsonElement value, string path) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result) && result >= 0
        ? result : throw CoverageArtifactSet.Invalid(path);
    static string Text(JsonElement root, string name) => CoverageBuildInputJson.Text(root, name, "$");
    static string Digest(JsonElement root, string name) => CoverageBuildInputJson.Digest(root.GetProperty(name), "$." + name);
    static string? NullableDigest(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Digest(root, name);
    static bool Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw CoverageArtifactSet.Invalid("$.complete");
}

/// <summary>Owns a verified private payload snapshot. Dispose after all readers have closed.</summary>
public sealed class CoverageArtifactReadSession : IDisposable
{
    readonly string scratch, payload;
    bool disposed;
    public string RootSha256 { get; }
    private CoverageArtifactReadSession(string scratch, string payload, string hash)
    { this.scratch = scratch; this.payload = payload; RootSha256 = hash; }
    internal static CoverageArtifactReadSession Create(string scratch, string payload, string hash) => new(scratch, payload, hash);
    public Stream OpenPayload()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return File.OpenRead(payload);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Directory.Delete(scratch, true);
    }
}
