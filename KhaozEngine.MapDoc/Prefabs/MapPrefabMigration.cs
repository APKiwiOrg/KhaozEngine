using System;
using System.Text.Json.Nodes;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>The pure format 6 to 7 advance. No execution or authored data changes.</summary>
public static class MapPrefabMigration
{
    public static JsonObject Upgrade(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source["formatVersion"] is not JsonValue value || !value.TryGetValue(out int version) || version is not (6 or 7))
            throw new MapDocumentException(!source.ContainsKey("formatVersion") ? MapDocumentRefusal.SchemaMemberMissing
                : source["formatVersion"] is not JsonValue submitted || !submitted.TryGetValue<int>(out _)
                    ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.DocumentMigrationSourceVersionInvalid,
                "Prefab migration requires formatVersion 6 or 7.")
            {
                FieldPath = "$.formatVersion", ActualVersion = source["formatVersion"] is JsonValue v && v.TryGetValue<int>(out int actual) ? actual : null,
                SupportedVersions = [6, 7],
            };
        var result = (JsonObject)source.DeepClone();
        result["formatVersion"] = 7;
        return result;
    }
}
