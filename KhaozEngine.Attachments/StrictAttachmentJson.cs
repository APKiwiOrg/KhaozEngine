using System;
using System.Collections.Generic;
using System.Text.Json;

namespace KhaozEngine.Attachments;

internal static class StrictAttachmentJson
{
    internal enum Fault { UnknownProperty, DuplicateProperty, MissingProperty, ValueShape }

    internal delegate Exception Refuse(Fault fault, string path, string message, string? property = null);

    internal static void Members(JsonElement value, string path, Refuse refuse, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw refuse(Fault.ValueShape, path, $"{path}: expected an object.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty member in value.EnumerateObject())
        {
            string name = member.Name;
            if (!found.Add(name))
                throw refuse(Fault.DuplicateProperty, path, $"{path}: duplicate property '{name}'.", name);
            if (Array.IndexOf(allowed, name) < 0)
                throw refuse(Fault.UnknownProperty, path, $"{path}: unknown property '{name}'.", name);
        }
    }

    internal static JsonElement Required(JsonElement value, string name, string path, Refuse refuse) =>
        value.TryGetProperty(name, out JsonElement result) ? result
            : throw refuse(Fault.MissingProperty, path, $"{path}: missing '{name}'.", name);

    internal static string Text(JsonElement value, string path, Refuse refuse) => value.ValueKind == JsonValueKind.String
        ? value.GetString()!
        : throw refuse(Fault.ValueShape, path, $"{path}: expected a string.");

    internal static JsonElement ArrayValue(JsonElement value, string path, Refuse refuse) => value.ValueKind == JsonValueKind.Array
        ? value
        : throw refuse(Fault.ValueShape, path, $"{path}: expected an array.");
}
