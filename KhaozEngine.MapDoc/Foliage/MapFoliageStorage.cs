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

/// <summary>Closed stored foliage, bounded pack reads and manifest-last preparation. With 2 m samples and 512 m tiles,
/// 64 chunks share one 68,108 byte pack. This trades 64 opens/files and 1024 rewritten density bytes per changed chunk
/// for one open/file and 68,108 rewritten bytes, including the header and records. A one-chunk region still costs one
/// file, and tiny cells can reach the explicit 16,384 chunk pack ceiling. These are size facts, not timing results.</summary>
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

    internal static MapFoliagePreparedStorage Prepare(MapDocument document, string directory)
    {
        MapFoliageStorageIndex? prior = document.Tiles?.Foliage;
        bool partial = document.Tiles?.IsPartial == true;
        var carried = new Dictionary<(string Layer, MapFoliageChunkKey Key), MapFoliageChunk>();
        if (partial && prior is not null)
        {
            foreach (MapFoliagePackRef pack in prior.Packs)
            {
                Require(prior.ReadPack(directory, pack, out var records), pack);
                foreach (MapFoliageChunk chunk in records!) carried.Add((pack.LayerId, chunk.Key), chunk);
            }
            // An unseen advertised record cannot be removed by dropping its layer or directory row.
            foreach (MapFoliageChunkRef row in prior.Chunks.Where(c => !prior.WasResident(c.LayerId, c.Key)))
            {
                MapFoliageLayer? layer = document.Foliage?.Layers.FirstOrDefault(l => l.Id == row.LayerId);
                if (layer is null || !layer.ChunkDigests.Any(d => d.Key == row.Key && d.Sha256 == row.Sha256))
                    throw new MapDocumentException("foliage partial save would drop or replace unavailable advertised data.");
            }
        }
        var packs = new List<MapFoliagePackRef>();
        var references = new List<MapFoliageChunkRef>();
        var payloads = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (document.Foliage is not { } set) return new(new(packs, references), [], keep);
        RequireValid(set);
        long membershipCount = 0;
        foreach (MapFoliageLayer layer in set.Layers)
            foreach (MapFoliageChunkDigest row in layer.ChunkDigests)
            {
                membershipCount = checked(membershipCount + RegionRange(layer, row.Key, document.TileSize).Count);
                MapFoliageManifestBudget.CheckMemberships(membershipCount);
            }
        foreach (MapFoliageLayer layer in set.Layers)
        {
            var resident = layer.Chunks.ToDictionary(c => c.Key);
            var chunks = new List<MapFoliageChunk>();
            foreach (MapFoliageChunkDigest row in layer.ChunkDigests)
            {
                if (resident.TryGetValue(row.Key, out MapFoliageChunk? chunk)) chunks.Add(chunk);
                else if (partial && carried.TryGetValue((layer.Id, row.Key), out chunk) && MapFoliageLayer.Digest(chunk) == row.Sha256) chunks.Add(chunk);
                else throw new MapDocumentException("foliage advertised density is unavailable, refusing to publish zero.");
            }
            // Padding is checked on carried records too, against the proposed metadata.
            RequireValid(new(1, [MapFoliageLayer.FromStorage(layer, layer.ChunkDigests, chunks)]));
            var membership = chunks.ToDictionary(c => c.Key, c => Regions(layer, c.Key, document.TileSize));
            foreach (var group in chunks.GroupBy(c => membership[c.Key][0]).OrderBy(g => g.Key.Z).ThenBy(g => g.Key.X))
            {
                MapFoliageChunk[] sorted = group.OrderBy(c => c.Key.Z).ThenBy(c => c.Key.X).ToArray();
                byte[] bytes = MapFoliageRegionPack.Encode(sorted);
                string hash = MapFoliageRegionPack.Digest(bytes);
                var pack = new MapFoliagePackRef(layer.Id, group.Key, hash, sorted.Length, bytes.Length);
                packs.Add(pack);
                string relative = MapFoliageStorageIndex.RelativePath(pack);
                payloads.TryAdd(relative, bytes); keep.Add(MapTiledFile.Normalize(Path.Combine(directory, relative)));
                for (int i = 0; i < sorted.Length; i++)
                {
                    MapFoliageChunk chunk = sorted[i];
                    references.Add(new(layer.Id, chunk.Key, group.Key, membership[chunk.Key], MapFoliageLayer.Digest(chunk), hash, MapFoliageRegionPack.DataOffset(i)));
                }
            }
        }
        var index = new MapFoliageStorageIndex(packs, references, directory, set);
        MapFoliageManifestBudget.Check(set, index);
        return new(index, Array.AsReadOnly(payloads.Select(p => (p.Key, p.Value)).ToArray()), keep)
        { FoliageSha256 = MapFoliageProjection.Digest(set) };
    }

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
            MapFoliageChunkRef[] selected = index.Chunks.Where(c => c.LayerId == pack.LayerId && c.OwnerRegion == pack.Region &&
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
        foreach (var payload in prepared.Payloads)
        {
            string path = Path.Combine(directory, payload.RelativePath);
            if (File.Exists(path))
            {
                MapFoliagePackRef pack = prepared.Index.Packs.First(p => MapFoliageStorageIndex.RelativePath(p) == payload.RelativePath);
                Require(prepared.Index.ReadPack(directory, pack, out _), pack);
                continue;
            }
            save.OnStep?.Invoke(MapTiledSaveStep.BeforeTileWrite);
            string parent = Path.GetDirectoryName(path)!; Directory.CreateDirectory(parent);
            string temp = path + MapTileFile.TempSuffix;
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

    static IReadOnlyList<MapTileCoord> Regions(MapFoliageLayer layer, MapFoliageChunkKey key, float tileSize)
    {
        var range = RegionRange(layer, key, tileSize);
        var regions = new List<MapTileCoord>((int)range.Count);
        for (long z = range.MinZ; z <= range.MaxZ; z++)
            for (long x = range.MinX; x <= range.MaxX; x++) regions.Add(new((int)x, (int)z));
        return Array.AsReadOnly(regions.ToArray());
    }

    static (long MinX, long MinZ, long MaxX, long MaxZ, long Count) RegionRange(MapFoliageLayer layer, MapFoliageChunkKey key, float tileSize)
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
    static void RequireValid(MapFoliageSet set)
    {
        var errors = MapFoliageValidation.Validate(set);
        if (errors.Count > 0) throw new MapDocumentException(string.Join("\n", errors));
    }
    static void Require(MapFoliageReadStatus status, MapFoliagePackRef pack)
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
