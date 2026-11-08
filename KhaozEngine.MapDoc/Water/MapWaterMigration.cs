using System;
using System.Text.Json.Nodes;

namespace KhaozEngine.MapDoc.Water;

/// <summary>The pure format-5 to format-6 transition. It adds no member, so a migrated document has no water
/// section and keeps its legacy global water.</summary>
public static class MapWaterMigration
{
    public static JsonObject Upgrade(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        JsonNode? node = source["formatVersion"];
        int version = 0;
        bool integer = node is JsonValue value && value.TryGetValue(out version);
        int? actual = integer ? version : null;
        if (!integer || (actual != 5 && actual != 6))
            throw new MapDocumentException(integer ? MapDocumentRefusal.SchemaPayloadVersionUnsupported
                : MapDocumentRefusal.SchemaValueRequired, "Water migration requires formatVersion 5 or 6.")
            { FieldPath = "formatVersion", ActualVersion = actual, SupportedVersions = integer ? [5, 6] : null };

        var result = (JsonObject)source.DeepClone();
        if (actual == 6) return result;
        result["formatVersion"] = 6;
        return result;
    }
}
