using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Streams bounded canonical payloads through ordinary root serialization.</summary>
internal sealed class MapSurfaceEmbeddingConverter : JsonConverter<List<MapSurfacePatch>>
{
    public override List<MapSurfacePatch> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument json = JsonDocument.ParseValue(ref reader);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > MapSurfaceEmbedding.MaxPatches)
            throw new JsonException("surface embedding limit, use tiled storage");
        var result = new List<MapSurfacePatch>();
        long bytes = 2;
        foreach (JsonElement element in json.RootElement.EnumerateArray())
        {
            byte[] payload = Encoding.UTF8.GetBytes(element.GetRawText());
            bytes = checked(bytes + payload.Length + 1);
            if (bytes > MapSurfaceEmbedding.MaxEncodedBytes) throw new JsonException("surface embedding limit, use tiled storage");
            MapPatchKey key = element.GetProperty("key").Deserialize<MapPatchKey>(options);
            result.Add(MapSurfacePatchCodec.Decode(payload, key));
        }
        return result;
    }
    public override void Write(Utf8JsonWriter writer, List<MapSurfacePatch> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (MapSurfacePatch patch in value.OrderBy(p => p.Key)) writer.WriteRawValue(MapSurfacePatchCodec.Encode(patch));
        writer.WriteEndArray();
    }
}
