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
    float _sculptCellSize;

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
    /// <see cref="Tiles"/> a real index rather than a coordinate list.
    /// <para>The bucketing happens ONCE, right here: like a tiled directory's index frozen at
    /// <see cref="OpenTiled"/>, this source's view of <paramref name="doc"/> is a snapshot from the moment of
    /// this call, not a live view over it. Mutating <paramref name="doc"/>'s lists afterward is invisible to
    /// this source, and <see cref="Refresh"/> is a no-op here (there is no <c>map.json</c> to re-read) rather
    /// than a fix for it - build a new source over the updated document instead.</para></summary>
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
    /// four point-shaped lists empty. Swapped by <see cref="Refresh"/> for a directory-backed source.</summary>
    public MapDocument Manifest { get; private set; }

    /// <summary>The occupied-tile index. Entries read from a directory are all unloaded, because a source
    /// holds nothing until it is asked. Swapped by <see cref="Refresh"/> for a directory-backed source, so a
    /// consumer that reads THIS property fresh on every call (as <see cref="MapResidencyGate"/> does) sees an
    /// occupancy change made after this source was opened, once <see cref="Refresh"/> has been called.</summary>
    public MapTileIndex Tiles { get; private set; }

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
            // The spatial index's buckets are the CALLER's own live document objects, bucketed once and never
            // copied since (MapSpatialIndex is itself a frozen snapshot, see Refresh's remarks). Handing those
            // straight out would break the immutability MapTileContent promises: a placement is a mutable class
            // and a sculpt tile's Deltas array is a mutable float[], so a consumer that later mutates the
            // ORIGINAL document (or something like TerrainSculpt.With, which stores a delta array by reference)
            // would silently change content this source already served as final. OpenTiled never has this
            // problem - MapTileFile.Read parses fresh objects off disk on every call - so only this branch
            // clones.
            MapTileLists lists = MapTileLists.Of(_spatial, coord);
            var content = new MapTileContent(coord, ClonePlacements(lists.Placements), lists.Spawns,
                                             lists.PlayerSpawns, CloneSculptTiles(lists.SculptTiles));
            MapTileValidator.Validate(content, "(in memory)", MapTileFile.FileName(coord, entry.Hash),
                                      Tiles.TileSize, _sculptCellSize);
            return content;
        }

        return MapTileFile.Read(_directory!, coord, entry.Hash, _options, Tiles.TileSize, _sculptCellSize);
    }

    static List<MapPlacement> ClonePlacements(IReadOnlyList<MapPlacement> source)
    {
        var clones = new List<MapPlacement>(source.Count);
        foreach (MapPlacement p in source)
            clones.Add(new MapPlacement
            {
                Id = p.Id, Kind = p.Kind, X = p.X, Z = p.Z, Y = p.Y, Yaw = p.Yaw, Scale = p.Scale,
                Tags = new List<string>(p.Tags),
            });
        return clones;
    }

    static List<MapSculptTile> CloneSculptTiles(IReadOnlyList<MapSculptTile> source)
    {
        var clones = new List<MapSculptTile>(source.Count);
        foreach (MapSculptTile t in source)
            clones.Add(new MapSculptTile(t.TileX, t.TileZ, (float[])t.Deltas.Clone()));
        return clones;
    }

    /// <summary>Re-reads <c>map.json</c> and atomically swaps <see cref="Manifest"/> and <see cref="Tiles"/> for
    /// a freshly parsed pair, picking up whatever an external writer (the editor, a generation tool) did to the
    /// directory since this source was opened or last refreshed: a tile added, removed, or re-saved.
    /// Content-addressed tile files change name on every edit, so without this the OLD index still names the
    /// OLD filename and <see cref="ReadTile"/> would either throw (the stale file was swept) or quietly keep
    /// serving stale content (it was not).
    /// <para>This is the CONSUMER's signal that the on-disk document changed outside this process - residency
    /// does not poll for it on its own. <see cref="MapTileResidency.Invalidate"/> calls this before it re-reads
    /// a tile, and <see cref="MapResidencyGate"/> reads <see cref="Tiles"/> fresh on every
    /// <c>CanBuild</c> call, so a tile that became newly occupied after this source was opened is picked up too,
    /// the moment a consumer calls this after the external save.</para>
    /// <para>A no-op for a source built with <see cref="FromDocument"/>: there is no <c>map.json</c> backing an
    /// in-memory document to re-read. See the caveat on <see cref="FromDocument"/> - its point-content buckets
    /// are ALSO a frozen snapshot, just for a different reason, and this method cannot refresh them.</para></summary>
    /// <exception cref="MapDocumentException">The manifest cannot be read or fails validation.</exception>
    public void Refresh()
    {
        if (_directory is null) return;
        MapDocument manifest = MapTiledFile.ReadManifest(_directory, _options, out MapTileIndex index);
        manifest.Tiles = index;
        Manifest = manifest;
        Tiles = index;
        _sculptCellSize = MapCanonical.SculptCellSizeOf(manifest);
    }

    /// <summary>Nothing is held open between reads, so disposal is a no-op today. It is on the type because a
    /// source is the natural owner of a future cache or handle, and a consumer that already disposes will not
    /// need changing when one arrives.</summary>
    public void Dispose() { }
}
