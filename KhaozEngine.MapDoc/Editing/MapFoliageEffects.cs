using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>One changed generated foliage batch, keyed by its layer and chunk. A side exists when the layer exists there,
/// the batch is not provably off its raster and it reads at least one stored density chunk. A missing side has null
/// bounds and a null digest. Bounds are the cosmetic world XZ rectangle where the batch may place samples: the chunk's
/// world rectangle clipped to the raster's sample domain, rounded outward to single precision. A digest is the batch
/// dependency digest a generated batch carries. A side whose preparation refuses deterministically carries a digest
/// that binds its status and the read chunks' direct digests instead.</summary>
public sealed record MapFoliageChunkChange(string LayerId, MapFoliageChunkKey Chunk, MapFoliageRect? OldBounds,
    MapFoliageRect? NewBounds, string? BeforeDigest, string? AfterDigest);

/// <summary>Foliage effects of one document edit. <see cref="Describe"/> publishes batch rows. The direct diff reads
/// authored layer metadata and advertised chunk digests only and is an internal pre-filter, never a published row. A
/// settings change on a layer with no stored chunks still writes the foliage root but reports no chunk and no
/// invalidation, because an all-zero raster places nothing.</summary>
public static class MapFoliageEffects
{
    /// <summary>The changed batches between two complete documents, read from contexts captured from exactly those
    /// documents and their closures. Direct rows come from changed density chunks and layer settings. Dependent rows come
    /// from every batch whose preparation acquisition meets the old or new bounds of a changed input: support, paint,
    /// rooms, rules, masks, markers and clearance owners measured by their proxies. A context may be null only for a
    /// document without foliage. <paramref name="trigger"/> is the base effect set whose foliage channel these rows
    /// replace. Only its foliage channel is superseded, it never narrows the diff. Over-budget selection or a capacity
    /// refusal throws, so a caller publishes all rows or none.</summary>
    public static IReadOnlyList<MapFoliageChunkChange> Describe(MapDocument before, MapDocument after,
        MapFoliageGenerationContext? oldContext, MapFoliageGenerationContext? newContext, MapNativeEditEffects trigger)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(trigger);
        Require(before, oldContext, nameof(oldContext));
        Require(after, newContext, nameof(newContext));
        return MapFoliageDependents.Rows(before, after,
            () => oldContext ?? throw new ArgumentNullException(nameof(oldContext)),
            () => newContext ?? throw new ArgumentNullException(nameof(newContext)));
    }

    // A context must hold exactly the document's logical foliage. A stale context from another snapshot refuses.
    static void Require(MapDocument document, MapFoliageGenerationContext? context, string name)
    {
        if (context is null)
        {
            if (document.Foliage is not null) throw new ArgumentNullException(name, "a document with foliage needs its context.");
            return;
        }
        if (!string.Equals(MapFoliageProjection.Digest(context.Foliage), MapFoliageProjection.Digest(document.Foliage), StringComparison.Ordinal))
            throw new ArgumentException("the foliage context was not captured from this document.", name);
    }

    internal static IReadOnlyList<MapFoliageChunkChange> Direct(MapDocument before, MapDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
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
    internal static string Settings(MapFoliageLayer layer) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray();
        w.WriteStringValue("kemap/foliage-layer-settings/1");
        MapFoliageJson.WriteLayer(w, layer, (_, _) => { });
        w.WriteEndArray();
    });

    internal static string Digest(string settings, MapFoliageChunkKey key, string sha256) => MapCanonical.HashHex(w =>
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

    internal static float Down(double value)
    {
        float rounded = (float)value;
        return rounded > value ? MathF.BitDecrement(rounded) : rounded;
    }

    internal static float Up(double value)
    {
        float rounded = (float)value;
        return rounded < value ? MathF.BitIncrement(rounded) : rounded;
    }
}
