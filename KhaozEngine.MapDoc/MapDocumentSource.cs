using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc;

/// <summary>One tile's parsed content. Immutable once handed out, INCLUDING the delta arrays inside
/// <see cref="SculptTiles"/>: a reader hands out the same arrays it parsed, and <c>TerrainSculpt</c> stores
/// them by reference, so a consumer that wants to edit a streamed tile's deltas clones first (exactly as the
/// editor's sculpt stroke already does). <c>IReadOnlyList</c> of a mutable element type cannot express that,
/// so it is written here instead.</summary>
public sealed class MapTileContent
{
    internal MapTileContent(MapTileCoord coord,
                            IReadOnlyList<MapPlacement> placements,
                            IReadOnlyList<MapSpawn> spawns,
                            IReadOnlyList<MapPlayerSpawn> playerSpawns,
                            IReadOnlyList<MapSculptTile> sculptTiles)
    {
        Coord = coord;
        Placements = placements;
        Spawns = spawns;
        PlayerSpawns = playerSpawns;
        SculptTiles = sculptTiles;
    }

    public MapTileCoord Coord { get; }
    public IReadOnlyList<MapPlacement> Placements { get; }
    public IReadOnlyList<MapSpawn> Spawns { get; }
    public IReadOnlyList<MapPlayerSpawn> PlayerSpawns { get; }
    public IReadOnlyList<MapSculptTile> SculptTiles { get; }

    /// <summary>True when the tile carries nothing, so it drops out of the occupied set on the next save.</summary>
    public bool IsEmpty => Lists.IsEmpty;

    internal MapTileLists Lists => new(Placements, Spawns, PlayerSpawns, SculptTiles);
}

/// <summary>Reads tiles on demand. Two sources, one type, so a caller works against a tiled directory and
/// against an in-memory whole document alike. The second is what lets a game adopt on-demand tile reads
/// before it converts its world to the tiled form, and what keeps dungeons on the monolithic form
/// forever.</summary>
public sealed class MapDocumentSource : IDisposable
{
    readonly string? _directory;
    readonly MapSpatialIndex? _spatial;
    readonly MapDocumentLoadOptions _options;
    readonly float _sculptCellSize;

    MapDocumentSource(MapDocument manifest, MapTileIndex tiles, string? directory,
                      MapSpatialIndex? spatial, MapDocumentLoadOptions options)
    {
        Manifest = manifest;
        Tiles = tiles;
        _directory = directory;
        _spatial = spatial;
        _options = options;
        _sculptCellSize = MapCanonical.SculptCellSizeOf(manifest);
    }

    /// <summary>Opens a tiled directory: reads and validates <c>map.json</c> and nothing else. No tile file is
    /// touched until <see cref="ReadTile"/> asks for one.</summary>
    public static MapDocumentSource OpenTiled(string directory, MapDocumentLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        options ??= new MapDocumentLoadOptions();
        MapDocument manifest = MapTiledFile.ReadManifest(directory, options, out MapTileIndex index);
        manifest.Tiles = index;
        return new MapDocumentSource(manifest, index, directory, spatial: null, options);
    }

    /// <summary>Wraps a whole in-memory document, bucketing its point content so the same tile-at-a-time API
    /// works against a monolithic world. Hashes every occupied tile up front, which is what makes
    /// <see cref="Tiles"/> a real index rather than a coordinate list.</summary>
    public static MapDocumentSource FromDocument(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var options = new MapDocumentLoadOptions();
        MapSpatialIndex spatial = MapSpatialIndex.Build(doc);
        var entries = new List<MapTileEntry>(spatial.OccupiedTiles.Count);
        foreach (MapTileCoord tile in spatial.OccupiedTiles)
            entries.Add(new MapTileEntry(tile, MapDocumentHash.OfTile(spatial, tile, options.Registry), Loaded: true));

        MapDocument manifest = MapTiledFile.GlobalsOnly(doc);
        var index = new MapTileIndex(doc.TileSize, MapDocumentHash.SchemeVersion, sourceDirectory: null, entries);
        manifest.Tiles = index;
        return new MapDocumentSource(manifest, index, directory: null, spatial, options);
    }

    /// <summary>The globals: bounds, terrain, scatter and companion layers, shapes. Fully populated, with the
    /// four point-shaped lists empty.</summary>
    public MapDocument Manifest { get; }

    /// <summary>The occupied-tile index. Entries read from a directory are all unloaded, because a source
    /// holds nothing until it is asked.</summary>
    public MapTileIndex Tiles { get; }

    /// <summary>Reads, parses and VALIDATES one tile. Pure and free of shared mutable state, so a caller may
    /// run it on a worker thread.</summary>
    /// <exception cref="MapDocumentException">The index does not mark the tile occupied, the file cannot be
    /// read, or the tile fails per-tile validation.</exception>
    public MapTileContent ReadTile(MapTileCoord coord)
    {
        if (!Tiles.TryGet(coord, out MapTileEntry entry))
            throw new MapDocumentException(
                $"{_directory ?? "(in memory)"}: tile ({coord.X}, {coord.Z}) is not in the occupied-tile index.");

        if (_spatial is not null)
        {
            MapTileLists lists = MapTileLists.Of(_spatial, coord);
            var content = new MapTileContent(coord, lists.Placements, lists.Spawns, lists.PlayerSpawns, lists.SculptTiles);
            MapTileValidator.Validate(content, "(in memory)", MapTileFile.FileName(coord, entry.Hash),
                                      Tiles.TileSize, _sculptCellSize);
            return content;
        }

        return MapTileFile.Read(_directory!, coord, entry.Hash, _options, Tiles.TileSize, _sculptCellSize);
    }

    /// <summary>Nothing is held open between reads, so disposal is a no-op today. It is on the type because a
    /// source is the natural owner of a future cache or handle, and a consumer that already disposes will not
    /// need changing when one arrives.</summary>
    public void Dispose() { }
}
