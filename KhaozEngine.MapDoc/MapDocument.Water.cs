using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc;

public sealed partial class MapDocument
{
    /// <summary>Bounded native water. Null keeps legacy global water. A present set, even an empty one, makes the
    /// world dry wherever no body covers.</summary>
    [JsonConverter(typeof(MapWaterSetJsonConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(MapDocumentOrder.Water)]
    public MapWaterSet? Water { get; set; }
}
