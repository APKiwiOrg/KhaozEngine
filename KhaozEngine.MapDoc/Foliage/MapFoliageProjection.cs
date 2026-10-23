using System;
using System.Text;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Logical authored foliage, independent of packing and residency. Ordered metadata and complete raw chunk
/// digest rows enter the kemap/foliage-authored/1 scheme. Inline bytes, paths and offsets never enter it.</summary>
internal static class MapFoliageProjection
{
    internal static string Digest(MapFoliageSet? set)
    {
        using var sink = new MapCanonical.HashingBufferWriter();
        sink.Append(Encoding.UTF8.GetBytes("kemap/foliage-authored/1\0"));
        using (var writer = new Utf8JsonWriter(sink)) Write(writer, set);
        return Convert.ToHexStringLower(sink.GetHashAndReset());
    }
    internal static void Write(Utf8JsonWriter writer, MapFoliageSet? set)
    {
        if (set is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject(); writer.WriteNumber("payloadVersion", set.PayloadVersion);
        writer.WriteStartArray("layers");
        foreach (MapFoliageLayer layer in set.Layers)
            MapFoliageJson.WriteLayer(writer, layer, (w, l) =>
            {
                foreach (MapFoliageChunkDigest row in l.ChunkDigests)
                {
                    w.WriteStartObject(); w.WriteNumber("x", row.Key.X); w.WriteNumber("z", row.Key.Z);
                    w.WriteString("sha256", row.Sha256); w.WriteEndObject();
                }
            });
        writer.WriteEndArray(); writer.WriteEndObject();
    }
    internal static void WriteProperty(Utf8JsonWriter writer, MapFoliageSet? set)
    {
        if (set is null) return;
        writer.WritePropertyName("foliage"); Write(writer, set);
    }
}
