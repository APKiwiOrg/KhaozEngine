using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

internal sealed record MapFoliagePreparedStorage(MapFoliageStorageIndex Index,
    IReadOnlyList<(string RelativePath, ReadOnlyMemory<byte> Bytes)> Payloads, IReadOnlySet<string> Keep)
{
    internal string FoliageSha256 { get; init; } = MapFoliageProjection.Digest(null);
}

/// <summary>Closed stored foliage and manifest-last publication. Unchanged logical rows carry a present pinned pack
/// by reference without reading or encoding it. Rewriting a pack encodes only that region, reading its prior pack only
/// when needed for nonresident records. Full byte verification belongs to VerifyTiled and stored identity.
/// With 2 m samples and 512 m tiles, 64 chunks share one 68,108 byte file. A changed chunk rewrites that file rather
/// than a 1024 byte individual payload, trading 64 possible files/opens for one region file. Sparse one-chunk regions
/// still cost one file, and tiny cells can reach the 16,384 chunk ceiling. These are size facts, not timings.</summary>
internal static class MapFoliageStorage
{
    internal static (MapFoliageSet Set, MapFoliageStorageIndex Index) ReadManifest(JsonElement value)
    {
        MapFoliageSet metadata = MapFoliageJson.ReadStoredMetadata(value);
        JsonElement packArray = MapFoliageJson.RequireArray(value, "packs", "foliage", MapFoliagePayloadLimits.MaxDocumentChunks);
        var packs = new List<MapFoliagePackRef>(packArray.GetArrayLength());
        foreach (JsonElement pack in packArray.EnumerateArray())
        {
            MapFoliageJson.Closed(pack, "foliage pack", ["layerId", "region", "payloadSha256", "chunkCount", "byteLength"]);
            packs.Add(new(Text(pack, "layerId"), Coord(pack.GetProperty("region")), Text(pack, "payloadSha256"),
                Int(pack, "chunkCount"), Int(pack, "byteLength")));
        }
        var chunks = new List<MapFoliageChunkRef>();
        var layers = new List<MapFoliageLayer>();
        int layerIndex = 0;
        foreach (JsonElement layer in value.GetProperty("layers").EnumerateArray())
        {
            MapFoliageLayer m = metadata.Layers[layerIndex++];
            var advertised = new List<MapFoliageChunkDigest>();
            foreach (JsonElement chunk in layer.GetProperty("chunks").EnumerateArray())
            {
                MapFoliageJson.Closed(chunk, "foliage stored chunk", ["layerId", "x", "z", "ownerRegion", "regions", "sha256", "packSha256", "dataOffset"]);
                string id = Text(chunk, "layerId");
                if (id != m.Id) throw new MapDocumentException("foliage chunk names a different layer.");
                var key = new MapFoliageChunkKey(Int(chunk, "x"), Int(chunk, "z"));
                var regions = MapFoliageJson.RequireArray(chunk, "regions", "foliage stored chunk", 4096).EnumerateArray().Select(Coord).ToArray();
                string digest = Text(chunk, "sha256");
                chunks.Add(new(id, key, Coord(chunk.GetProperty("ownerRegion")), regions, digest, Text(chunk, "packSha256"), Int(chunk, "dataOffset")));
                advertised.Add(new(key, digest));
            }
            layers.Add(MapFoliageLayer.FromStorage(m, advertised, []));
        }
        var set = new MapFoliageSet(metadata.PayloadVersion, layers);
        RequireValid(set);
        var ids = set.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        if (packs.Any(p => !ids.Contains(p.LayerId))) throw new MapDocumentException("foliage pack names an unknown layer.");
        return (set, new(packs, chunks));
    }

    internal static MapFoliagePreparedStorage Prepare(MapDocument document, string directory) =>
        MapFoliagePackPreparation.Prepare(document, directory);

    internal static void RequireUnchanged(MapDocument document, MapFoliagePreparedStorage prepared)
    {
        if (MapFoliageProjection.Digest(document.Foliage) != prepared.FoliageSha256)
            throw new MapDocumentException("foliage changed during tiled save, refusing manifest publication.");
    }

    internal static void Load(MapDocument document, string directory, MapTileRect? window)
    {
        if (document.Foliage is not { } set || document.Tiles?.Foliage is not { } index) return;
        ValidateLayout(document, index);
        var resident = new Dictionary<string, List<MapFoliageChunk>>(StringComparer.Ordinal);
        foreach (MapFoliagePackRef pack in index.Packs)
        {
            MapFoliageChunkRef[] selected = index.ChunksFor(pack).Select(MapFoliageStorageWork.Inspect).Where(c => c.LayerId == pack.LayerId && c.OwnerRegion == pack.Region &&
                (window is null || c.Regions.Any(window.Value.Contains))).ToArray();
            if (selected.Length == 0) continue;
            Require(index.ReadPack(directory, pack, out var chunks), pack);
            var keys = selected.Select(c => c.Key).ToHashSet();
            if (!resident.TryGetValue(pack.LayerId, out var list)) resident.Add(pack.LayerId, list = new());
            list.AddRange(chunks!.Where(c => keys.Contains(c.Key)));
        }
        document.Foliage = new(set.PayloadVersion, set.Layers.Select(l => MapFoliageLayer.FromStorage(l, l.ChunkDigests,
            resident.TryGetValue(l.Id, out var list) ? list : [])).ToArray());
        RequireValid(document.Foliage);
        document.Tiles = new(document.Tiles.TileSize, document.Tiles.SchemeVersion, document.Tiles.SourceDirectory,
            document.Tiles.Entries, document.Tiles.ManifestSha256, document.Tiles.Surfaces)
        { Foliage = index.WithResidency(directory, document.Foliage) };
    }

    internal static MapFoliageSet? ReadComplete(MapFoliageSet? set, MapFoliageStorageIndex? index, string directory)
    {
        if (set is null) return null;
        if (index is null) { RequireComplete(set); return set.Clone(); }
        var chunks = new Dictionary<string, List<MapFoliageChunk>>(StringComparer.Ordinal);
        foreach (MapFoliagePackRef pack in index.Packs)
        {
            Require(index.ReadPack(directory, pack, out var records), pack);
            if (!chunks.TryGetValue(pack.LayerId, out var list)) chunks.Add(pack.LayerId, list = new());
            list.AddRange(records!);
        }
        var complete = new MapFoliageSet(set.PayloadVersion, set.Layers.Select(l => MapFoliageLayer.FromStorage(l, l.ChunkDigests,
            chunks.TryGetValue(l.Id, out var list) ? list : [])).ToArray());
        RequireComplete(complete); RequireValid(complete);
        return complete;
    }

    internal static void WriteManifest(Utf8JsonWriter writer, MapFoliageSet set, MapFoliageStorageIndex index)
    {
        writer.WriteStartObject(); writer.WriteNumber("payloadVersion", set.PayloadVersion);
        writer.WriteStartArray("layers");
        foreach (MapFoliageLayer layer in set.Layers)
            MapFoliageJson.WriteLayer(writer, layer, (w, l) =>
            {
                foreach (MapFoliageChunkRef row in index.Chunks.Where(c => c.LayerId == l.Id))
                {
                    w.WriteStartObject(); w.WriteString("layerId", row.LayerId);
                    w.WriteNumber("x", row.Key.X); w.WriteNumber("z", row.Key.Z);
                    WriteCoord(w, "ownerRegion", row.OwnerRegion);
                    w.WriteStartArray("regions"); foreach (MapTileCoord region in row.Regions) WriteCoord(w, null, region); w.WriteEndArray();
                    w.WriteString("sha256", row.Sha256); w.WriteString("packSha256", row.PackSha256);
                    w.WriteNumber("dataOffset", row.DataOffset); w.WriteEndObject();
                }
            });
        writer.WriteEndArray(); writer.WriteStartArray("packs");
        foreach (MapFoliagePackRef pack in index.Packs)
        {
            writer.WriteStartObject(); writer.WriteString("layerId", pack.LayerId); WriteCoord(writer, "region", pack.Region);
            writer.WriteString("payloadSha256", pack.PayloadSha256); writer.WriteNumber("chunkCount", pack.ChunkCount);
            writer.WriteNumber("byteLength", pack.ByteLength); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
    }

    internal static void Write(string directory, MapFoliagePreparedStorage prepared, MapDocumentSaveOptions save, HashSet<string> touched)
    {
        var byPath = new Dictionary<string, MapFoliagePackRef>(StringComparer.Ordinal);
        foreach (var pack in prepared.Index.Packs) byPath.TryAdd(MapFoliageStorageIndex.RelativePath(pack), pack);
        foreach (var payload in prepared.Payloads)
        {
            string path = Path.Combine(directory, payload.RelativePath);
            if (File.Exists(path))
            {
                MapFoliagePackRef pack = byPath[payload.RelativePath];
                Require(prepared.Index.ReadPack(directory, pack, out _), pack);
                continue;
            }
            save.OnStep?.Invoke(MapTiledSaveStep.BeforeTileWrite);
            string parent = Path.GetDirectoryName(path)!; Directory.CreateDirectory(parent);
            string temp = path + MapTileFile.TempSuffix;
            if (MapFoliageStorageWork.Current is { } work) work.PackWrites++;
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(payload.Bytes.Span);
                if (save.Durability == MapSaveDurability.PowerFail) stream.Flush(true);
            }
            File.Move(temp, path);
            while (parent != directory) { touched.Add(parent); parent = Path.GetDirectoryName(parent)!; }
            save.OnStep?.Invoke(MapTiledSaveStep.AfterTileWrite);
        }
    }

    internal static void Verify(string root, MapDocument doc, MapTileIndex tiles, HashSet<string> named, List<string> report)
    {
        if (tiles.Foliage is not { } index) return;
        try { ValidateLayout(doc, index); }
        catch (MapDocumentException ex) { report.Add(ex.Message); }
        bool complete = true;
        foreach (MapFoliagePackRef pack in index.Packs)
        {
            named.Add(MapTiledFile.Normalize(MapFoliageStorageIndex.PathOf(root, pack)));
            MapFoliageReadStatus status = index.ReadPack(root, pack, out _);
            if (status != MapFoliageReadStatus.Found) { report.Add($"foliage pack {status}: {pack.PayloadSha256}"); complete = false; }
        }
        if (!complete) return;
        try { _ = ReadComplete(doc.Foliage, index, root); }
        catch (MapDocumentException ex) { report.Add(ex.Message); }
    }

    internal static void ValidateLayout(MapDocument doc, MapFoliageStorageIndex index)
    {
        var layers = doc.Foliage!.Layers.ToDictionary(l => l.Id, StringComparer.Ordinal);
        foreach (MapFoliageChunkRef row in index.Chunks)
        {
            IReadOnlyList<MapTileCoord> regions = Regions(layers[row.LayerId], row.Key, doc.TileSize);
            if (regions[0] != row.OwnerRegion || !regions.SequenceEqual(row.Regions))
                throw new MapDocumentException("foliage chunk owner region or memberships disagree with its density lattice.");
        }
    }

    internal static IReadOnlyList<MapTileCoord> Regions(MapFoliageLayer layer, MapFoliageChunkKey key, float tileSize)
    {
        var range = RegionRange(layer, key, tileSize);
        var regions = new List<MapTileCoord>((int)range.Count);
        for (long z = range.MinZ; z <= range.MaxZ; z++)
            for (long x = range.MinX; x <= range.MaxX; x++) regions.Add(new((int)x, (int)z));
        return Array.AsReadOnly(regions.ToArray());
    }

    internal static (long MinX, long MinZ, long MaxX, long MaxZ, long Count) RegionRange(MapFoliageLayer layer, MapFoliageChunkKey key, float tileSize)
    {
        try
        {
            MapExactValue size = MapExactValue.FromSingle(tileSize), cell = MapExactValue.FromSingle(layer.CellSize);
            long Region(float origin, long sample) => MapExactValue.FromSingle(origin).Add(cell.Multiply(new(sample, 1))).Divide(size).Floor();
            long minX = Region(layer.OriginX, checked((long)key.X * 32)), minZ = Region(layer.OriginZ, checked((long)key.Z * 32));
            long maxX = Region(layer.OriginX, Math.Min(checked((long)key.X * 32 + 31), layer.Width - 1));
            long maxZ = Region(layer.OriginZ, Math.Min(checked((long)key.Z * 32 + 31), layer.Depth - 1));
            long count = checked((maxX - minX + 1) * (maxZ - minZ + 1));
            if (minX < int.MinValue || minZ < int.MinValue || maxX > int.MaxValue || maxZ > int.MaxValue ||
                maxX < minX || maxZ < minZ || count > 4096)
                throw new MapDocumentException("foliage region memberships exceed capacity or are not representable.");
            return (minX, minZ, maxX, maxZ, count);
        }
        catch (Exception ex) when (ex is ArithmeticException or ArgumentException)
        { throw new MapDocumentException("foliage region memberships are not representable.", ex); }
    }

    internal static void RequireComplete(MapFoliageSet? set)
    {
        if (set is null) return;
        foreach (MapFoliageLayer layer in set.Layers)
        {
            if (layer.Chunks.Count != layer.ChunkDigests.Count)
                throw new MapDocumentException("foliage advertised density is incomplete.");
            for (int i = 0; i < layer.Chunks.Count; i++)
                if (layer.Chunks[i].Key != layer.ChunkDigests[i].Key || MapFoliageLayer.Digest(layer.Chunks[i]) != layer.ChunkDigests[i].Sha256)
                    throw new MapDocumentException("foliage advertised density digest mismatch.");
        }
    }
    internal static void RequireValid(MapFoliageSet set)
    {
        var errors = MapFoliageValidation.Validate(set);
        if (errors.Count > 0) throw new MapDocumentException(string.Join("\n", errors));
    }
    internal static void Require(MapFoliageReadStatus status, MapFoliagePackRef pack)
    {
        if (status != MapFoliageReadStatus.Found) throw new MapDocumentException($"foliage pack {status}: {pack.PayloadSha256}");
    }
    static string Text(JsonElement e, string name) => MapFoliageJson.Text(e, name, "foliage storage");
    static int Int(JsonElement e, string name) => MapFoliageJson.Int(e, name, "foliage storage");
    static MapTileCoord Coord(JsonElement e)
    {
        MapFoliageJson.Closed(e, "foliage region", ["x", "z"]); return new(Int(e, "x"), Int(e, "z"));
    }
    static void WriteCoord(Utf8JsonWriter w, string? name, MapTileCoord coord)
    {
        if (name is null) w.WriteStartObject(); else w.WriteStartObject(name);
        w.WriteNumber("x", coord.X); w.WriteNumber("z", coord.Z); w.WriteEndObject();
    }
}
