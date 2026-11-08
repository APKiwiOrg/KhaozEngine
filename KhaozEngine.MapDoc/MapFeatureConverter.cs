using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace KhaozEngine.MapDoc;

/// <summary>Registry-driven polymorphic converter for <see cref="MapFeature"/>: reads the "type" discriminator,
/// resolves the DTO type through the registry, and delegates to the default (de)serialization for the concrete
/// type. Built-in JsonDerivedType attributes are not used here because the feature set is open (games register
/// types at runtime).</summary>
internal sealed class MapFeatureConverter : JsonConverter<MapFeature>
{
    readonly MapDocRegistry _registry;

    public MapFeatureConverter(MapDocRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public override MapFeature Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        string? type = doc.RootElement.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
        if (type is null || !_registry.TryGetFeatureDocType(type, out Type docType))
            throw new MapFeatureJsonException(type is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.DocumentTerrainFeatureUnknown,
                $"Unknown terrain feature type '{type ?? "(missing)"}'.") { FieldPath = "$.type", ActualText = type };
        // docType is a concrete type, never MapFeature itself, so this does not re-enter the converter.
        var feature = (MapFeature?)doc.RootElement.Deserialize(docType, options);
        return feature ?? throw new MapFeatureJsonException(MapDocumentRefusal.SchemaValueRequired, $"Terrain feature '{type}' deserialized to null.")
            { FieldPath = "$", ActualText = type };
    }

    public override void Write(Utf8JsonWriter writer, MapFeature value, JsonSerializerOptions options)
    {
        JsonObject node = JsonSerializer.SerializeToNode(value, value.GetType(), options)!.AsObject();
        node["type"] = value.Type;
        node.WriteTo(writer, options);
    }
}
