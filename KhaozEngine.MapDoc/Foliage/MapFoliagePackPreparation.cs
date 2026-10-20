using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Plans logical pack rows once and materializes density only for packs that need rewriting.</summary>
internal static class MapFoliagePackPreparation
{
    sealed record Row(MapFoliageChunkDigest Digest, IReadOnlyList<MapTileCoord> Regions);

    internal static MapFoliagePreparedStorage Prepare(MapDocument document, string directory)
    {
        MapFoliageStorageIndex? prior = document.Tiles?.Foliage;
        MapFoliageSet? set = document.Foliage;
        var layers = set?.Layers.ToDictionary(l => l.Id, StringComparer.Ordinal) ?? new();
        var logical = layers.ToDictionary(p => p.Key, p => p.Value.ChunkDigests.ToDictionary(d => d.Key), StringComparer.Ordinal);
        if (document.Tiles?.IsPartial == true && prior is not null)
            foreach (var row in prior.Chunks.Where(r => !prior.WasResident(r.LayerId, r.Key)))
                if (!logical.TryGetValue(row.LayerId, out var rows) || !rows.TryGetValue(row.Key, out var digest) || digest.Sha256 != row.Sha256)
                    throw new MapDocumentException("foliage partial save would drop or replace unavailable advertised data.");

        var packs = new List<MapFoliagePackRef>();
        var references = new List<MapFoliageChunkRef>();
        var payloads = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (set is null) return new(new(packs, references), [], keep);
        MapFoliageStorage.RequireValid(set);
        long memberships = 0;
        foreach (var layer in set.Layers)
            foreach (var row in layer.ChunkDigests)
            {
                memberships = checked(memberships + MapFoliageStorage.RegionRange(layer, row.Key, document.TileSize).Count);
                MapFoliageManifestBudget.CheckMemberships(memberships);
            }

        var decoded = new Dictionary<(string Layer, MapTileCoord Region), IReadOnlyList<MapFoliageChunk>>();
        foreach (MapFoliageLayer layer in set.Layers)
        {
            var resident = layer.Chunks.ToDictionary(c => c.Key);
            var groups = layer.ChunkDigests.Select(d => new Row(d, MapFoliageStorage.Regions(layer, d.Key, document.TileSize)))
                .GroupBy(r => r.Regions[0]).OrderBy(g => g.Key.Z).ThenBy(g => g.Key.X);
            foreach (var group in groups)
            {
                Row[] rows = group.OrderBy(r => r.Digest.Key.Z).ThenBy(r => r.Digest.Key.X).ToArray();
                MapFoliagePackRef? previous = prior is not null && prior.TryGetPack(layer.Id, group.Key, out var found) ? found : null;
                bool unchanged = previous is not null && SameRows(rows, prior!.ChunksFor(previous));
                bool present = previous is not null && File.Exists(MapFoliageStorageIndex.PathOf(directory, previous));
                MapFoliagePackRef pack;
                if (unchanged && present)
                {
                    // A narrowed domain needs a padding check for any bytes this snapshot does not hold.
                    if (!prior!.PreservesPadding(layer))
                        foreach (Row row in rows) ValidateChunk(layer, Materialize(row));
                    pack = previous!;
                }
                else
                {
                    MapFoliageChunk[] chunks = rows.Select(Materialize).ToArray();
                    foreach (MapFoliageChunk chunk in chunks) ValidateChunk(layer, chunk);
                    byte[] bytes = MapFoliageRegionPack.Encode(chunks);
                    string digest = MapFoliageRegionPack.Digest(bytes);
                    pack = new(layer.Id, group.Key, digest, chunks.Length, bytes.Length);
                    payloads.TryAdd(MapFoliageStorageIndex.RelativePath(pack), bytes);
                }
                packs.Add(pack);
                keep.Add(MapTiledFile.Normalize(MapFoliageStorageIndex.PathOf(directory, pack)));
                for (int i = 0; i < rows.Length; i++)
                    references.Add(new(layer.Id, rows[i].Digest.Key, group.Key, rows[i].Regions, rows[i].Digest.Sha256,
                        pack.PayloadSha256, MapFoliageRegionPack.DataOffset(i)));
            }

            MapFoliageChunk Materialize(Row row)
            {
                if (resident.TryGetValue(row.Digest.Key, out var chunk)) return chunk;
                if (prior is null || !prior.TryGetChunk(layer.Id, row.Digest.Key, out var old) || old.Sha256 != row.Digest.Sha256 ||
                    !prior.TryGetPack(layer.Id, old.OwnerRegion, out var pack))
                    throw new MapDocumentException("foliage advertised density is unavailable, refusing to publish zero.");
                var address = (pack.LayerId, pack.Region);
                if (!decoded.TryGetValue(address, out var chunks))
                {
                    MapFoliageStorage.Require(prior.ReadPack(directory, pack, out var records), pack);
                    chunks = records!; decoded.Add(address, chunks);
                }
                return chunks[(old.DataOffset - 52) / MapFoliageRegionPack.RecordLength];
            }
        }
        var index = new MapFoliageStorageIndex(packs, references, directory, set);
        MapFoliageManifestBudget.Check(set, index);
        return new(index, Array.AsReadOnly(payloads.Select(p => (p.Key, p.Value)).ToArray()), keep)
        { FoliageSha256 = MapFoliageProjection.Digest(set) };
    }

    static bool SameRows(IReadOnlyList<Row> proposed, IReadOnlyList<MapFoliageChunkRef> prior)
    {
        if (proposed.Count != prior.Count) return false;
        for (int i = 0; i < proposed.Count; i++)
            if (proposed[i].Digest.Key != prior[i].Key || proposed[i].Digest.Sha256 != prior[i].Sha256) return false;
        return true;
    }

    static void ValidateChunk(MapFoliageLayer layer, MapFoliageChunk chunk)
    {
        var errors = new List<string>();
        MapFoliageValidation.ValidateChunk(layer, chunk, errors);
        if (errors.Count > 0) throw new MapDocumentException(string.Join("\n", errors));
    }
}
