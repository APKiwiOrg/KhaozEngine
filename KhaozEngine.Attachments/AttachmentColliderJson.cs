using System;
using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.Attachments;

/// <summary>The collider object shared by the body and worn set formats.</summary>
internal static class AttachmentColliderJson
{
    internal static ColliderDefinition Read(JsonElement value, string path, StrictAttachmentJson.Refuse refuse,
        Func<InvalidOperationException, Exception> badJoint)
    {
        StrictAttachmentJson.Members(value, path, refuse, "joint", "start", "end", "radius");
        JsonElement jointValue = StrictAttachmentJson.Required(value, "joint", path, refuse);
        string joint;
        try
        {
            joint = StrictAttachmentJson.Text(jointValue, path + ".joint", refuse);
        }
        catch (InvalidOperationException error)
        {
            // An unpaired escaped surrogate cannot be decoded into a joint name.
            throw badJoint(error);
        }
        Vector3 start = ReadPoint(StrictAttachmentJson.Required(value, "start", path, refuse), path + ".start", refuse);
        Vector3 end = ReadPoint(StrictAttachmentJson.Required(value, "end", path, refuse), path + ".end", refuse);
        float radius = ReadNumber(StrictAttachmentJson.Required(value, "radius", path, refuse), path + ".radius", refuse);
        return new ColliderDefinition(joint, start, end, radius);
    }

    internal static Vector3 ReadPoint(JsonElement value, string path, StrictAttachmentJson.Refuse refuse)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            throw refuse(StrictAttachmentJson.Fault.ValueShape, path, $"{path}: expected three numbers.");

        return new Vector3(ReadNumber(value[0], path + "[0]", refuse),
            ReadNumber(value[1], path + "[1]", refuse), ReadNumber(value[2], path + "[2]", refuse));
    }

    internal static float ReadNumber(JsonElement value, string path, StrictAttachmentJson.Refuse refuse)
    {
        if (value.ValueKind != JsonValueKind.Number)
            throw refuse(StrictAttachmentJson.Fault.ValueShape, path, $"{path}: expected a number.");
        // Domain validation locates numeric tokens that overflow float.
        return value.TryGetSingle(out float number) ? number : float.NaN;
    }

    internal static void Write(Utf8JsonWriter writer, ColliderDefinition collider)
    {
        writer.WriteStartObject();
        writer.WriteString("joint", collider.Joint);
        WritePoint(writer, "start", collider.Start);
        WritePoint(writer, "end", collider.End);
        writer.WriteNumber("radius", collider.Radius);
        writer.WriteEndObject();
    }

    internal static void WritePoint(Utf8JsonWriter writer, string name, Vector3 point)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(point.X == 0 ? 0f : point.X);
        writer.WriteNumberValue(point.Y == 0 ? 0f : point.Y);
        writer.WriteNumberValue(point.Z == 0 ? 0f : point.Z);
        writer.WriteEndArray();
    }
}
