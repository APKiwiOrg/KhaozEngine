using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Scheme-one object normalization on manifest globals, extended by native surface metadata.</summary>
internal static class MapSurfaceRootProjection
{
    internal static string Digest(MapDocument root)
    {
        JsonSerializerOptions options = MapDocumentFile.CreateCompactOptions(MapDocRegistry.CreateDefault());
        root.NativeAssets = root.NativeAssets.OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("formatVersion", root.FormatVersion); writer.WriteString("id", root.Id);
            MapCanonical.WriteGlobals(writer, root, options);
            MapCanonical.WriteNativeGlobals(writer, root, options); writer.WriteEndObject();
        }
        JsonNode node = JsonNode.Parse(stream.ToArray())!;
        using var sink = new MapCanonical.HashingBufferWriter();
        sink.Append(Encoding.UTF8.GetBytes("kemap/native-root/2\0"));
        using (var writer = new Utf8JsonWriter(sink)) WriteNormalized(writer, node);
        return Convert.ToHexStringLower(sink.GetHashAndReset());
    }
    static void WriteNormalized(Utf8JsonWriter writer, JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            writer.WriteStartObject();
            foreach (var member in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(member.Key); WriteNormalized(writer, member.Value);
            }
            writer.WriteEndObject();
        }
        else if (node is JsonArray array)
        {
            writer.WriteStartArray(); foreach (JsonNode? item in array) WriteNormalized(writer, item); writer.WriteEndArray();
        }
        else if (node is null) writer.WriteNullValue();
        else node.WriteTo(writer);
    }
}
