using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace KhaozEngine.MapDoc;

/// <summary>Checks closed members before DTO deserialization, and required members within native blocks.
/// Legacy defaults and open registry-owned features remain supported. Not a general JSON Schema engine.</summary>
internal static class MapDocumentMembers
{
    static readonly JsonObject Schema = JsonNode.Parse(MapDocumentSchema.GetJson())!.AsObject();

    static readonly JsonObject Manifest = JsonNode.Parse(MapDocumentSchema.GetManifestJson())!.AsObject();
    static readonly JsonObject Tile = JsonNode.Parse(MapDocumentSchema.GetTileJson())!.AsObject();

    internal static void ValidateManifest(JsonObject root, string where) => Check(root, Manifest, where, native: false);
    internal static void ValidateTile(JsonObject root, string where) => Check(root, Tile, where, native: false);

    internal static void Validate(JsonObject root, string where) => Check(root, Schema, where, native: false);

    // Match the serializer's case-insensitive, last-value-wins property lookup without rewriting input.
    internal static bool TryGetProperty(JsonObject obj, string name, out JsonNode? value)
    {
        bool found = false;
        value = null;
        foreach (KeyValuePair<string, JsonNode?> member in obj)
        {
            if (!string.Equals(member.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            found = true;
            value = member.Value;
        }
        return found;
    }

    static void Check(JsonNode? node, JsonObject schema, string path, bool native)
    {
        schema = Resolve(schema);
        if (node is null)
        {
            if (native && !AllowsNull(schema))
                throw new MapDocumentException($"{path}: native member must not be null.");
            return;
        }
        CheckUnion(node, schema["oneOf"] as JsonArray, path, native);
        CheckUnion(node, schema["anyOf"] as JsonArray, path, native);
        if (node is JsonArray array && schema["items"] is JsonObject itemSchema)
        {
            for (int i = 0; i < array.Count; i++) Check(array[i], itemSchema, $"{path}[{i}]", native);
        }
        if (node is not JsonObject obj || schema["properties"] is not JsonObject properties) return;
        if (native && schema["required"] is JsonArray required)
        {
            foreach (JsonNode? member in required)
            {
                string name = member!.GetValue<string>();
                if (!TryGetProperty(obj, name, out _))
                    throw new MapDocumentException($"{path}: required native member '{name}' is missing.");
            }
        }
        bool closed = schema["additionalProperties"] is JsonValue closedValue &&
            closedValue.TryGetValue(out bool additional) && !additional;
        foreach (KeyValuePair<string, JsonNode?> member in obj)
        {
            if (TryGetProperty(properties, member.Key, out JsonNode? declared) && declared is JsonObject memberSchema)
            {
                bool nativeMember = native || ((ReferenceEquals(schema, Schema) || ReferenceEquals(schema, Manifest)) && IsNativeBlock(member.Key)) ||
                    string.Equals(member.Key, "supportBinding", StringComparison.OrdinalIgnoreCase);
                Check(member.Value, memberSchema, path + "." + member.Key, nativeMember);
            }
            else if (closed)
                throw new MapDocumentException($"{path}: unknown property '{member.Key}'.");
        }
    }

    static void CheckUnion(JsonNode node, JsonArray? alternatives, string path, bool native)
    {
        if (alternatives is null) return;
        foreach (JsonNode? alternative in alternatives)
        {
            JsonObject candidate = Resolve(alternative!.AsObject());
            if (candidate["type"] is JsonValue type)
            {
                string name = type.GetValue<string>();
                if (name == "null" || (name == "array" && node is not JsonArray) ||
                    (name == "object" && node is not JsonObject)) continue;
            }
            if (candidate["properties"] is JsonObject fields)
            {
                if (node is not JsonObject obj) continue;
                JsonNode? expected = fields["type"]?["const"];
                if (expected is not null &&
                    (!TryGetProperty(obj, "type", out JsonNode? actual) || !JsonNode.DeepEquals(expected, actual))) continue;
            }
            Check(node, candidate, path, native);
            return;
        }
    }

    static JsonObject Resolve(JsonObject schema)
    {
        if (schema["$ref"] is not JsonValue reference) return schema;
        string name = reference.GetValue<string>()["#/$defs/".Length..];
        return Schema["$defs"]![name]!.AsObject();
    }

    static bool AllowsNull(JsonObject schema)
    {
        if (schema["type"] is JsonArray types)
            foreach (JsonNode? type in types)
                if (type!.GetValue<string>() == "null") return true;
        return false;
    }

    static bool IsNativeBlock(string name) =>
        string.Equals(name, "playableBounds", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "nativeAssets", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "resolverIdentity", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "supportRecipe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "surfaces", StringComparison.OrdinalIgnoreCase);
}
