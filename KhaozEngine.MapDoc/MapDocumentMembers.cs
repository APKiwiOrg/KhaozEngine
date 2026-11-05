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

    internal static void ValidateManifest(JsonObject root, string where) => Check(root, Manifest, "$", "$", native: false, where);
    internal static void ValidateTile(JsonObject root, string where) => Check(root, Tile, "$", "$", native: false, where);

    internal static void Validate(JsonObject root, string where) => Check(root, Schema, "$", "$", native: false, where);

    /// <summary>A parsed <see cref="JsonObject"/> builds its case-insensitive member dictionary lazily and throws
    /// <see cref="ArgumentException"/> on a repeated name. Building every dictionary right after parsing makes a
    /// duplicate member refuse as a document error before any reader touches it.</summary>
    internal static void RequireUniqueMembers(JsonNode? node, string where)
        => BuildMembers(node, where, "$", "$");

    static void BuildMembers(JsonNode? node, string where, string path, string fieldPath)
    {
        try
        {
            if (node is JsonObject obj)
                foreach (KeyValuePair<string, JsonNode?> member in obj)
                    BuildMembers(member.Value, where, path + "." + member.Key, MapDocumentJsonPath.AppendProperty(fieldPath, member.Key));
            else if (node is JsonArray array)
                for (int i = 0; i < array.Count; i++) BuildMembers(array[i], where, $"{path}[{i}]", $"{fieldPath}[{i}]");
        }
        catch (ArgumentException ex)
        {
            // The lazy dictionary exposes the containing object, not a typed duplicate member name.
            throw new MapDocumentException(MapDocumentRefusal.SchemaMemberDuplicate, $"{where}: duplicate property. {ex.Message}", ex)
            { SourceName = where, FieldPath = fieldPath };
        }
    }

    // Match the serializer's case-insensitive lookup without rewriting input. Parsed documents have unique names.
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

    // Preserve the released display path in prose while FieldPath follows the JSON reader's segment format.
    static void Check(JsonNode? node, JsonObject schema, string path, string fieldPath, bool native, string where)
    {
        // Foliage has a closed codec for each storage form. It charges all counts before visiting nested records,
        // so let that codec validate this native block once rather than walking its untrusted arrays first.
        if (schema["$ref"]?.GetValue<string>() is "#/$defs/foliageInline" or "#/$defs/foliageStored") return;
        schema = Resolve(schema);
        if (node is null)
        {
            if (native && !AllowsNull(schema))
                throw new MapDocumentException(MapDocumentRefusal.SchemaMemberNull, $"{where}{path[1..]}: native member must not be null.")
                { SourceName = where, FieldPath = fieldPath };
            return;
        }
        CheckUnion(node, schema["oneOf"] as JsonArray, path, fieldPath, native, where);
        CheckUnion(node, schema["anyOf"] as JsonArray, path, fieldPath, native, where);
        if (node is JsonArray array && schema["items"] is JsonObject itemSchema)
        {
            for (int i = 0; i < array.Count; i++) Check(array[i], itemSchema, $"{path}[{i}]", $"{fieldPath}[{i}]", native, where);
        }
        if (node is not JsonObject obj || schema["properties"] is not JsonObject properties) return;
        if (native && schema["required"] is JsonArray required)
        {
            foreach (JsonNode? member in required)
            {
                string name = member!.GetValue<string>();
                if (!TryGetProperty(obj, name, out _))
                    throw new MapDocumentException(MapDocumentRefusal.SchemaMemberMissing, $"{where}{path[1..]}: required native member '{name}' is missing.")
                    { SourceName = where, FieldPath = MapDocumentJsonPath.AppendProperty(fieldPath, name) };
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
                Check(member.Value, memberSchema, path + "." + member.Key, MapDocumentJsonPath.AppendProperty(fieldPath, member.Key), nativeMember, where);
            }
            else if (closed)
                throw new MapDocumentException(MapDocumentRefusal.SchemaMemberUnknown, $"{where}{path[1..]}: unknown property '{member.Key}'.")
                { SourceName = where, FieldPath = MapDocumentJsonPath.AppendProperty(fieldPath, member.Key) };
        }
    }

    // The first admitted alternative is checked, as released. When that alternative is an object shape, a later
    // admitted shape whose every const member matches is preferred, so payload and kind discriminants select their
    // own closed shape. A node no shape fully matches keeps the released first choice.
    static void CheckUnion(JsonNode node, JsonArray? alternatives, string path, string fieldPath, bool native, string where)
    {
        if (alternatives is null) return;
        JsonObject? first = null;
        JsonObject? matched = null;
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
                first ??= candidate;
                // A type-discriminated union admits exactly one alternative, so only an untyped shape compares consts.
                if (expected is null && ConstantsMatch(obj, fields)) { matched = candidate; break; }
            }
            else
            {
                first ??= candidate;
                break;
            }
        }
        if (first is null) return;
        Check(node, first["properties"] is JsonObject ? matched ?? first : first, path, fieldPath, native, where);
    }

    static bool ConstantsMatch(JsonObject obj, JsonObject fields)
    {
        foreach (KeyValuePair<string, JsonNode?> field in fields)
        {
            if (field.Value is not JsonObject declared || declared["const"] is not { } expected) continue;
            if (!TryGetProperty(obj, field.Key, out JsonNode? actual) || !JsonNode.DeepEquals(expected, actual)) return false;
        }
        return true;
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
        string.Equals(name, "surfaces", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "surfacePatches", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "surfaceStorage", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "water", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "foliage", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "prefabs", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "buildingIntent", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "nativeResourceTransforms", StringComparison.OrdinalIgnoreCase);
}
