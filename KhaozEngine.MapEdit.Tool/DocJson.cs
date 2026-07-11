using System;
using System.Text.Json;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEdit;

/// <summary>Parses and serializes the two document members that cross the MCP boundary as raw JSON strings:
/// terrain features (a registry-open tagged union) and shapes (the closed disc/rect/polygon union). Both use the
/// document's own serializer options (<see cref="MapDocumentFile.CreateOptions"/>) so the registry discriminators,
/// camelCase naming, and JSONC tolerance match exactly what load and save use. Typed parameter explosion across
/// the tool surface was rejected in favor of this, so the union stays extensible without new verbs.</summary>
public static class DocJson
{
    /// <summary>Parses a single terrain feature object (registry-open tagged union) using the document's own
    /// serializer options. The options carry <c>MapFeatureConverter</c>, so the registry discriminators drive the
    /// parse and an unknown discriminator throws <see cref="JsonException"/> with the SDK's precise message. A null
    /// deserialization result also throws.</summary>
    public static MapFeature ParseFeature(string featureJson, MapDocRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureJson);
        ArgumentNullException.ThrowIfNull(registry);
        MapFeature? feature = JsonSerializer.Deserialize<MapFeature>(
            featureJson, MapDocumentFile.CreateOptions(registry, write: false));
        return feature ?? throw new JsonException("terrain feature JSON deserialized to null.");
    }

    /// <summary>Parses a shape object (the closed disc/rect/polygon tagged union) using the document's own
    /// serializer options. A null deserialization result throws <see cref="JsonException"/>.</summary>
    public static MapShapeDoc ParseShape(string shapeJson, MapDocRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shapeJson);
        ArgumentNullException.ThrowIfNull(registry);
        MapShapeDoc? shape = JsonSerializer.Deserialize<MapShapeDoc>(
            shapeJson, MapDocumentFile.CreateOptions(registry, write: false));
        return shape ?? throw new JsonException("shape JSON deserialized to null.");
    }

    /// <summary>Serializes a feature back to compact JSON (the write:false options are not indented) for change
    /// reports. The registry-driven converter stamps the "type" discriminator.</summary>
    public static string FeatureToJson(MapFeature feature, MapDocRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(registry);
        return JsonSerializer.Serialize(feature, MapDocumentFile.CreateOptions(registry, write: false));
    }
}
