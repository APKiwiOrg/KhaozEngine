using System.Text.Json.Nodes;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Mutate one wire metric without changing other samples that share its schema.
internal static class RendererPerfRawTestJson
{
    internal static JsonNode Pass(JsonObject report, JsonNode expanded)
    {
        var schema = new JsonObject();
        foreach (string field in new[] { "id", "occurrence", "parentId", "parentOccurrence", "beginMarker", "endMarker", "deviceOnly" })
            schema[field] = expanded[field]?.DeepClone();
        var duration = Metric(report, expanded["duration"]!);
        return new JsonArray(Intern((JsonArray)report["raw"]!["passes"]!, schema),
            Optional(expanded["elapsedTicks"]), duration["schema"]!.DeepClone(), Optional(duration["value"]));
    }
    static JsonArray Optional(JsonNode? value) => value is null ? new() : new(value.DeepClone());
    internal static void Unavailable(JsonObject report, JsonNode metric, string reason)
    {
        var schemas = (JsonArray)report["raw"]!["metrics"]!;
        var schema = schemas[metric["schema"]!.GetValue<int>()]!.DeepClone();
        schema["status"] = "invalid";
        schema["reason"] = reason;
        metric["value"] = null;
        metric["schema"] = Intern(schemas, schema);
    }
    internal static void Set(JsonObject report, JsonNode metric, string field, JsonNode? value)
    {
        var schemas = (JsonArray)report["raw"]!["metrics"]!;
        var schema = schemas[metric["schema"]!.GetValue<int>()]!.DeepClone();
        schema[field] = value;
        metric["schema"] = Intern(schemas, schema);
    }

    internal static JsonObject Metric(JsonObject report, JsonNode expanded)
    {
        var schema = new JsonObject
        {
            ["name"] = null,
            ["unit"] = expanded["unit"]!.DeepClone(),
            ["source"] = expanded["source"]!.DeepClone(),
            ["status"] = expanded["status"]!.DeepClone(),
            ["reason"] = expanded["reason"]?.DeepClone()
        };
        return new JsonObject
        {
            ["schema"] = Intern((JsonArray)report["raw"]!["metrics"]!, schema),
            ["value"] = expanded["value"]?.DeepClone()
        };
    }
    static int Intern(JsonArray schemas, JsonNode schema)
    {
        for (int i = 0; i < schemas.Count; i++)
            if (JsonNode.DeepEquals(schemas[i], schema)) return i;
        schemas.Add(schema);
        return schemas.Count - 1;
    }
}
