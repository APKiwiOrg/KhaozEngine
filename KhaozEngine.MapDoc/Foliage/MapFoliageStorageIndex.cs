using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KhaozEngine.MapDoc.Foliage;

public sealed record MapFoliagePackRef(string LayerId, MapTileCoord Region, string PayloadSha256, int ChunkCount, int ByteLength);
public sealed record MapFoliageChunkRef(string LayerId, MapFoliageChunkKey Key, MapTileCoord OwnerRegion,
    IReadOnlyList<MapTileCoord> Regions, string Sha256, string PackSha256, int DataOffset);
public enum MapFoliageReadStatus { Found, Absent, Unavailable, Invalid, CapacityExceeded }
public sealed record MapFoliageChunkRead(MapFoliageReadStatus Status, MapFoliageChunk? Chunk);

/// <summary>One immutable manifest generation. Advertised keys, pack references and resident bytes are copied at
/// construction. Reads never change residency, and a missing pinned file never becomes an absent logical chunk.</summary>
public sealed class MapFoliageStorageIndex
{
    readonly Dictionary<(string Layer, MapFoliageChunkKey Key), MapFoliageChunkRef> _byKey;
    readonly Dictionary<(string Layer, MapTileCoord Region), MapFoliagePackRef> _byRegion;
    readonly Dictionary<(string Layer, MapTileCoord Region), MapFoliageChunkRef[]> _packChunks;
    readonly Dictionary<(string Layer, MapFoliageChunkKey Key), MapFoliageChunk> _resident = new();
    readonly string? _directory;
    readonly Dictionary<string, (int Width, int Depth)> _extents = new(StringComparer.Ordinal);

    internal MapFoliageStorageIndex(IReadOnlyList<MapFoliagePackRef> packs, IReadOnlyList<MapFoliageChunkRef> chunks,
        string? directory = null, MapFoliageSet? resident = null)
    {
        if (chunks.Count > MapFoliagePayloadLimits.MaxDocumentChunks || packs.Count > MapFoliagePayloadLimits.MaxDocumentChunks)
            throw new MapDocumentException("foliage storage exceeds document chunk capacity.");
        Packs = Array.AsReadOnly(packs.OrderBy(p => p.LayerId, StringComparer.Ordinal).ThenBy(p => p.Region.Z).ThenBy(p => p.Region.X).ToArray());
        Chunks = Array.AsReadOnly(chunks.Select(c => c with { Regions = Array.AsReadOnly(c.Regions.ToArray()) })
            .OrderBy(c => c.LayerId, StringComparer.Ordinal).ThenBy(c => c.Key.Z).ThenBy(c => c.Key.X).ToArray());
        _directory = directory is null ? null : MapTiledFile.Normalize(directory);
        _byRegion = new(); _byKey = new();
        foreach (MapFoliagePackRef pack in Packs)
        {
            MapFoliageRegionPack.RequireDigest(pack.PayloadSha256);
            if (string.IsNullOrEmpty(pack.LayerId) || pack.ByteLength != MapFoliageRegionPack.ByteLength(pack.ChunkCount) ||
                !_byRegion.TryAdd((pack.LayerId, pack.Region), pack))
                throw new MapDocumentException("foliage: invalid or duplicate region pack reference.");
        }
        foreach (MapFoliageChunkRef chunk in Chunks)
        {
            MapFoliageRegionPack.RequireDigest(chunk.Sha256); MapFoliageRegionPack.RequireDigest(chunk.PackSha256);
            if (chunk.Key.X is < 0 or > 32767 || chunk.Key.Z is < 0 or > 32767 || chunk.Regions.Count is < 1 or > 4096 ||
                !_byKey.TryAdd((chunk.LayerId, chunk.Key), chunk) ||
                !_byRegion.TryGetValue((chunk.LayerId, chunk.OwnerRegion), out MapFoliagePackRef? pack) || pack.PayloadSha256 != chunk.PackSha256)
                throw new MapDocumentException("foliage: invalid or duplicate chunk reference.");
        }
        _packChunks = Chunks.GroupBy(c => (c.LayerId, c.OwnerRegion)).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (MapFoliagePackRef pack in Packs)
        {
            if (!_packChunks.TryGetValue((pack.LayerId, pack.Region), out MapFoliageChunkRef[]? rows) || rows.Length != pack.ChunkCount)
                throw new MapDocumentException("foliage pack count disagrees with advertised chunk references.");
            for (int i = 0; i < rows.Length; i++)
                if (rows[i].DataOffset != MapFoliageRegionPack.DataOffset(i))
                    throw new MapDocumentException("foliage chunk data offset disagrees with canonical pack order.");
        }
        if (resident is null) return;
        foreach (MapFoliageLayer layer in resident.Layers) _extents.Add(layer.Id, (layer.Width, layer.Depth));
        foreach (MapFoliageLayer layer in resident.Layers)
            foreach (MapFoliageChunk chunk in layer.Chunks)
            {
                if (!_byKey.TryGetValue((layer.Id, chunk.Key), out MapFoliageChunkRef? row) || row.Sha256 != MapFoliageLayer.Digest(chunk))
                    throw new MapDocumentException("foliage resident chunk disagrees with its advertised digest.");
                _resident.Add((layer.Id, chunk.Key), chunk);
            }
    }

    public IReadOnlyList<MapFoliagePackRef> Packs { get; }
    public IReadOnlyList<MapFoliageChunkRef> Chunks { get; }
    public bool IsPartial => _resident.Count < _byKey.Count;

    public MapFoliageChunkRead Read(string layerId, MapFoliageChunkKey key)
    {
        if (!_byKey.TryGetValue((layerId, key), out MapFoliageChunkRef? row)) return new(MapFoliageReadStatus.Absent, null);
        if (_resident.TryGetValue((layerId, key), out MapFoliageChunk? resident)) return new(MapFoliageReadStatus.Found, resident);
        if (_directory is null) return new(MapFoliageReadStatus.Unavailable, null);
        MapFoliageReadStatus status = ReadPack(_directory, _byRegion[(layerId, row.OwnerRegion)], out var chunks);
        return status == MapFoliageReadStatus.Found ? new(status, chunks![(row.DataOffset - 52) / MapFoliageRegionPack.RecordLength]) : new(status, null);
    }

    internal IReadOnlyList<MapFoliageChunkRef> ChunksFor(MapFoliagePackRef pack) => _packChunks[(pack.LayerId, pack.Region)];
    internal bool TryGetPack(string layer, MapTileCoord region, out MapFoliagePackRef pack) => _byRegion.TryGetValue((layer, region), out pack!);
    internal bool TryGetChunk(string layer, MapFoliageChunkKey key, out MapFoliageChunkRef chunk) => _byKey.TryGetValue((layer, key), out chunk!);
    internal bool PreservesPadding(MapFoliageLayer layer) => _extents.TryGetValue(layer.Id, out var extent) &&
        layer.Width >= extent.Width && layer.Depth >= extent.Depth;

    internal MapFoliageStorageIndex WithResidency(string directory, MapFoliageSet? set) => new(Packs, Chunks, directory, set);
    internal bool WasResident(string layer, MapFoliageChunkKey key) => _resident.ContainsKey((layer, key));

    internal MapFoliageReadStatus ReadPack(string directory, MapFoliagePackRef pack, out IReadOnlyList<MapFoliageChunk>? chunks)
    {
        chunks = null;
        if (MapFoliageStorageWork.Current is { } work) work.PackReads++;
        try
        {
            using var stream = new FileStream(PathOf(directory, pack), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MapFoliageRegionPack.MaxByteLength) return MapFoliageReadStatus.CapacityExceeded;
            if (stream.Length != pack.ByteLength) return MapFoliageReadStatus.Invalid;
            byte[] bytes = new byte[pack.ByteLength]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1 || MapFoliageRegionPack.Digest(bytes) != pack.PayloadSha256) return MapFoliageReadStatus.Invalid;
            var decoded = MapFoliageRegionPack.Decode(bytes);
            MapFoliageChunkRef[] rows = _packChunks[(pack.LayerId, pack.Region)];
            if (decoded.Count != rows.Length) return MapFoliageReadStatus.Invalid;
            for (int i = 0; i < decoded.Count; i++)
                if (decoded[i].Key != rows[i].Key || MapFoliageLayer.Digest(decoded[i]) != rows[i].Sha256)
                    return MapFoliageReadStatus.Invalid;
            chunks = decoded;
            return MapFoliageReadStatus.Found;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return MapFoliageReadStatus.Unavailable; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MapDocumentException) { return MapFoliageReadStatus.Invalid; }
    }

    internal static string RelativePath(MapFoliagePackRef pack)
    {
        if (MapFoliageStorageWork.Current is { } work) work.PackPathFormats++;
        return FormattableString.Invariant($"tiles/foliage/r_{pack.Region.X}_{pack.Region.Z}/{pack.PayloadSha256}.pack");
    }
    internal static string PathOf(string directory, MapFoliagePackRef pack) => Path.Combine(directory, RelativePath(pack));
}
