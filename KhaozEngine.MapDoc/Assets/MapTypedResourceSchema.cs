using System;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Assets;

/// <summary>The in-band discriminant that opts an R5 Light or Support payload into typed decoding.</summary>
/// <remarks>A typed payload is a JSON object whose first member is <c>resourceSchema</c> with the role's exact value.
/// Writers emit it first and the closed decoders require it there. Closure construction decodes only bytes that carry
/// it. Every other byte sequence, JSON or not, stays an opaque released resource.</remarks>
internal static class MapTypedResourceSchema
{
    internal const string Property = "resourceSchema";
    internal const string Light = "kemap/light-resource";
    internal const string Support = "kemap/support-resource";

    /// <summary>True when the bytes open a root object whose first member is the discriminant. This reads two tokens
    /// only. A malformed typed payload is left to its closed decoder, which refuses it.</summary>
    internal static bool Carries(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: false, default);
            return reader.Read() && reader.TokenType == JsonTokenType.StartObject && reader.Read() &&
                reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals(Property);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Refuses a root that does not open with exactly <paramref name="schema"/>.</summary>
    internal static void Require(JsonElement root, string schema, string payload, string resourceId)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            using var members = root.EnumerateObject();
            if (members.MoveNext() && members.Current.NameEquals(Property) &&
                members.Current.Value.ValueKind == JsonValueKind.String && members.Current.Value.ValueEquals(schema)) return;
        }
        throw new MapDocumentException($"{payload} '{resourceId}' must begin with {Property} '{schema}'.");
    }

    /// <summary>The payload members after the leading discriminant, for a closed member decoder.</summary>
    internal static byte[] Body(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            bool first = true;
            foreach (JsonProperty member in root.EnumerateObject())
            {
                if (first) { first = false; continue; }
                member.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Prefixes a serialized object with the discriminant as its first member.</summary>
    internal static byte[] Tag(ReadOnlySpan<byte> body, string schema)
    {
        var reader = new Utf8JsonReader(body);
        using JsonDocument json = JsonDocument.ParseValue(ref reader);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(Property, schema);
            foreach (JsonProperty member in json.RootElement.EnumerateObject()) member.WriteTo(writer);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>The refusal for a typed consumer that finds an opaque resource.</summary>
    internal static MapDocumentException Opaque(string role, string resourceId, string schema) =>
        new($"Native {role} '{resourceId}' is not captured as a typed payload. Its resource is opaque, and a typed {role} " +
            $"consumer requires a payload that begins with {Property} '{schema}'.");
}
