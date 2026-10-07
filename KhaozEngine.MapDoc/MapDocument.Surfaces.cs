using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc;

public sealed partial class MapDocument
{
    public MapSupportRecipe SupportRecipe { get; set; } = MapSupportRecipe.LegacyXzCallbackV1;

    /// <summary>Surface metadata and resident payloads. Persistence serializes metadata separately.</summary>
    [JsonIgnore]
    public MapSurfaceSet Surfaces { get; set; } = new();

    // Keep the resident container out of JSON while sharing the metadata shape with the manifest writer.
    [JsonInclude]
    [JsonPropertyName("surfaces")]
    internal List<MapSurfaceRef> SurfaceRefs
    {
        get
        {
            Surfaces.RequireWritable();
            return Surfaces.Refs;
        }
        set
        {
            if (value is null) throw new JsonException("surfaces must not be null.");
            Surfaces.Refs.Clear();
            Surfaces.Refs.AddRange(value);
        }
    }
}

public sealed partial class MapPlacement
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapSupportBinding? SupportBinding { get; set; }
}
