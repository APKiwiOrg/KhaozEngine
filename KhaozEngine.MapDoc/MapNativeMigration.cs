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
        if (!result.ContainsKey("playableBounds"))
            result["playableBounds"] = result["bounds"]?.DeepClone();
        return result;
    }
}
