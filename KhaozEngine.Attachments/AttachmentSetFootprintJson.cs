using System;
using System.Text.Json;

namespace KhaozEngine.Attachments;

internal static class AttachmentSetFootprintJson
{
    const string Member = "footprint";

    internal static LiftFootprintRef? Read(JsonElement root)
    {
        if (!root.TryGetProperty(Member, out JsonElement value)) return null;
        AttachmentSetJson.Members(value, Member, AttachmentSetFootprint.Path, "key", "sha256");
        string key = ReadText(value, "key", AttachmentSetRefusal.FootprintKeyInvalid);
        string hash = ReadText(value, "sha256", AttachmentSetRefusal.FootprintHashInvalid, key);
        return new LiftFootprintRef(key, hash);
    }

    static string ReadText(JsonElement value, string name, AttachmentSetRefusal malformedReason, string? footprintKey = null)
    {
        try
        {
            return AttachmentSetJson.Text(value, name, Member, AttachmentSetFootprint.Path);
        }
        catch (InvalidOperationException error)
        {
            // An unpaired escaped surrogate has no decoded value to include in the refusal.
            string path = AttachmentSetFootprint.Path + "." + name;
            throw new AttachmentSetDataException(malformedReason, $"{path}: expected well-formed UTF-16.", error)
            { JsonPath = path, FootprintKey = footprintKey };
        }
    }

    internal static void Write(Utf8JsonWriter writer, LiftFootprintRef? footprint)
    {
        if (footprint is not { } reference) return;
        writer.WriteStartObject(Member);
        writer.WriteString("key", reference.Key);
        writer.WriteString("sha256", reference.Sha256);
        writer.WriteEndObject();
    }
}
