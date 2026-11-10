using System;
using System.IO;
using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
            Members(root, "$", "formatVersion", "attachmentSha256", "hostMapSha256", "commands");
            JsonElement version = Required(root, "formatVersion", "$");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != FormatVersion)
                throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.UnsupportedVersion, "Unsupported formatVersion.")
                { Path = "$.formatVersion", FormatVersion = version.TryGetInt32(out int actual) ? actual : null };
            string attachmentSha256 = Text(root, "attachmentSha256", "$");
            string hostMapSha256 = Text(root, "hostMapSha256", "$");
            JsonElement commandArray = BoundedArray(root, "commands", "$", EquipmentCoverageDefinition.MaxCommands);
            var commands = new EquipmentCoverageCommand[commandArray.GetArrayLength()];
            int index = 0;
            foreach (JsonElement item in commandArray.EnumerateArray())
                commands[index] = ReadCommand(item, $"$.commands[{index++}]");
            return new EquipmentCoverageDefinition(attachmentSha256, hostMapSha256, commands);
        }
        catch (EquipmentCoverageDataException error)
        {
            throw error.WithContext(identity: identity);
        }
        catch (JsonException error)
        {
            throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.InvalidJson, $"{identity}: invalid coverage JSON.", error)
            { Identity = identity, Path = error.Path ?? "$" };
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

        uint partId = Identifier(item, "partId", context, id);
        string hostRegion = Text(item, "hostRegion", context);

        JsonElement local = Required(item, "coverageLocal", context);
        if (local.ValueKind != JsonValueKind.Array || local.GetArrayLength() != 16)
            throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.FrameComponentCapacity, "Expected 16 frame components.")
            { Path = context + ".coverageLocal", Count = local.GetArrayLength(), Limit = 16, CommandId = id, PartId = partId };
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
            string entryContext = $"{context}.inwardSupport[{index}]";
            Members(entry, entryContext, "mesh", "primitive", "firstTriangle", "count");
            support[index++] = new(Integer(entry, "mesh", entryContext), Integer(entry, "primitive", entryContext),
                Integer(entry, "firstTriangle", entryContext), Integer(entry, "count", entryContext));
        }

        float collarWidth = Single(item, "collarWidth", context);
        float minZ = Single(item, "minZ", context), maxZ = Single(item, "maxZ", context);
        float clearance = Single(item, "clearance", context), maxRecession = Single(item, "maxWorldRecession", context);
        try
        {
            return new EquipmentCoverageCommand(id, partId, hostRegion, coverageLocal, support,
                collarWidth, minZ, maxZ, clearance, maxRecession);
        }
        catch (EquipmentCoverageDataException error)
        {
            throw error.WithContext(prefix: context);
        }
    }

    static void Members(JsonElement value, string context, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.ExpectedObject, "Expected an object.") { Path = context };
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty member in value.EnumerateObject())
        {
            if (!found.Add(member.Name))
                throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.DuplicateMember, "Duplicate coverage member.")
                { Path = EquipmentCoverageJsonPreflight.Property(context, member.Name), MemberName = member.Name };
            if (Array.IndexOf(allowed, member.Name) < 0)
                throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.UnknownMember, "Unknown coverage member.")
                { Path = EquipmentCoverageJsonPreflight.Property(context, member.Name), MemberName = member.Name };
        }
    }

    static JsonElement Required(JsonElement value, string name, string context) => value.TryGetProperty(name, out JsonElement result)
        ? result : throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.MissingMember, "Missing coverage member.")
        { Path = EquipmentCoverageJsonPreflight.Property(context, name), MemberName = name };

    static JsonElement BoundedArray(JsonElement value, string name, string context, int limit)
    {
        JsonElement member = Required(value, name, context);
        if (member.ValueKind != JsonValueKind.Array)
            throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.ExpectedArray, "Expected an array.") { Path = context + "." + name };
        int count = member.GetArrayLength();
        if (count > limit)
            throw new EquipmentCoverageDataException(EquipmentCoverageJsonPreflight.CapacityReason(name), "Coverage array exceeds its capacity.")
            { Path = context + "." + name, Count = count, Limit = limit };
        return member;
    }

    static string Text(JsonElement value, string name, string context)
    {
        JsonElement member = Required(value, name, context);
        string path = context + "." + name;
        if (member.ValueKind != JsonValueKind.String)
            throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.ExpectedString, "Expected a string.") { Path = path };
        try { return member.GetString()!; }
        catch (InvalidOperationException error)
        {
            throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.InvalidText, "Malformed UTF-16 JSON text.", error) { Path = path };
        }
    }

    static uint Identifier(JsonElement value, string name, string context, uint? commandId = null)
    {
        JsonElement member = Required(value, name, context);
        if (member.ValueKind != JsonValueKind.Number || !member.TryGetUInt32(out uint result) || result == 0)
            throw new EquipmentCoverageDataException(name == "id" ? EquipmentCoverageRefusal.InvalidCommandId : EquipmentCoverageRefusal.InvalidPartId,
                "Expected a positive uint identifier.")
            {
                Path = context + "." + name,
                CommandId = name == "id" && member.TryGetUInt32(out uint id) ? id : commandId,
                IdentifierValue = member.ValueKind == JsonValueKind.Number && member.TryGetUInt64(out ulong authoredId) ? authoredId : null,
                PartId = name == "partId" && member.TryGetUInt32(out uint part) ? part : null
            };
        return result;
    }

    static int Integer(JsonElement value, string name, string context)
    {
        JsonElement member = Required(value, name, context);
        if (member.ValueKind != JsonValueKind.Number || !member.TryGetInt32(out int result))
            throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.InvalidInteger, "Expected a 32-bit integer.") { Path = context + "." + name };
        return result;
    }

    static float Single(JsonElement value, string name, string context) => Finite(Required(value, name, context), name, context);

    static float Finite(JsonElement value, string name, string context)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float result) || !float.IsFinite(result))
            throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.NonFiniteNumber, "Expected a finite float.") { Path = context + "." + name };
        return result;
    }
}
