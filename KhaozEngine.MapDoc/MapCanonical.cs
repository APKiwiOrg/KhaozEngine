using System;
using System.Buffers;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;

namespace KhaozEngine.MapDoc;

/// <summary>One tile's four content lists, whatever they were sourced from: a bucketed in-memory document, a
/// parsed tile file, or a residency read. Lets the canonical writer serve the hash, the tiled writer and the
/// hash-verification path without any of them needing a <see cref="MapSpatialIndex"/>.</summary>
internal readonly record struct MapTileLists(
    IReadOnlyList<MapPlacement> Placements,
    IReadOnlyList<MapSpawn> Spawns,
    IReadOnlyList<MapPlayerSpawn> PlayerSpawns,
    IReadOnlyList<MapSculptTile> SculptTiles)
{
    internal static MapTileLists Of(MapSpatialIndex index, MapTileCoord tile) => new(
        index.PlacementsIn(tile), index.SpawnsIn(tile), index.PlayerSpawnsIn(tile), index.SculptTilesIn(tile));

    internal bool IsEmpty =>
        Placements.Count == 0 && Spawns.Count == 0 && PlayerSpawns.Count == 0 && SculptTiles.Count == 0;
}

/// <summary>The one place that decides what a document's canonical bytes look like. Shared by
/// <see cref="MapDocumentHash"/> (compact, hashed, never stored) and by the tiled writer (indented, stored,
/// never hashed), so the two can never describe different content: they are the same calls into the same
/// <see cref="Utf8JsonWriter"/> shapes with a different indent setting.</summary>
internal static class MapCanonical
{
    /// <summary>Ordinal id sort, the total order the four bucketed lists are canonicalized in. Ids are
    /// validated unique within a document, so there is no tie to break and no secondary key is needed.</summary>
    internal static readonly Comparison<MapPlacement> ByPlacementId =
        static (a, b) => string.CompareOrdinal(a.Id, b.Id);

    internal static readonly Comparison<MapSpawn> BySpawnId =
        static (a, b) => string.CompareOrdinal(a.Id, b.Id);

    internal static readonly Comparison<MapPlayerSpawn> ByPlayerSpawnId =
        static (a, b) => string.CompareOrdinal(a.Id, b.Id);

    /// <summary>Ascending (tileZ, then tileX), the order <see cref="MapTerrainOverrides.Tiles"/> already
    /// returns.</summary>
    internal static readonly Comparison<MapSculptTile> BySculptTile =
        static (a, b) => a.TileZ != b.TileZ ? a.TileZ.CompareTo(b.TileZ) : a.TileX.CompareTo(b.TileX);

    /// <summary>The globals shared by the manifest FILE and the manifest HASH, in the manifest's declared
    /// field order. Everything either side adds (<c>$schema</c> and <c>displayName</c>, which are excluded
    /// from identity, plus <c>schemeVersion</c> and <c>tiles</c>, which are index bookkeeping) is written by
    /// the caller around this block.</summary>
    internal static void WriteGlobals(Utf8JsonWriter w, MapDocument doc, JsonSerializerOptions options)
    {
        w.WritePropertyName("bounds");
        JsonSerializer.Serialize(w, doc.Bounds, options);
        w.WriteNumber("tileSize", doc.TileSize);
        w.WriteNumber("sculptCellSize", SculptCellSizeOf(doc));
        w.WritePropertyName("terrain");
        JsonSerializer.Serialize(w, doc.Terrain, options);
        w.WritePropertyName("scatterLayers");
        JsonSerializer.Serialize(w, doc.ScatterLayers, options);
        w.WritePropertyName("companionLayers");
        JsonSerializer.Serialize(w, doc.CompanionLayers, options);
        w.WritePropertyName("exclusions");
        JsonSerializer.Serialize(w, doc.Exclusions, options);
        w.WritePropertyName("scatterOverrides");
        JsonSerializer.Serialize(w, doc.ScatterOverrides, options);
        w.WritePropertyName("regions");
        JsonSerializer.Serialize(w, doc.Regions, options);
    }

    /// <summary>A null sculpt block normalizes to <see cref="MapTerrainOverrides.DefaultCellSize"/>: "no
    /// sculpt" and "an empty sculpt block at the default cell size" are the same world, and the monolithic
    /// writer collapses the second onto the first so a round trip through the tiled form is byte-stable.</summary>
    internal static float SculptCellSizeOf(MapDocument doc) =>
        doc.TerrainOverrides?.CellSize ?? MapTerrainOverrides.DefaultCellSize;

    /// <summary>One tile's four content lists, in canonical order. The same call writes the compact bytes the
    /// hash is taken over and the indented bytes that land on disk.</summary>
    internal static void WriteTileBody(Utf8JsonWriter w, in MapTileLists lists,
                                       JsonSerializerOptions options, string? schemaRef)
    {
        w.WriteStartObject();
        // $schema is a file-level annotation, not content: the writer emits it, the reader ignores it, and it
        // never enters the tile hash (the hash path passes null).
        if (schemaRef is not null) w.WriteString("$schema", schemaRef);

        WriteSorted(w, "placements", lists.Placements, ByPlacementId, options);
        WriteSorted(w, "spawns", lists.Spawns, BySpawnId, options);
        WriteSorted(w, "playerSpawns", lists.PlayerSpawns, ByPlayerSpawnId, options);

        var sculpt = new List<MapSculptTile>(lists.SculptTiles);
        sculpt.Sort(BySculptTile);
        w.WriteStartArray("sculpt");
        foreach (MapSculptTile t in sculpt) WriteSculptTile(w, t);
        w.WriteEndArray();

        w.WriteEndObject();
    }

    internal static void WriteSculptTile(Utf8JsonWriter w, MapSculptTile tile)
    {
        w.WriteStartObject();
        w.WriteNumber("tileX", tile.TileX);
        w.WriteNumber("tileZ", tile.TileZ);
        w.WriteStartArray("deltas");
        float[] deltas = tile.Deltas;
        for (int i = 0; i < deltas.Length; i++) w.WriteNumberValue(deltas[i]);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void WriteSorted<T>(Utf8JsonWriter w, string name, IReadOnlyList<T> items, Comparison<T> order,
                               JsonSerializerOptions options)
    {
        var sorted = new List<T>(items);
        sorted.Sort(order);
        w.WriteStartArray(name);
        foreach (T item in sorted) JsonSerializer.Serialize(w, item, options);
        w.WriteEndArray();
    }

    /// <summary>Streams a compact JSON body through a <see cref="Utf8JsonWriter"/> straight into a SHA-256
    /// state and returns the digest as lower hex. No intermediate string or byte array of the body exists at
    /// any point, which is the whole reason the hash replaced "serialize the document, then hash the text".</summary>
    internal static string HashHex(Action<Utf8JsonWriter> body)
    {
        using var sink = new HashingBufferWriter();
        using (var w = new Utf8JsonWriter(sink, new JsonWriterOptions { Indented = false, SkipValidation = true }))
        {
            body(w);
            w.Flush();
        }
        return Convert.ToHexStringLower(sink.GetHashAndReset());
    }

    /// <summary>An <see cref="IBufferWriter{T}"/> that hashes everything advanced through it and keeps
    /// nothing. The buffer is reused from index 0 after every <see cref="Advance"/>, which the
    /// <see cref="IBufferWriter{T}"/> contract allows because advanced bytes belong to the writer.</summary>
    internal sealed class HashingBufferWriter : IBufferWriter<byte>, IDisposable
    {
        readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] _buffer = new byte[8192];

        public void Advance(int count) => _hash.AppendData(_buffer, 0, count);

        public Memory<byte> GetMemory(int sizeHint = 0) => Grow(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => Grow(sizeHint);

        public void Append(ReadOnlySpan<byte> bytes) => _hash.AppendData(bytes);

        public byte[] GetHashAndReset() => _hash.GetHashAndReset();

        public void Dispose() => _hash.Dispose();

        byte[] Grow(int sizeHint)
        {
            if (sizeHint < 1) sizeHint = 1;
            if (_buffer.Length < sizeHint) _buffer = new byte[Math.Max(sizeHint, _buffer.Length * 2)];
            return _buffer;
        }
    }
}
