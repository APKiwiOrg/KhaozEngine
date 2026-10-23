using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.MapDoc;

public sealed partial class MapDocument
{
    /// <summary>Authored foliage density layers. Null means the document authors no foliage and writes no section.
    /// Independent of analytic scatter, companion layers and explicit scenery.</summary>
    [JsonConverter(typeof(MapFoliageSetJsonConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(MapDocumentOrder.Foliage)]
    public MapFoliageSet? Foliage { get; set; }
}
