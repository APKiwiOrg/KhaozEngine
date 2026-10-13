using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.Attachments;

public static class RigidAttachmentSetJson
{
    public const int CurrentFormatVersion = 1;

    public static RigidAttachmentSetDefinition Parse(ReadOnlyMemory<byte> utf8, string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8, new JsonDocumentOptions());
            JsonElement root = document.RootElement;
            Members(root, "root", "formatVersion", "parts");
            JsonElement version = Required(root, "formatVersion", "root");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != CurrentFormatVersion)
                throw new InvalidDataException("root: unsupported formatVersion.");
            JsonElement array = Required(root, "parts", "root");
            if (array.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("root: parts must be an array.");
            var parts = new List<RigidAttachmentPartDefinition>();
            int index = 0;
            foreach (JsonElement item in array.EnumerateArray())
            {
                string context = $"parts[{index++}]";
                Members(item, context, "id", "mesh", "joint", "local");
                JsonElement idValue = Required(item, "id", context);
                if (idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetUInt32(out uint id))
                    throw new InvalidDataException($"{context}: id must be a positive uint.");
                context += $" part {id}";
                JsonElement mesh = Required(item, "mesh", context);
                Members(mesh, context + " mesh", "key", "sha256");
                string key = Text(mesh, "key", context + " mesh");
                string hash = Text(mesh, "sha256", context + " mesh");
                string joint = Text(item, "joint", context);
                JsonElement local = Required(item, "local", context);
                if (local.ValueKind != JsonValueKind.Array || local.GetArrayLength() != 16)
                    throw new InvalidDataException($"{context}: local must contain 16 row-major numbers.");
                float[] values = new float[16];
                int component = 0;
                foreach (JsonElement value in local.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float parsed) || !float.IsFinite(parsed))
                        throw new InvalidDataException($"{context}: local[{component}] must be a finite float.");
                    values[component++] = parsed;
                }
                var matrix = new Matrix4x4(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
                    values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
                parts.Add(new(id, new(key, hash), joint, matrix));
            }
            return new RigidAttachmentSetDefinition(parts);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException($"{sourceName}: {error.Message}", error);
        }
    }

    public static byte[] Write(RigidAttachmentSetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", CurrentFormatVersion);
            writer.WriteStartArray("parts");
            foreach (RigidAttachmentPartDefinition part in definition.Parts)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", part.Id);
                writer.WriteStartObject("mesh");
                writer.WriteString("key", part.Mesh.Key);
                writer.WriteString("sha256", part.Mesh.Sha256);
                writer.WriteEndObject();
                writer.WriteString("joint", part.Joint);
                writer.WriteStartArray("local");
                foreach (float value in RigidAttachmentSetDefinition.Values(part.Local))
                    writer.WriteNumberValue(value == 0 ? 0f : value);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    static void Members(JsonElement value, string context, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{context}: expected an object.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty member in value.EnumerateObject())
        {
            if (!found.Add(member.Name))
                throw new InvalidDataException($"{context}: duplicate property '{member.Name}'.");
            if (Array.IndexOf(allowed, member.Name) < 0)
                throw new InvalidDataException($"{context}: unknown property '{member.Name}'.");
        }
    }

    static JsonElement Required(JsonElement value, string name, string context) => value.TryGetProperty(name, out JsonElement result)
        ? result : throw new InvalidDataException($"{context}: missing '{name}'.");

    static string Text(JsonElement value, string name, string context)
    {
        JsonElement member = Required(value, name, context);
        if (member.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{context}: '{name}' must be a string.");
        return member.GetString()!;
    }
}
