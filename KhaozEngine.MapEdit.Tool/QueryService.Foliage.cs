using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.MapEdit;

public sealed partial class QueryService
{
    /// <summary>Reads foliage without changing the document. An omitted <paramref name="layerId"/> lists every layer's
    /// detached metadata in authoring order, with no density and no preview. A named layer returns its metadata, the
    /// stored chunks the sample rectangle [x, x + width) by [z, z + depth) reaches when all four bounds are given, and a
    /// CPU preview of <paramref name="previewChunk"/> when given. Absent chunks are omitted and read zero. A rectangle or
    /// preview without a layer ID, a partial rectangle, an unknown layer, a rectangle outside the raster and a rectangle
    /// beyond the raster edit limits of 64 chunks and 65,536 samples refuse.</summary>
    public MapFoliageReadResult FoliageGet(string? layerId = null, int? x = null, int? z = null, int? width = null, int? depth = null,
        MapFoliageChunkKey? previewChunk = null)
    {
        int given = (x is null ? 0 : 1) + (z is null ? 0 : 1) + (width is null ? 0 : 1) + (depth is null ? 0 : 1);
        if (given is not (0 or 4)) throw new MapDocumentException("foliage_get takes x, z, width and depth together or not at all.");
        if (layerId is null)
        {
            if (given == 4 || previewChunk is not null)
                throw new MapDocumentException("foliage_get needs a layer ID to read density or a preview.");
            return session.WithDocument((document, _) => new MapFoliageReadResult(MapFoliageReadMode.List,
                Array.AsReadOnly((document.Foliage?.Layers ?? []).Select(l => new MapFoliageLayerInfo(l)).ToArray()), null, [], null));
        }
        if (previewChunk is not { } chunk)
            return session.WithDocument((document, _) => Read(document, layerId, x, z, width, depth, null));
        return session.WithNativeAssets(c => Read(c.Document, layerId, x, z, width, depth, Preview(c, layerId, chunk)));
    }

    static MapFoliageReadResult Read(MapDocument document, string id, int? x, int? z, int? width, int? depth, Func<MapFoliageSampleBatch>? preview)
    {
        MapFoliageLayer layer = document.Foliage?.Layers.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.Ordinal)) ??
            throw new MapDocumentException($"No foliage layer '{id}'.");
        IReadOnlyList<MapFoliageChunk> chunks = x is { } x0 && z is { } z0 && width is { } w && depth is { } d ? Chunks(layer, x0, z0, w, d) : [];
        return new(MapFoliageReadMode.Layer, [], new MapFoliageLayerInfo(layer), chunks, preview?.Invoke());
    }

    // The stored chunks the rectangle reaches, in row-major key order. Shape and budget refuse before any chunk is read.
    static IReadOnlyList<MapFoliageChunk> Chunks(MapFoliageLayer layer, int x, int z, int width, int depth)
    {
        var (hiX, hiZ) = MapFoliageBrush.RequireRectangle(layer, x, z, width, depth);
        int cx0 = x / MapFoliageChunk.Size, cz0 = z / MapFoliageChunk.Size, cx1 = hiX / MapFoliageChunk.Size, cz1 = hiZ / MapFoliageChunk.Size;
        var resident = layer.Chunks.Select(c => c.Key).ToHashSet();
        foreach (MapFoliageChunkDigest advertised in layer.ChunkDigests)
            if (advertised.Key.X >= cx0 && advertised.Key.X <= cx1 && advertised.Key.Z >= cz0 && advertised.Key.Z <= cz1 && !resident.Contains(advertised.Key))
                throw new MapDocumentException($"Foliage layer '{layer.Id}' chunk {advertised.Key} is unavailable.");
        return Array.AsReadOnly(layer.Chunks.Where(c => c.Key.X >= cx0 && c.Key.X <= cx1 && c.Key.Z >= cz0 && c.Key.Z <= cz1).ToArray());
    }

    // One captured context serves every preview of the same accepted session snapshot, keyed by the session's snapshot
    // version. A refused capture is reported as the preview batch's status, with no samples.
    Func<MapFoliageSampleBatch> Preview(NativeAssetContext c, string layerId, MapFoliageChunkKey chunk) => () =>
    {
        MapFoliageContextResult captured = session.FoliagePreviewContext(() => MapFoliageGenerationContext.Capture(c.Document, c.Assets));
        return captured.Context is { } context
            ? MapFoliageDistribution.Generate(context, layerId, chunk)
            : new MapFoliageSampleBatch(layerId, chunk, captured.Status, "", [], new MapFoliageWork(0, 0, 0, 0, 0, 0, 0, 0));
    };
}
