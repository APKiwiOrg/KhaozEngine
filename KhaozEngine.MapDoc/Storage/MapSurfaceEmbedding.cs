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
        if (set.Patches.Count > maxPatches || maxEncodedBytes < 2)
            throw set.Patches.Count > maxPatches ? Limit("MaxEmbeddedPatches", set.Patches.Count, maxPatches)
                : InvalidBudget("maxEncodedBytes", maxEncodedBytes, 2);
        long bytes = 2;
        int count = 0;
        foreach (MapSurfacePatch patch in set.Patches.Values)
        {
            byte[] encoded = MapSurfacePatchCodec.Encode(patch, out var ownership);
            using var consumed = ownership;
            int length = checked(encoded.Length + (count++ == 0 ? 0 : 1));
            if (length > maxEncodedBytes - bytes) throw Limit("MaxEmbeddingBytes", bytes + length, maxEncodedBytes, bytes: true);
            bytes += length;
        }
    }
    internal static void Read(JsonObject root, MapSurfaceSet set) =>
        Read(root, set, MapSurfacePatchCodec.MaxEncodedBytes, MaxEncodedBytes, static size => new byte[size]);

    internal static void Read(JsonObject root, MapSurfaceSet set, int maxPatchBytes, long maxEncodedBytes,
        Func<int, byte[]> allocatePayload)
    {
        ArgumentNullException.ThrowIfNull(allocatePayload);
        if (!MapDocumentMembers.TryGetProperty(root, "surfacePatches", out JsonNode? node)) return;
        JsonArray? patches = node as JsonArray;
        if (patches is null || patches.Count > MaxPatches || maxPatchBytes < 0 || maxEncodedBytes < 2)
            throw patches is null ? new MapDocumentException(MapDocumentRefusal.SchemaArrayRequired, "surface embedding exceeds its limit, use tiled storage")
            { FieldPath = "$.surfacePatches" }
                : patches.Count > MaxPatches ? Limit("MaxEmbeddedPatches", patches.Count, MaxPatches)
                : maxPatchBytes < 0 ? InvalidBudget("maxPatchBytes", maxPatchBytes, 0)
                : InvalidBudget("maxEncodedBytes", maxEncodedBytes, 2);
        long remaining = maxEncodedBytes - 2;
        bool first = true;
        int index = 0;
        foreach (JsonNode? item in patches)
        {
            if (item is not JsonObject patch) throw new MapDocumentException(MapDocumentRefusal.SchemaObjectRequired, "invalid embedded surface patch")
            { FieldPath = $"$.surfacePatches[{index}]", Index = index };
            if (!first)
            {
                if (remaining < 1) throw Limit("MaxEmbeddingBytes", maxEncodedBytes - remaining + 1, maxEncodedBytes, bytes: true);
                remaining--;
            }
            byte[] bytes = MapSurfaceEmbeddedPayload.Encode(patch, (int)Math.Min(maxPatchBytes, remaining), allocatePayload);
            remaining -= bytes.Length;
            first = false;
            MapPatchKey key = patch["key"]!.Deserialize<MapPatchKey>(MapDocumentFile.CreateCompactOptions(MapDocRegistry.CreateDefault()));
            if (!set.Patches.TryAdd(key, MapSurfacePatchCodec.Decode(bytes, key)))
                throw new MapDocumentException(MapDocumentRefusal.IdentityDuplicate, "duplicate embedded surface patch")
                { Patch = key, Index = index, FieldPath = $"$.surfacePatches[{index}].key" };
            index++;
        }
    }
    static MapDocumentException Limit(string budget, long actual, long limit, bool bytes = false) =>
        new(MapDocumentRefusal.WorkCapacityExceeded, "surface embedding exceeds its limit, use tiled storage")
        {
            FieldPath = "$.surfacePatches",
            Budget = budget,
            ActualCount = bytes ? null : actual,
            ActualByteCount = bytes ? actual : null,
            Limit = limit
        };

    static MapDocumentException InvalidBudget(string field, long actual, long minimum) =>
        new(MapDocumentRefusal.ValueOutsideRange, "surface embedding exceeds its limit, use tiled storage")
        { FieldPath = field, ActualInteger = actual, MinimumInteger = minimum };
}
