using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.Attachments;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Strict manifest reader with an eager, independent snapshot result. Relative references resolve beside the document that declares them.</summary>
public static class CoverageBuildInputJson
{
    internal const int MaxBytes = 64 * 1024 * 1024;
    internal sealed record Command(EquipmentCoverageCommand Definition, CoverageSupportSurface Support, int Region);

    public static CoverageBuildInput Read(string manifestPath)
    {
        using var session = CoverageInputSession.Open(manifestPath, Path.GetTempPath());
        return session.Campaign.Materialize();
    }

    internal static CoverageInputBinding ReadBinding(JsonElement root, string folder, List<string> sourcePaths)
    {
        byte[] hostBytes = Asset(root, "host", folder, "$", sourcePaths);
        byte[] mapBytes = Asset(root, "map", folder, "$", sourcePaths);
        var map = HostCoverageMapJson.Read(mapBytes, "map");
        var parts = GltfLoader.LoadSkinnedPartsWithMaterials(hostBytes, "host", map);
        GltfSkinnedMeshPart.ValidateCoverageParts(parts[0].Mesh.Coverage!, parts.Select(p => p.Mesh).ToArray());
        var topology = CoverageRefinement.Build(parts[0].Mesh.Coverage!.Source, map);
        var binds = Matrices(Get(root, "inverseBind", "$"), "$.inverseBind", 1024);
        if (binds.Length != parts[0].Mesh.InverseBind.Length || !binds.AsSpan().SequenceEqual(parts[0].Mesh.InverseBind))
            throw Error(CoverageBuildInputRefusal.AssetBinding, "$.inverseBind");
        byte[] configurationBytes = Asset(root, "configuration", folder, "$", sourcePaths);
        string configurationPath = Resolve(Get(root, "configuration", "$"), folder, "$.configuration");
        var (commands, configurationItem) = Configuration(configurationBytes, Path.GetDirectoryName(configurationPath)!, topology, sourcePaths);
        string stateHash = Digest(Get(root, "stateSchemaSha256", "$"), "$.stateSchemaSha256");
        var identity = new CoverageRecessionIdentity(Hash(hostBytes), map.SemanticSha256,
            CoverageConfigurationIdentity.Compute([configurationItem], binds), RefinementHash(topology), ArithmeticHash(), stateHash);
        return new(topology, binds, commands, identity);
    }

    static (Command[], CoverageConfigurationItem) Configuration(byte[] bytes, string folder, CoverageRefinement topology, List<string> sourcePaths)
    {
        using var doc = Document(bytes);
        var root = doc.RootElement;
        Members(root, "$", "formatVersion", "attachment", "companion", "parts");
        Version(root);
        byte[] attachmentBytes = Asset(root, "attachment", folder, "$", sourcePaths);
        byte[] companionBytes = Asset(root, "companion", folder, "$", sourcePaths);
        var attachment = AttachmentSetJson.Parse(attachmentBytes, "attachment");
        var companion = EquipmentCoverageJson.Read(companionBytes, "companion");
        if (companion.AttachmentSha256 != Hash(attachmentBytes) || companion.HostMapSha256 != topology.Map.SemanticSha256)
            throw Error(CoverageBuildInputRefusal.AssetBinding, "$.companion");
        var supportSources = new Dictionary<uint, CoverageSourceTopology>();
        var configurationParts = new List<CoverageConfigurationPart>();
        var seen = new HashSet<uint>();
        var commanded = companion.Commands.Select(c => c.PartId).ToHashSet();
        foreach (var part in Array(root, "parts", "$", CoverageBuildInput.MaxSamples).EnumerateArray())
        {
            Members(part, "$.parts", "id", "asset", "hostJoint", "socket", "bodyAlignment");
            uint id = UInt(part, "id", "$.parts");
            var declared = attachment.Parts.SingleOrDefault(p => p.Id == id);
            if (declared is null || !seen.Add(id))
                throw Error(CoverageBuildInputRefusal.AssetBinding, "$.parts.id");
            string joint = String(Get(part, "hostJoint", "$.parts"), "$.parts.hostJoint", allowEmpty: declared is SkinnedAttachmentPartDefinition);
            var socket = Matrix(Get(part, "socket", "$.parts"), "$.parts.socket");
            var alignment = Matrix(Get(part, "bodyAlignment", "$.parts"), "$.parts.bodyAlignment");
            if (declared is RigidAttachmentPartDefinition rigid)
            {
                if (joint != rigid.Joint) throw Error(CoverageBuildInputRefusal.AssetBinding, "$.parts.hostJoint");
                configurationParts.Add(new(id, joint, socket, alignment));
            }
            else
            {
                if (commanded.Contains(id) || joint != string.Empty || socket != Matrix4x4.Identity || alignment != Matrix4x4.Identity)
                    throw Error(CoverageBuildInputRefusal.AssetBinding, "$.parts.id");
                // Match AttachmentSetBinding's canonical representation for an uncommanded garment.
                configurationParts.Add(new(id, string.Empty, Matrix4x4.Identity, Matrix4x4.Identity));
            }
            byte[] asset = Asset(part, "asset", folder, "$.parts", sourcePaths);
            if (Hash(asset) != declared.Mesh.Sha256) throw Error(CoverageBuildInputRefusal.HashMismatch, "$.parts.asset.sha256");
            if (commanded.Contains(id)) supportSources.Add(id, GltfLoader.LoadCoverageSource(asset, $"part-{id}"));
        }
        if (seen.Count != attachment.Parts.Count) throw Error(CoverageBuildInputRefusal.AssetBinding, "$.parts");
        var commands = companion.Commands.Select(command =>
        {
            if (!supportSources.TryGetValue(command.PartId, out var source)) throw Error(CoverageBuildInputRefusal.AssetBinding, "$.companion.commands.partId");
            int region = topology.Map.Regions.Select((r, i) => (r, i)).Where(p => p.r.Key == command.HostRegion).Select(p => p.i).DefaultIfEmpty(-1).Single();
            if (region < 0) throw Error(CoverageBuildInputRefusal.AssetBinding, "$.companion.commands.hostRegion");
            var support = CoverageSupportSurface.Compile(source, command.InwardSupport.Select(r => new CoverageTriangleRange(r.Mesh, r.Primitive, r.FirstTriangle, r.Count)).ToArray(), command.CoverageLocal);
            return new Command(command, support, region);
        }).ToArray();
        return (commands, new(Hash(attachmentBytes), Hash(companionBytes), configurationParts.ToArray()));
    }

    internal static JsonDocument Document(byte[] bytes)
    {
        // Bound every array and member diagnostic before allocating the DOM. Sidecars retain their own stricter preflight.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
        if (!reader.Read()) throw Error(CoverageBuildInputRefusal.InvalidJson, "$");
        Preflight(ref reader, "$");
        if (reader.Read()) throw Error(CoverageBuildInputRefusal.InvalidJson, "$");
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
    }

    static void Preflight(ref Utf8JsonReader reader, string path)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string? name = JsonMemberDiagnostics.ReadName(ref reader);
                if (name == null) throw Error(CoverageBuildInputRefusal.UnknownMember, path);
                string next = path + "." + name;
                if (!names.Add(name)) throw new CoverageBuildDataException(CoverageBuildInputRefusal.DuplicateMember, "Duplicate JSON member.") { Path = next, MemberName = name };
                if (!reader.Read()) throw Error(CoverageBuildInputRefusal.InvalidJson, next);
                Preflight(ref reader, next);
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            int limit = path == "$.poseChunks" ? CoverageBuildInput.MaxPackedChunks
                : path.EndsWith(".candidateSampleIds", StringComparison.Ordinal) ? CoverageBuildInput.MaxCandidates
                : path.EndsWith(".commands", StringComparison.Ordinal) ? EquipmentCoverageDefinition.MaxCommands
                : path.EndsWith(".inverseBind", StringComparison.Ordinal) || path.EndsWith(".palette", StringComparison.Ordinal) ? 1024
                : path.EndsWith(".pairResults", StringComparison.Ordinal) ? CoverageBuildInput.MaxPairs : CoverageBuildInput.MaxSamples;
            int count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (count == limit) throw new CoverageBuildDataException(CoverageBuildInputRefusal.CapacityExceeded, "Array exceeds capacity.") { Path = path, Count = (long)count + 1, Limit = limit };
                Preflight(ref reader, $"{path}[{count++}]");
            }
        }
        else if (reader.TokenType == JsonTokenType.Number && (!reader.TryGetDouble(out double number) || !double.IsFinite(number)))
            throw Error(CoverageBuildInputRefusal.NonFiniteNumber, path);
    }

    internal static void Members(JsonElement value, string path, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Error(CoverageBuildInputRefusal.InvalidValue, path);
        foreach (var member in value.EnumerateObject())
            if (System.Array.IndexOf(allowed, member.Name) < 0)
                throw new CoverageBuildDataException(CoverageBuildInputRefusal.UnknownMember, "Unknown JSON member.") { Path = path + "." + member.Name, MemberName = member.Name };
    }
    internal static JsonElement Get(JsonElement value, string name, string path) => value.TryGetProperty(name, out var item) ? item
        : throw new CoverageBuildDataException(CoverageBuildInputRefusal.MissingMember, "Missing JSON member.") { Path = path + "." + name, MemberName = name };
    static void Version(JsonElement root)
    {
        var value = Get(root, "formatVersion", "$");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int version) || version != 1) throw Error(CoverageBuildInputRefusal.UnsupportedVersion, "$.formatVersion");
    }
    internal static JsonElement Array(JsonElement root, string name, string path, int limit)
    {
        var value = Get(root, name, path);
        if (value.ValueKind != JsonValueKind.Array) throw Error(CoverageBuildInputRefusal.InvalidValue, path + "." + name);
        if (value.GetArrayLength() > limit) throw new CoverageBuildDataException(CoverageBuildInputRefusal.CapacityExceeded, "Array exceeds capacity.")
        { Path = path + "." + name, Count = value.GetArrayLength(), Limit = limit };
        return value;
    }
    internal static string[] Strings(JsonElement root, string name, string path, int limit = CoverageBuildInput.MaxSamples) =>
        Array(root, name, path, limit).EnumerateArray().Select((x, i) => String(x, $"{path}.{name}[{i}]")).ToArray();
    internal static string Text(JsonElement root, string name, string path) => String(Get(root, name, path), path + "." + name);
    internal static string String(JsonElement value, string path, bool allowEmpty = false) => value.ValueKind == JsonValueKind.String
        && (allowEmpty && value.GetString() == string.Empty || !string.IsNullOrWhiteSpace(value.GetString()))
        ? value.GetString()! : throw Error(CoverageBuildInputRefusal.InvalidValue, path);
    internal static uint UInt(JsonElement value, string name, string path)
    {
        var item = Get(value, name, path);
        return item.ValueKind == JsonValueKind.Number && item.TryGetUInt32(out uint result) ? result
            : throw Error(CoverageBuildInputRefusal.InvalidValue, path + "." + name);
    }
    internal static float Single(JsonElement value, string path) => value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float result) && float.IsFinite(result)
        ? result : throw Error(CoverageBuildInputRefusal.NonFiniteNumber, path);
    internal static Matrix4x4[] Matrices(JsonElement array, string path, int limit)
    {
        if (array.ValueKind != JsonValueKind.Array) throw Error(CoverageBuildInputRefusal.InvalidValue, path);
        if (array.GetArrayLength() > limit) throw new CoverageBuildDataException(CoverageBuildInputRefusal.CapacityExceeded, "Palette exceeds capacity.") { Path = path, Count = array.GetArrayLength(), Limit = limit };
        return array.EnumerateArray().Select((x, i) => Matrix(x, $"{path}[{i}]")).ToArray();
    }
    internal static Matrix4x4 Matrix(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 16) throw Error(CoverageBuildInputRefusal.InvalidValue, path);
        float[] v = value.EnumerateArray().Select((x, i) => Single(x, $"{path}[{i}]")).ToArray();
        return new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
    }
    internal static string Digest(JsonElement value, string path)
    {
        string result = String(value, path);
        if (result.Length != 64 || result.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Error(CoverageBuildInputRefusal.MalformedDigest, path);
        return result;
    }
    static string Resolve(JsonElement value, string folder, string path) => System.IO.Path.GetFullPath(Text(value, "path", path), folder);
    static byte[] Asset(JsonElement parent, string name, string folder, string path, List<string> sourcePaths)
    {
        var value = Get(parent, name, path);
        path += "." + name;
        Members(value, path, "path", "sha256");
        string hash = Digest(Get(value, "sha256", path), path + ".sha256");
        string file = Resolve(value, folder, path);
        sourcePaths.Add(file);
        byte[] bytes = ReadBytes(file);
        if (Hash(bytes) != hash) throw new CoverageBuildDataException(CoverageBuildInputRefusal.HashMismatch, "Exact asset hash differs.") { Path = path + ".sha256", Identity = file };
        return bytes;
    }
    internal static byte[] ReadBytes(string path, int byteLimit = MaxBytes)
    {
        using var stream = File.OpenRead(path);
        CheckByteCapacity(stream.Length, byteLimit, path);
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
    internal static void CheckByteCapacity(long count, int byteLimit, string? identity = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteLimit, MaxBytes);
        if (count > byteLimit)
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.CapacityExceeded, "Artifact exceeds byte capacity.")
            { Identity = identity, Count = count, Limit = byteLimit };
    }
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string CodeHash() => Hash(File.ReadAllBytes(typeof(CoverageBuildInput).Assembly.Location));
    internal static string Render3DCodeHash() => Hash(File.ReadAllBytes(typeof(CoverageRefinement).Assembly.Location));
    internal static string ArithmeticHash() => CoverageTableBinding.ArithmeticSha256;
    internal static string RefinementHash(CoverageRefinement topology) => CoverageTableBinding.RefinementSha256(topology);
    internal static float[] Values(Matrix4x4 m) => [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
    static CoverageBuildDataException Error(CoverageBuildInputRefusal reason, string path) => new(reason, "Coverage input was refused.") { Path = path };
}

internal sealed record CoverageInputBinding(CoverageRefinement Topology, Matrix4x4[] InverseBind,
    CoverageBuildInputJson.Command[] Commands, CoverageRecessionIdentity Identity);
