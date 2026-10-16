using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.Attachments;

/// <summary>Strict object, required-member and text validation shared by attachment JSON codecs.</summary>
internal static class AttachmentJson
{
    internal static void Members(JsonElement value, string context, params string[] allowed)
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

    internal static JsonElement Required(JsonElement value, string name, string context) => value.TryGetProperty(name, out JsonElement result)
        ? result : throw new InvalidDataException($"{context}: missing '{name}'.");

    internal static string Text(JsonElement value, string name, string context)
    {
        JsonElement member = Required(value, name, context);
        if (member.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{context}: '{name}' must be a string.");
        return member.GetString()!;
    }
}
