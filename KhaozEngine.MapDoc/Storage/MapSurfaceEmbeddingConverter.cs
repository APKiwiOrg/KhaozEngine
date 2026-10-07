using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Streams bounded canonical payloads through ordinary root serialization.</summary>
internal sealed class MapSurfaceEmbeddingConverter : JsonConverter<List<MapSurfacePatch>>
{
    readonly int _maxPatchBytes;
    readonly long _maxEncodedBytes;
    readonly Func<int, byte[]> _allocatePayload;

    public MapSurfaceEmbeddingConverter() : this(MapSurfacePatchCodec.MaxEncodedBytes,
        MapSurfaceEmbedding.MaxEncodedBytes, static size => new byte[size])
    { }

    internal MapSurfaceEmbeddingConverter(int maxPatchBytes, long maxEncodedBytes, Func<int, byte[]> allocatePayload)
    {
        ArgumentNullException.ThrowIfNull(allocatePayload);
        _maxPatchBytes = maxPatchBytes;
        _maxEncodedBytes = maxEncodedBytes;
        _allocatePayload = allocatePayload;
    }

    public override List<MapSurfacePatch> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument json = JsonDocument.ParseValue(ref reader);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > MapSurfaceEmbedding.MaxPatches ||
            _maxPatchBytes < 0 || _maxEncodedBytes < 2)
            throw new JsonException("surface embedding limit, use tiled storage");
        var result = new List<MapSurfacePatch>();
        long remaining = _maxEncodedBytes - 2;
        foreach (JsonElement element in json.RootElement.EnumerateArray())
        {
            if (result.Count != 0)
            {
                if (remaining < 1) throw new JsonException("surface embedding limit, use tiled storage");
                remaining--;
            }
            byte[] payload;
            try
            {
                payload = MapSurfaceEmbeddedPayload.Encode(element, (int)Math.Min(_maxPatchBytes, remaining), _allocatePayload);
            }
            catch (MapDocumentException error)
            {
                throw new JsonException(error.Message, error);
            }
            remaining -= payload.Length;
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
