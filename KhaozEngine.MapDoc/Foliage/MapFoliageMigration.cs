using System;
using System.Text.Json.Nodes;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>The pure format 8 to 9 advance. Format 9 admits root foliage payload 1. No member is added, so absent foliage
/// stays absent, and no source, terrain, surface, closure or analytic layer is read or sampled.</summary>
public static class MapFoliageMigration
{
    public static JsonObject Upgrade(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source["formatVersion"] is not JsonValue value || !value.TryGetValue(out int version) || version is not (8 or 9))
            throw new MapDocumentException("Foliage migration requires formatVersion 8 or 9.");
        var result = (JsonObject)source.DeepClone();
        result["formatVersion"] = 9;
        return result;
    }
}
