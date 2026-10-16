using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static KhaozEngine.Attachments.AttachmentJson;

namespace KhaozEngine.Attachments;

/// <summary>
/// Strict equipment coverage JSON format 1, a companion file separate from attachment JSON v1/v2. Reading refuses
/// unknown or duplicate members, other versions, nonfinite numbers and over-limit arrays before allocating for them,
/// and reports the caller's asset identity in every refusal. Writing is canonical: fixed member order, commands by ID,
/// support ranges by source ordinal, normalized negative zero and no whitespace. The semantic digest hashes a domain
/// prefix and the canonical bytes, so it never equals the exact byte hash of a fetched file.
/// </summary>
public static class EquipmentCoverageJson
{
    public const int FormatVersion = 1;

    static readonly byte[] SemanticDomain = Encoding.UTF8.GetBytes("KhaozEngine.EquipmentCoverage/1\n");

    public static EquipmentCoverageDefinition Read(ReadOnlySpan<byte> json, string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        try
        {
            EquipmentCoverageJsonPreflight.Validate(json);
            var reader = new Utf8JsonReader(json);
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            Members(root, "root", "formatVersion", "attachmentSha256", "hostMapSha256", "commands");
            JsonElement version = Required(root, "formatVersion", "root");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != FormatVersion)
                throw new InvalidDataException("root: unsupported formatVersion.");
            string attachmentSha256 = Text(root, "attachmentSha256", "root");
            string hostMapSha256 = Text(root, "hostMapSha256", "root");
            JsonElement commandArray = BoundedArray(root, "commands", "root", EquipmentCoverageDefinition.MaxCommands);
            var commands = new EquipmentCoverageCommand[commandArray.GetArrayLength()];
            int index = 0;
            foreach (JsonElement item in commandArray.EnumerateArray())
                commands[index] = ReadCommand(item, $"commands[{index++}]");
            return new EquipmentCoverageDefinition(attachmentSha256, hostMapSha256, commands);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"{identity}: {error.Message}", error);
        }
    }

    public static byte[] Write(EquipmentCoverageDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("attachmentSha256", definition.AttachmentSha256);
            writer.WriteString("hostMapSha256", definition.HostMapSha256);
            writer.WriteStartArray("commands");
            foreach (EquipmentCoverageCommand command in definition.Commands)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", command.Id);
                writer.WriteNumber("partId", command.PartId);
                writer.WriteString("hostRegion", command.HostRegion);
                writer.WriteStartArray("coverageLocal");
                foreach (float value in AttachmentSetDefinition.Values(command.CoverageLocal))
                    writer.WriteNumberValue(value == 0f ? 0f : value);
                writer.WriteEndArray();
                writer.WriteStartArray("inwardSupport");
                foreach (EquipmentSupportRange range in command.InwardSupport)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("mesh", range.Mesh);
                    writer.WriteNumber("primitive", range.Primitive);
                    writer.WriteNumber("firstTriangle", range.FirstTriangle);
                    writer.WriteNumber("count", range.Count);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteNumber("collarWidth", command.CollarWidth);
                writer.WriteNumber("minZ", command.MinZ == 0f ? 0f : command.MinZ);
                writer.WriteNumber("maxZ", command.MaxZ == 0f ? 0f : command.MaxZ);
                writer.WriteNumber("clearance", command.Clearance);
                writer.WriteNumber("maxWorldRecession", command.MaxWorldRecession == 0f ? 0f : command.MaxWorldRecession);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Lowercase SHA-256 of the equipment coverage domain prefix followed by the canonical bytes. Names the
    /// companion identity independent of whitespace, member order and entry order in the source file.</summary>
    public static string SemanticSha256(EquipmentCoverageDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.SemanticSha256;
    }

    internal static string ComputeSemanticSha256(EquipmentCoverageDefinition definition)
    {
        byte[] canonical = Write(definition);
        byte[] prefixed = new byte[SemanticDomain.Length + canonical.Length];
        SemanticDomain.CopyTo(prefixed, 0);
        canonical.CopyTo(prefixed, SemanticDomain.Length);
        return Convert.ToHexStringLower(SHA256.HashData(prefixed));
    }

    static EquipmentCoverageCommand ReadCommand(JsonElement item, string context)
    {
        Members(item, context, "id", "partId", "hostRegion", "coverageLocal", "inwardSupport", "collarWidth", "minZ", "maxZ",
            "clearance", "maxWorldRecession");
        uint id = Identifier(item, "id", context);
        context += $" command {id}";
        uint partId = Identifier(item, "partId", context);
        string hostRegion = Text(item, "hostRegion", context);

        JsonElement local = Required(item, "coverageLocal", context);
        if (local.ValueKind != JsonValueKind.Array || local.GetArrayLength() != 16)
            throw new InvalidDataException($"{context}: coverageLocal must contain 16 row-major numbers.");
        Span<float> values = stackalloc float[16];
        int component = 0;
        foreach (JsonElement value in local.EnumerateArray())
        {
            values[component] = Finite(value, $"coverageLocal[{component}]", context);
            component++;
        }
        var coverageLocal = new Matrix4x4(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);

        JsonElement supportArray = BoundedArray(item, "inwardSupport", context, EquipmentCoverageCommand.MaxSupportTriangles);
        var support = new EquipmentSupportRange[supportArray.GetArrayLength()];
        int index = 0;
        foreach (JsonElement entry in supportArray.EnumerateArray())
        {
            string entryContext = $"{context} inwardSupport[{index}]";
            Members(entry, entryContext, "mesh", "primitive", "firstTriangle", "count");
            support[index++] = new(Integer(entry, "mesh", entryContext), Integer(entry, "primitive", entryContext),
                Integer(entry, "firstTriangle", entryContext), Integer(entry, "count", entryContext));
        }

        return new EquipmentCoverageCommand(id, partId, hostRegion, coverageLocal, support,
            Single(item, "collarWidth", context), Single(item, "minZ", context), Single(item, "maxZ", context),
            Single(item, "clearance", context), Single(item, "maxWorldRecession", context));
    }

    /// <summary>Returns a required array after checking its length against the limit, before any allocation for it.</summary>
    static JsonElement BoundedArray(JsonElement value, string name, string context, int limit)
    {
        JsonElement member = Required(value, name, context);
        if (member.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{context}: '{name}' must be an array.");
        if (member.GetArrayLength() > limit)
            throw new InvalidDataException($"{context}: '{name}' exceeds the limit of {limit} entries.");
        return member;
    }

    static uint Identifier(JsonElement value, string name, string context)
    {
        JsonElement member = Required(value, name, context);
        if (member.ValueKind != JsonValueKind.Number || !member.TryGetUInt32(out uint result))
            throw new InvalidDataException($"{context}: '{name}' must be a positive uint.");
        return result;
    }

    static int Integer(JsonElement value, string name, string context)
    {
        JsonElement member = Required(value, name, context);
        if (member.ValueKind != JsonValueKind.Number || !member.TryGetInt32(out int result))
            throw new InvalidDataException($"{context}: '{name}' must be a 32-bit integer.");
        return result;
    }

    static float Single(JsonElement value, string name, string context) => Finite(Required(value, name, context), name, context);

    static float Finite(JsonElement value, string name, string context)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float result) || !float.IsFinite(result))
            throw new InvalidDataException($"{context}: '{name}' must be a finite float.");
        return result;
    }
}
