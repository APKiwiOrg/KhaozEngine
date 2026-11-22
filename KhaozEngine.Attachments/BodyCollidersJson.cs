using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Unicode;

namespace KhaozEngine.Attachments;

/// <summary>Strict body collider JSON format 1 with a canonical UTF-8 writer.</summary>
public static class BodyCollidersJson
{
    /// <summary>The only body collider format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>Read a body collider set, refusing with <see cref="BodyCollidersDataException"/>.</summary>
    public static BodyColliderSet Parse(ReadOnlyMemory<byte> utf8, string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (!Utf8.IsValid(utf8.Span))
            throw new BodyCollidersDataException(BodyCollidersRefusal.JsonMalformed, $"{sourceName}: invalid UTF-8.")
            { SourceName = sourceName };

        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8);
            JsonElement root = document.RootElement;
            StrictAttachmentJson.Members(root, "$", RefuseRoot, "formatVersion", "rig", "colliders");
            JsonElement version = StrictAttachmentJson.Required(root, "formatVersion", "$", RefuseRoot);
            int? declared = version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int number) ? number : null;
            if (declared != FormatVersion)
                throw new BodyCollidersDataException(BodyCollidersRefusal.FormatVersionUnsupported, "unsupported formatVersion.")
                { JsonPath = "$.formatVersion", FormatVersion = declared };

            string rig = StrictAttachmentJson.Text(StrictAttachmentJson.Required(root, "rig", "$", RefuseRoot), "$.rig", RefuseRoot);
            JsonElement array = StrictAttachmentJson.ArrayValue(
                StrictAttachmentJson.Required(root, "colliders", "$", RefuseRoot), "$.colliders", RefuseRoot);
            int count = array.GetArrayLength();
            BodyColliderSet.ValidateCount(count);
            var colliders = new ColliderDefinition[count];
            int index = 0;
            foreach (JsonElement item in array.EnumerateArray())
            {
                colliders[index] = ReadCollider(item, index);
                index++;
            }

            return new BodyColliderSet(rig, colliders);
        }
        catch (BodyCollidersDataException error)
        {
            throw ((IAttachmentDataException)error).WithSource(sourceName);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new BodyCollidersDataException(BodyCollidersRefusal.JsonMalformed, $"{sourceName}: {error.Message}", error)
            { SourceName = sourceName };
        }
    }

    /// <summary>Write the canonical UTF-8 bytes used by <see cref="BodyColliderSet.Sha256"/>.</summary>
    public static byte[] Write(BodyColliderSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("rig", set.RigIdentity);
            writer.WriteStartArray("colliders");
            foreach (ColliderDefinition collider in set.Colliders)
            {
                writer.WriteStartObject();
                writer.WriteString("joint", collider.Joint);
                WritePoint(writer, "start", collider.Start);
                WritePoint(writer, "end", collider.End);
                writer.WriteNumber("radius", collider.Radius);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    static ColliderDefinition ReadCollider(JsonElement value, int index)
    {
        string path = $"$.colliders[{index}]";
        StrictAttachmentJson.Refuse refuse = Refuse;
        StrictAttachmentJson.Members(value, path, refuse, "joint", "start", "end", "radius");
        string joint = ReadJoint(StrictAttachmentJson.Required(value, "joint", path, refuse), path + ".joint", index, refuse);
        Vector3 start = ReadPoint(StrictAttachmentJson.Required(value, "start", path, refuse), path + ".start", refuse);
        Vector3 end = ReadPoint(StrictAttachmentJson.Required(value, "end", path, refuse), path + ".end", refuse);
        float radius = ReadNumber(StrictAttachmentJson.Required(value, "radius", path, refuse), path + ".radius", refuse);
        return new ColliderDefinition(joint, start, end, radius);

        Exception Refuse(StrictAttachmentJson.Fault fault, string jsonPath, string message, string? property) =>
            new BodyCollidersDataException(Reason(fault), message) { JsonPath = jsonPath, PropertyName = property, ColliderIndex = index };
    }

    static string ReadJoint(JsonElement value, string path, int index, StrictAttachmentJson.Refuse refuse)
    {
        try
        {
            return StrictAttachmentJson.Text(value, path, refuse);
        }
        catch (InvalidOperationException error)
        {
            // The JSON reader cannot decode an unpaired escaped surrogate to a joint name.
            throw new BodyCollidersDataException(BodyCollidersRefusal.JointNameMalformedUtf16, $"{path}: a joint name must contain well-formed UTF-16.", error)
            { JsonPath = path, ColliderIndex = index };
        }
    }

    static Vector3 ReadPoint(JsonElement value, string path, StrictAttachmentJson.Refuse refuse)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            throw refuse(StrictAttachmentJson.Fault.ValueShape, path, $"{path}: expected three numbers.");

        return new Vector3(ReadNumber(value[0], path + "[0]", refuse),
            ReadNumber(value[1], path + "[1]", refuse), ReadNumber(value[2], path + "[2]", refuse));
    }

    static float ReadNumber(JsonElement value, string path, StrictAttachmentJson.Refuse refuse)
    {
        if (value.ValueKind != JsonValueKind.Number)
            throw refuse(StrictAttachmentJson.Fault.ValueShape, path, $"{path}: expected a number.");
        // A numeric token outside the float domain is refused by the shared collider validation.
        return value.TryGetSingle(out float number) ? number : float.NaN;
    }

    static void WritePoint(Utf8JsonWriter writer, string name, Vector3 point)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(point.X == 0 ? 0f : point.X);
        writer.WriteNumberValue(point.Y == 0 ? 0f : point.Y);
        writer.WriteNumberValue(point.Z == 0 ? 0f : point.Z);
        writer.WriteEndArray();
    }

    static Exception RefuseRoot(StrictAttachmentJson.Fault fault, string path, string message, string? property) =>
        new BodyCollidersDataException(Reason(fault), message) { JsonPath = path, PropertyName = property };

    static BodyCollidersRefusal Reason(StrictAttachmentJson.Fault fault) => fault switch
    {
        StrictAttachmentJson.Fault.UnknownProperty => BodyCollidersRefusal.JsonPropertyUnknown,
        StrictAttachmentJson.Fault.DuplicateProperty => BodyCollidersRefusal.JsonPropertyDuplicate,
        StrictAttachmentJson.Fault.MissingProperty => BodyCollidersRefusal.JsonPropertyMissing,
        StrictAttachmentJson.Fault.ValueShape => BodyCollidersRefusal.JsonValueShapeInvalid,
        _ => throw new ArgumentOutOfRangeException(nameof(fault)),
    };
}
