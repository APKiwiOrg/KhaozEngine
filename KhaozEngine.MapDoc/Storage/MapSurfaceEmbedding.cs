using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Small whole documents embed the same canonical payload shape used by tiled storage.</summary>
internal static class MapSurfaceEmbedding
{
    internal const int MaxPatches = 256;
    internal const long MaxEncodedBytes = 8_388_608L;
    internal static void Check(MapSurfaceSet set, int maxPatches, long maxEncodedBytes)
    {
        if (set.Patches.Count > maxPatches || maxEncodedBytes < 2) throw Limit();
        long bytes = 2;
        int count = 0;
        foreach (MapSurfacePatch patch in set.Patches.Values)
        {
            int length = checked(MapSurfacePatchCodec.Encode(patch).Length + (count++ == 0 ? 0 : 1));
            if (length > maxEncodedBytes - bytes) throw Limit();
            bytes += length;
        }
    }
    internal static void Read(JsonObject root, MapSurfaceSet set)
    {
        if (!MapDocumentMembers.TryGetProperty(root, "surfacePatches", out JsonNode? node)) return;
        if (node is not JsonArray patches || patches.Count > MaxPatches) throw Limit();
        long total = 2;
        foreach (JsonNode? item in patches)
        {
            if (item is not JsonObject patch) throw new MapDocumentException("invalid embedded surface patch");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(patch);
            total = checked(total + bytes.Length + 1);
            if (total > MaxEncodedBytes) throw Limit();
            MapPatchKey key = patch["key"]!.Deserialize<MapPatchKey>(MapDocumentFile.CreateCompactOptions(MapDocRegistry.CreateDefault()));
            if (!set.Patches.TryAdd(key, MapSurfacePatchCodec.Decode(bytes, key))) throw new MapDocumentException("duplicate embedded surface patch");
        }
    }
    static MapDocumentException Limit() => new("surface embedding exceeds its limit, use tiled storage");
}
