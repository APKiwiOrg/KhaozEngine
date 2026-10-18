using System;
using System.Text.Json.Nodes;

namespace KhaozEngine.MapDoc.Markers;

/// <summary>The pure format 7 to 8 advance. Format 8 admits marker payload 2. No source, terrain, closure, registry
/// or role is read, no height is sampled and no legacy spawn list is converted.</summary>
public static class MapMarkerMigration
{
    public static JsonObject Upgrade(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source["formatVersion"] is not JsonValue value || !value.TryGetValue(out int version) || version is not (7 or 8))
            throw new MapDocumentException("Marker migration requires formatVersion 7 or 8.");
        var result = (JsonObject)source.DeepClone();
        result["formatVersion"] = 8;
        return result;
    }
}
