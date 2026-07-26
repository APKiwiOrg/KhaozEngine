using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KhaozEngine.Serialization;

namespace KhaozEngine.MapDoc;

/// <summary>Names and parses the per-tile files of a tiled document.
/// <para>A tile file is CONTENT-ADDRESSED: its name carries the signed tile coordinate verbatim followed by
/// that tile's full canonical SHA-256 in lower hex. That is what makes the manifest rename the document's
/// only mutation, so a crash at any instant leaves a document that is entirely the old version or entirely
/// the new one. The digest is never shortened, because a truncated digest reintroduces the overwrite hazard
/// at low probability, and a low-probability data-loss path inside a save routine is worse than a
/// 77-character file name nobody types.</para>
/// <para>Every integer in a file name is formatted with <see cref="CultureInfo.InvariantCulture"/>, and no
/// code path anywhere parses an integer back out of a file name: the manifest is the sole authority on which
/// tiles exist, what they hash to, and therefore what each one is called. Under ICU, <c>sv-SE</c> and
/// <c>fi-FI</c> format a negative integer with U+2212 MINUS SIGN rather than U+002D HYPHEN-MINUS, so a world
/// with any negative tile coordinate would otherwise write differently named files on a Swedish
/// workstation.</para></summary>
internal static class MapTileFile
{
    internal const string TilesDirectory = "tiles";
    internal const string TempSuffix = ".tmp";

    /// <summary>Shard directory for a tile: <c>tile &gt;&gt; 4</c>, an arithmetic shift so it floors for
    /// negatives. Caps a directory at 256 tile coordinates. A filesystem and git nicety, never a load unit:
    /// nothing ever reads a shard.</summary>
    internal static string ShardName(MapTileCoord c) =>
        string.Create(CultureInfo.InvariantCulture, $"s_{c.X >> 4}_{c.Z >> 4}");

    internal static string FileName(MapTileCoord c, string hash) =>
        string.Create(CultureInfo.InvariantCulture, $"t_{c.X}_{c.Z}.{hash}.json");

    internal static string ShardPath(string directory, MapTileCoord c) =>
        Path.Combine(directory, TilesDirectory, ShardName(c));

    internal static string PathOf(string directory, MapTileCoord c, string hash) =>
        Path.Combine(ShardPath(directory, c), FileName(c, hash));

    /// <summary>Reads, parses and validates one tile file. Loud-fail, like every other map-document read:
    /// dev-authored content fails a boot rather than being quarantined.</summary>
    internal static MapTileContent Read(string directory, MapTileCoord coord, string hash,
                                        MapDocumentLoadOptions options, float tileSize, float sculptCellSize)
    {
        string path = PathOf(directory, coord, hash);
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MapDocumentException(
                $"{directory}: tile ({coord.X}, {coord.Z}) at '{FileName(coord, hash)}' cannot be read. {ex.Message}", ex);
        }

        MapTileContent content = Parse(json, directory, coord, hash, options.Registry);
        MapTileValidator.Validate(content, directory, FileName(coord, hash), tileSize, sculptCellSize);

        if (options.VerifyTileHashes)
        {
            string actual = MapDocumentHash.OfLists(content.Lists, options.Registry);
            if (!string.Equals(actual, hash, StringComparison.Ordinal))
                throw new MapDocumentException(
                    $"{directory}: tile ({coord.X}, {coord.Z}) hashes to {actual} but the manifest names it {hash}.");
        }
        return content;
    }

    internal static MapTileContent Parse(string json, string where, MapTileCoord coord, string hash,
                                         MapDocRegistry registry)
    {
        MapTileFileDoc dto;
        try
        {
            JsonNode? node = Jsonc.ParseNode(json);
            if (node is not JsonObject)
                throw new MapDocumentException($"{where}: tile ({coord.X}, {coord.Z}) root must be a JSON object.");
            dto = node.Deserialize<MapTileFileDoc>(MapDocumentFile.CreateOptions(registry, write: false))
                ?? throw new MapDocumentException($"{where}: tile ({coord.X}, {coord.Z}) deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new MapDocumentException(
                $"{where}: tile ({coord.X}, {coord.Z}) at '{FileName(coord, hash)}' is invalid JSON. {ex.Message}", ex);
        }

        var sculpt = new List<MapSculptTile>(dto.Sculpt.Count);
        foreach (MapSculptTileDoc t in dto.Sculpt)
        {
            try { sculpt.Add(new MapSculptTile(t.TileX, t.TileZ, t.Deltas)); }
            catch (ArgumentException ex)
            {
                throw new MapDocumentException(
                    $"{where}: tile ({coord.X}, {coord.Z}) in '{FileName(coord, hash)}': sculpt tile " +
                    $"({t.TileX}, {t.TileZ}): {ex.Message}", ex);
            }
        }
        return new MapTileContent(coord, dto.Placements, dto.Spawns, dto.PlayerSpawns, sculpt);
    }
}

/// <summary>The on-disk shape of a tile file: an optional <c>$schema</c> annotation plus exactly four lists,
/// and nothing else. <c>$schema</c> is deserialized only so the closed-shape reader does not trip over it;
/// nothing reads the value and it never enters the tile hash.</summary>
internal sealed class MapTileFileDoc
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    public List<MapPlacement> Placements { get; set; } = new();
    public List<MapSpawn> Spawns { get; set; } = new();
    public List<MapPlayerSpawn> PlayerSpawns { get; set; } = new();
    public List<MapSculptTileDoc> Sculpt { get; set; } = new();
}

/// <summary>One sculpt tile as it sits in a tile file, the same <c>{ tileX, tileZ, deltas[] }</c> shape the
/// monolithic <c>terrainOverrides.tiles</c> entries use.</summary>
internal sealed class MapSculptTileDoc
{
    public int TileX { get; set; }
    public int TileZ { get; set; }
    public float[] Deltas { get; set; } = Array.Empty<float>();
}
