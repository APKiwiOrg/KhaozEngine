using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace KhaozEngine.MapDoc;

/// <summary>Rejects unknown members using the published schema before DTO deserialization.
/// Open registry-owned feature payloads remain open. This is a member check, not a JSON Schema engine.</summary>
internal static class MapDocumentMembers
{
    static readonly JsonObject Schema = JsonNode.Parse(MapDocumentSchema.GetJson())!.AsObject();

    internal static void Validate(JsonObject root, string where) => Check(root, Schema, where);

    static void Check(JsonNode? node, JsonObject schema, string path)
    {
        if (node is null) return;
        if (schema["$ref"] is JsonValue reference)
        {
            string name = reference.GetValue<string>()["#/$defs/".Length..];
            Check(node, Schema["$defs"]![name]!.AsObject(), path);
            return;
        }
        if (node is JsonObject && schema["oneOf"] is JsonArray alternatives)
        {
            foreach (JsonNode? alternative in alternatives)
            {
                var candidate = alternative!.AsObject();
                if (candidate["properties"] is not JsonObject fields) continue;
                JsonNode? discriminator = fields["type"]?["const"];
                if (discriminator is not null && !JsonNode.DeepEquals(discriminator, node["type"])) continue;
                Check(node, candidate, path);
            }
        }
        if (node is JsonArray array && schema["items"] is JsonObject itemSchema)
        {
            for (int i = 0; i < array.Count; i++) Check(array[i], itemSchema, $"{path}[{i}]");
        }
        if (node is not JsonObject obj || schema["properties"] is not JsonObject properties) return;
        bool closed = schema["additionalProperties"] is JsonValue closedValue &&
            closedValue.TryGetValue(out bool additional) && !additional;
        foreach (KeyValuePair<string, JsonNode?> member in obj)
        {
            if (properties[member.Key] is JsonObject memberSchema)
                Check(member.Value, memberSchema, path + "." + member.Key);
            else if (closed)
                throw new MapDocumentException($"{path}: unknown property '{member.Key}'.");
        }
    }
}
