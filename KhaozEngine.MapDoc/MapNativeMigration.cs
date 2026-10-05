using System;
using System.Text.Json.Nodes;

namespace KhaozEngine.MapDoc;

/// <summary>The pure format-3 to format-4 transition. Analytic content and stable IDs are unchanged.</summary>
public static class MapNativeMigration
{
    /// <summary>Returns an independent format-4 document. Repeating the upgrade is a no-op on content.</summary>
    public static JsonObject Upgrade(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source["formatVersion"] is not JsonValue value ||
            !value.TryGetValue(out int version) || (version != 3 && version != 4))
            throw new MapDocumentException("Native migration requires formatVersion 3 or 4.");

        var result = (JsonObject)source.DeepClone();
        if (version == 4) return result;
        result["formatVersion"] = 4;
        if (!MapDocumentMembers.TryGetProperty(result, "playableBounds", out _))
        {
            MapDocumentMembers.TryGetProperty(result, "bounds", out JsonNode? bounds);
            JsonNode? playable = bounds?.DeepClone();
            // Legacy storage bounds deserialize absent coordinates as zero. Materialize those defaults
            // only in the new block, whose required coordinates must now be explicit.
            if (playable is JsonObject extent)
                foreach (string coordinate in new[] { "minX", "minZ", "maxX", "maxZ" })
                    if (!MapDocumentMembers.TryGetProperty(extent, coordinate, out _)) extent[coordinate] = 0f;
            result["playableBounds"] = playable;
        }
        return result;
    }
}
