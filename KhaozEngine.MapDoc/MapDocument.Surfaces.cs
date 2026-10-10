using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;

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
        get => Surfaces.Refs;
        set
        {
            if (value is null) throw new JsonException("surfaces must not be null.");
            Surfaces.Refs.Clear();
            Surfaces.Refs.AddRange(value);
        }
    }

    [JsonInclude]
    [JsonPropertyName("surfacePatches")]
    [JsonConverter(typeof(MapSurfaceEmbeddingConverter))]
    internal List<MapSurfacePatch>? SurfacePatches
    {
        get => Surfaces.Patches.Count == 0 ? null : Surfaces.Patches.Values.ToList();
        set
        {
            Surfaces.Patches.Clear();
            if (value is null) throw new JsonException("surfacePatches must not be null");
            foreach (MapSurfacePatch patch in value)
                if (!Surfaces.Patches.TryAdd(patch.Key, patch)) throw new JsonException("duplicate surface patch");
        }
    }
}

public sealed partial class MapPlacement
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapSupportBinding? SupportBinding { get; set; }
}
