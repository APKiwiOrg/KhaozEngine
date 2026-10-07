using System;
using System.Text.Json.Nodes;

namespace KhaozEngine.MapDoc;

/// <summary>The pure format-4 to format-5 transition, preserving resolver-v1 execution.</summary>
public static class MapSurfaceMigration
{
    public static JsonObject Upgrade(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source["formatVersion"] is not JsonValue value ||
            !value.TryGetValue(out int version) || (version != 4 && version != 5))
            throw new MapDocumentException("Surface migration requires formatVersion 4 or 5.");

        var result = (JsonObject)source.DeepClone();
        if (version == 5) return result;
        result["formatVersion"] = 5;
        result["supportRecipe"] = nameof(MapSupportRecipe.LegacyXzCallbackV1);
        return result;
    }
}
