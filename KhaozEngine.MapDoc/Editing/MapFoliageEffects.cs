using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>One directly changed stored density chunk. A side without the stored chunk, including a side without the
/// layer, has null bounds and a null digest. Bounds are the cosmetic world XZ rectangle of candidate points whose
/// bilinear density reads the chunk's samples under that side's raster. A digest binds the side's layer settings, the
/// chunk key and the chunk's logical bytes.</summary>
public sealed record MapFoliageChunkChange(string LayerId, MapFoliageChunkKey Chunk, MapFoliageRect? OldBounds,
    MapFoliageRect? NewBounds, string? BeforeDigest, string? AfterDigest);

/// <summary>The direct foliage diff. It reads authored layer metadata and advertised chunk digests only, so a byte edit
/// prepares no support, clearance or prefab expansion. A layer settings change names every stored chunk of the layer.
/// A settings change on a layer with no stored chunks still writes the foliage root but reports no chunk and no
/// invalidation, because an all-zero raster places nothing. Dependent changes, where an owner edit changes what clears
/// foliage, are not direct.</summary>
internal static class MapFoliageEffects
{
    internal static IReadOnlyList<MapFoliageChunkChange> Direct(MapDocument before, MapDocument after, MapAssetClosure assets)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(assets);
        Dictionary<string, MapFoliageLayer> old = Layers(before.Foliage), next = Layers(after.Foliage);
        var rows = new List<MapFoliageChunkChange>();
        foreach (string id in old.Keys.Union(next.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            old.TryGetValue(id, out MapFoliageLayer? a);
            next.TryGetValue(id, out MapFoliageLayer? b);
            if (ReferenceEquals(a, b)) continue;
            string? settingsA = a is null ? null : Settings(a), settingsB = b is null ? null : Settings(b);
            Dictionary<MapFoliageChunkKey, string> chunksA = Stored(a), chunksB = Stored(b);
            foreach (MapFoliageChunkKey key in chunksA.Keys.Union(chunksB.Keys).OrderBy(k => k.Z).ThenBy(k => k.X))
            {
                string? digestA = chunksA.TryGetValue(key, out string? shaA) ? Digest(settingsA!, key, shaA) : null;
                string? digestB = chunksB.TryGetValue(key, out string? shaB) ? Digest(settingsB!, key, shaB) : null;
                if (string.Equals(digestA, digestB, StringComparison.Ordinal)) continue;
                rows.Add(new(id, key, digestA is null ? null : Bounds(a!, key), digestB is null ? null : Bounds(b!, key), digestA, digestB));
            }
        }
        return Array.AsReadOnly(rows.ToArray());
    }

    static Dictionary<string, MapFoliageLayer> Layers(MapFoliageSet? set)
    {
        var layers = new Dictionary<string, MapFoliageLayer>(StringComparer.Ordinal);
        foreach (MapFoliageLayer layer in set?.Layers ?? []) layers.TryAdd(layer.Id, layer);
        return layers;
    }

    static Dictionary<MapFoliageChunkKey, string> Stored(MapFoliageLayer? layer) =>
        layer?.ChunkDigests.ToDictionary(d => d.Key, d => d.Sha256) ?? new Dictionary<MapFoliageChunkKey, string>();

    // Every authored layer member except the chunk list, in the closed codec's canonical form.
    static string Settings(MapFoliageLayer layer) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray();
        w.WriteStringValue("kemap/foliage-layer-settings/1");
        MapFoliageJson.WriteLayer(w, layer, (_, _) => { });
        w.WriteEndArray();
    });

    static string Digest(string settings, MapFoliageChunkKey key, string sha256) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray();
        w.WriteStringValue("kemap/foliage-chunk/1");
        w.WriteStringValue(settings);
        w.WriteNumberValue(key.X);
        w.WriteNumberValue(key.Z);
        w.WriteStringValue(sha256);
        w.WriteEndArray();
    });

    // A sample reaches every point within one cell of it, clamped to the raster's sample domain. Rounded outward to
    // single precision.
    static MapFoliageRect? Bounds(MapFoliageLayer layer, MapFoliageChunkKey key)
    {
        long firstX = (long)key.X * MapFoliageChunk.Size, firstZ = (long)key.Z * MapFoliageChunk.Size;
        if (firstX < 0 || firstZ < 0 || firstX >= layer.Width || firstZ >= layer.Depth) return null;
        long lastX = Math.Min(firstX + MapFoliageChunk.Size - 1, layer.Width - 1);
        long lastZ = Math.Min(firstZ + MapFoliageChunk.Size - 1, layer.Depth - 1);
        double minX = layer.OriginX + (double)Math.Max(0, firstX - 1) * layer.CellSize;
        double minZ = layer.OriginZ + (double)Math.Max(0, firstZ - 1) * layer.CellSize;
        double maxX = layer.OriginX + (double)Math.Min(layer.Width - 1, lastX + 1) * layer.CellSize;
        double maxZ = layer.OriginZ + (double)Math.Min(layer.Depth - 1, lastZ + 1) * layer.CellSize;
        return new(Down(minX), Down(minZ), Up(maxX), Up(maxZ));
    }

    static float Down(double value)
    {
        float rounded = (float)value;
        return rounded > value ? MathF.BitDecrement(rounded) : rounded;
    }

    static float Up(double value)
    {
        float rounded = (float)value;
        return rounded < value ? MathF.BitIncrement(rounded) : rounded;
    }
}
