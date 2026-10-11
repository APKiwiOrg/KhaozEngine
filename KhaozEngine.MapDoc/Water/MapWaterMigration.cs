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
        if (source["formatVersion"] is not JsonValue value ||
            !value.TryGetValue(out int version) || (version != 5 && version != 6))
            throw new MapDocumentException("Water migration requires formatVersion 5 or 6.");

        var result = (JsonObject)source.DeepClone();
        if (version == 6) return result;
        result["formatVersion"] = 6;
        return result;
    }
}
