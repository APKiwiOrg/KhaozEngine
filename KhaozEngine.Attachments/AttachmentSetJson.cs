using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.Attachments;

public static class AttachmentSetJson
{
    public const int RigidOnlyFormatVersion = 1;
    public const int CurrentFormatVersion = 2;

    public static AttachmentSetDefinition Parse(ReadOnlyMemory<byte> utf8, string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8, new JsonDocumentOptions());
            JsonElement root = document.RootElement;
            Members(root, "root", "$", "formatVersion", "parts");
            JsonElement version = Required(root, "formatVersion", "root", "$");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number)
                || number is not (RigidOnlyFormatVersion or CurrentFormatVersion))
                throw new AttachmentSetDataException(AttachmentSetRefusal.FormatVersionUnsupported, "root: unsupported formatVersion.")
                {
                    JsonPath = "$.formatVersion",
                    FormatVersion = version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int declaredVersion) ? declaredVersion : null,
                };
            JsonElement array = Required(root, "parts", "root", "$");
            if (array.ValueKind != JsonValueKind.Array)
                throw new AttachmentSetDataException(AttachmentSetRefusal.PartsNotArray, "root: parts must be an array.") { JsonPath = "$.parts" };
            var parts = new List<AttachmentPartDefinition>();
            int index = 0;
            foreach (JsonElement item in array.EnumerateArray())
            {
                string jsonPath = $"$.parts[{index}]";
                string context = $"parts[{index++}]";
                bool rigid = number == RigidOnlyFormatVersion;
                if (rigid)
                    Members(item, context, jsonPath, "id", "mesh", "joint", "local");
                else
                {
                    Members(item, context, jsonPath, "id", "kind", "mesh", "joint", "local");
                    string kind = Text(item, "kind", context, jsonPath);
                    rigid = kind switch
                    {
                        "rigid" => true,
                        "skinned" => false,
                        _ => throw new AttachmentSetDataException(AttachmentSetRefusal.JsonPartKindUnsupported, $"{context}: unsupported attachment kind.")
                        { JsonPath = jsonPath + ".kind", Kind = kind },
                    };
                    if (!rigid) Members(item, context, jsonPath, "id", "kind", "mesh");
                }
                JsonElement idValue = Required(item, "id", context, jsonPath);
                if (idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetUInt32(out uint id))
                    throw new AttachmentSetDataException(AttachmentSetRefusal.JsonPartIdInvalid, $"{context}: id must be a positive uint.") { JsonPath = jsonPath + ".id" };
                context += $" part {id}";
                JsonElement mesh = Required(item, "mesh", context, jsonPath);
                Members(mesh, context + " mesh", jsonPath + ".mesh", "key", "sha256");
                string key = Text(mesh, "key", context + " mesh", jsonPath + ".mesh");
                string hash = Text(mesh, "sha256", context + " mesh", jsonPath + ".mesh");
                if (!rigid)
                {
                    parts.Add(new SkinnedAttachmentPartDefinition(id, new(key, hash)));
                    continue;
                }
                string joint = Text(item, "joint", context, jsonPath);
                JsonElement local = Required(item, "local", context, jsonPath);
                if (local.ValueKind != JsonValueKind.Array || local.GetArrayLength() != 16)
                    throw new AttachmentSetDataException(AttachmentSetRefusal.LocalTransformLengthMismatch, $"{context}: local must contain 16 row-major numbers.")
                    {
                        JsonPath = jsonPath + ".local",
                        ActualCount = local.ValueKind == JsonValueKind.Array ? local.GetArrayLength() : null,
                        ExpectedCount = 16,
                    };
                float[] values = new float[16];
                int component = 0;
                foreach (JsonElement value in local.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float parsed) || !float.IsFinite(parsed))
                        throw new AttachmentSetDataException(AttachmentSetRefusal.LocalTransformComponentInvalid, $"{context}: local[{component}] must be a finite float.")
                        { JsonPath = jsonPath + $".local[{component}]", ComponentIndex = component };
                    values[component++] = parsed;
                }
                var matrix = new Matrix4x4(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
                    values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
                parts.Add(new RigidAttachmentPartDefinition(id, new(key, hash), joint, matrix));
            }
            return new AttachmentSetDefinition(parts);
        }
        catch (MalformedDataException error)
        {
            if (error is IAttachmentDataException sourced) throw sourced.WithSource(sourceName);
            throw;
        }
        catch (AttachmentSetArgumentException error)
        {
            throw ((IAttachmentDataException)error).WithSource(sourceName);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new AttachmentSetDataException(
                error is JsonException ? AttachmentSetRefusal.JsonMalformed : AttachmentSetRefusal.JsonValueShapeInvalid,
                $"{sourceName}: {error.Message}", error) { SourceName = sourceName };
        }
    }

    public static byte[] Write(AttachmentSetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        bool rigidOnly = definition.Parts.All(part => part is RigidAttachmentPartDefinition);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", rigidOnly ? RigidOnlyFormatVersion : CurrentFormatVersion);
            writer.WriteStartArray("parts");
            foreach (AttachmentPartDefinition part in definition.Parts)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", part.Id);
                if (!rigidOnly)
                    writer.WriteString("kind", part is RigidAttachmentPartDefinition ? "rigid" : "skinned");
                writer.WriteStartObject("mesh");
                writer.WriteString("key", part.Mesh.Key);
                writer.WriteString("sha256", part.Mesh.Sha256);
                writer.WriteEndObject();
                if (part is RigidAttachmentPartDefinition rigid)
                {
                    writer.WriteString("joint", rigid.Joint);
                    writer.WriteStartArray("local");
                    foreach (float value in AttachmentSetDefinition.Values(rigid.Local))
                        writer.WriteNumberValue(value == 0 ? 0f : value);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    static void Members(JsonElement value, string context, string jsonPath, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new AttachmentSetDataException(AttachmentSetRefusal.JsonObjectRequired, $"{context}: expected an object.") { JsonPath = jsonPath };
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty member in value.EnumerateObject())
        {
            if (!found.Add(member.Name))
                throw new AttachmentSetDataException(AttachmentSetRefusal.JsonPropertyDuplicate, $"{context}: duplicate property '{member.Name}'.")
                { JsonPath = jsonPath, PropertyName = member.Name };
            if (Array.IndexOf(allowed, member.Name) < 0)
                throw new AttachmentSetDataException(AttachmentSetRefusal.JsonPropertyUnknown, $"{context}: unknown property '{member.Name}'.")
                { JsonPath = jsonPath, PropertyName = member.Name };
        }
    }

    static JsonElement Required(JsonElement value, string name, string context, string jsonPath) => value.TryGetProperty(name, out JsonElement result)
        ? result : throw new AttachmentSetDataException(AttachmentSetRefusal.JsonPropertyMissing, $"{context}: missing '{name}'.")
        { JsonPath = jsonPath, PropertyName = name };

    static string Text(JsonElement value, string name, string context, string jsonPath)
    {
        JsonElement member = Required(value, name, context, jsonPath);
        if (member.ValueKind != JsonValueKind.String)
            throw new AttachmentSetDataException(AttachmentSetRefusal.JsonStringRequired, $"{context}: '{name}' must be a string.")
            { JsonPath = AttachmentJsonPath.Property(jsonPath, name), PropertyName = name };
        return member.GetString()!;
    }
}
