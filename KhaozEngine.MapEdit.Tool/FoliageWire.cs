using System;
using System.Buffers;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using ModelContextProtocol;

namespace KhaozEngine.MapEdit;

/// <summary>The dedicated foliage wire mapping. Results become detached DTOs whose poses carry every binary32 component,
/// and requests read through the closed foliage payload codec. No core chunk memory reaches a DTO.</summary>
public static class FoliageWire
{
    /// <summary>The MCP defaults with camelCase members, string enums, every default-valued member written and the closed
    /// foliage support binding converter. Registered for the foliage tools only.</summary>
    internal static JsonSerializerOptions ToolOptions { get; } = CreateToolOptions();

    static JsonSerializerOptions CreateToolOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new SupportBindingConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Maps a detached read to its wire shape, copying every array.</summary>
    public static FoliageReadWireResult ToWire(MapFoliageReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result.Mode == MapFoliageReadMode.List ? "list" : "layer",
            result.Layers.Select(l => new FoliageLayerWire(l)).ToArray(),
            result.Layer is { } layer ? new FoliageLayerWire(layer) : null,
            result.Chunks.Select(c => new FoliageChunkWire(c.Key.X, c.Key.Z, c.CopyDensity(), c.Sha256)).ToArray(),
            result.Preview is { } preview ? Batch(preview) : null);
    }

    static FoliageBatchWire Batch(MapFoliageSampleBatch batch) => new(batch.LayerId, batch.Chunk.X, batch.Chunk.Z, batch.Status.ToString(),
        batch.DependencyDigest, batch.Samples.Select(Sample).ToArray(), batch.Work);

    static FoliageSampleWire Sample(MapFoliageSample s) => new(s.Key, s.OccupiedSpaceId, s.Face, s.AssetId, s.MaterialResourceIds.ToArray(),
        s.VisibleMaterial, new(s.Position.X, s.Position.Y, s.Position.Z), ToQuaternion(s.Orientation), s.Scale, ToMatrix(s.Transform), s.ThinningRank);

    static FoliageQuaternionWire ToQuaternion(Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    static FoliageMatrixWire ToMatrix(Matrix4x4 m) => new(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);

    /// <summary>Reads one layer in the closed inline payload 1 shape, the same object a layer takes inside the document's
    /// foliage section, through the payload codec and its limits.</summary>
    internal static MapFoliageLayer ReadLayer(string layerJson)
    {
        if (string.IsNullOrWhiteSpace(layerJson)) throw new MapDocumentException("foliage_layer_set requires layerJson.");
        using JsonDocument layer = JsonDocument.Parse(layerJson);
        if (layer.RootElement.ValueKind != JsonValueKind.Object) throw new MapDocumentException("layerJson must be one foliage layer object.");
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("payloadVersion", 1);
            writer.WriteStartArray("layers");
            layer.RootElement.WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using JsonDocument set = JsonDocument.Parse(buffer.WrittenMemory);
        return MapFoliageJson.Read(set.RootElement).Layers[0];
    }

    /// <summary>Reads a closed chunk address {x, z}.</summary>
    internal static MapFoliageChunkKey ReadChunkKey(string chunkJson)
    {
        if (string.IsNullOrWhiteSpace(chunkJson)) throw new MapDocumentException("previewChunkJson must be a chunk address object.");
        using JsonDocument json = JsonDocument.Parse(chunkJson);
        JsonElement e = json.RootElement;
        if (e.ValueKind != JsonValueKind.Object) throw new MapDocumentException("previewChunkJson must be a chunk address object.");
        MapFoliageJson.Closed(e, "previewChunkJson", ["x", "z"]);
        return new(MapFoliageJson.Int(e, "x", "previewChunkJson"), MapFoliageJson.Int(e, "z", "previewChunkJson"));
    }

    // The closed foliage binding: a Surface binding carries only its surface ID and a Space binding its space ID,
    // reference height and both search extents, written and read by the payload codec itself.
    sealed class SupportBindingConverter : JsonConverter<MapSupportBinding>
    {
        public override MapSupportBinding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument json = JsonDocument.ParseValue(ref reader);
            try { return MapFoliageJson.ReadBinding(json.RootElement, "foliage support binding"); }
            catch (MapDocumentException ex) { throw new JsonException(ex.Message, ex); }
        }

        public override void Write(Utf8JsonWriter writer, MapSupportBinding value, JsonSerializerOptions options) =>
            MapFoliageJson.WriteBinding(writer, value, "foliage support binding");
    }
}
