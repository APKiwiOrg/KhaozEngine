using System.Collections.Generic;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc;

public sealed partial class MapDocument
{
    [JsonConverter(typeof(MapPrefabSetJsonConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(MapDocumentOrder.Prefabs)]
    public MapPrefabSet? Prefabs { get; set; }

    [JsonConverter(typeof(MapBuildingIntentJsonConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(MapDocumentOrder.BuildingIntent)]
    public MapBuildingIntent? BuildingIntent { get; set; }

    [JsonConverter(typeof(MapStandaloneTransformsJsonConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(MapDocumentOrder.NativeResourceTransforms)]
    public IReadOnlyList<MapStandaloneResourceTransform>? NativeResourceTransforms { get; set; }
}
