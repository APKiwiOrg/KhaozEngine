using System;
using System.Linq;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>The shared "how big is too big to whole-load" policy for a tiled map document. Both
/// <see cref="MapEditorScene"/> and <c>ke-mapedit</c>'s <c>MapEditSession</c> open a document through this, so
/// the interactive editor and the MCP tool can never disagree about when a world is too large to load whole.
/// <para>Below <c>wholeWorldTileLimit</c> occupied tiles a tiled document loads WHOLE, exactly like a
/// monolithic one. Above it, <c>Load</c> opens a WINDOW instead: the manifest (cheap, always read) plus
/// only the tiles inside a square centered on a nearby enabled player spawn, radius
/// <c>windowRadius</c> tiles either side. Every world small enough to author today keeps loading whole. A
/// world that would have paid the whole-load cost the tiled format exists to remove instead opens a bounded
/// slice of itself.</para>
/// <para>The spawn search reads a bounded number of occupied tiles nearest the bounds center. If none of
/// those tiles contains an enabled player spawn, the window falls back to the bounds center. Camera bookmarks
/// remain session-only.</para></summary>
public static class MapDocumentWindowing
{
    /// <summary>Default occupied-tile ceiling below which a tiled document loads whole.</summary>
    public const int DefaultWholeWorldTileLimit = 512;

    /// <summary>Default tile radius either side of the window center when a document loads windowed.</summary>
    public const int DefaultEditorWindowRadius = 2;

    /// <summary>Maximum occupied tiles read while looking for an enabled player spawn before opening a window.</summary>
    public const int DefaultPlayerSpawnSearchTileLimit = 32;

    /// <summary>Loads <paramref name="path"/>. A monolithic file, a nonexistent path, or a tiled directory at
    /// or under <paramref name="wholeWorldTileLimit"/> occupied tiles loads WHOLE (dispatches to
    /// <see cref="MapDocumentFile.Load"/> / <see cref="MapDocumentFile.LoadTiled(string, MapDocumentLoadOptions?)"/>).
    /// A tiled directory over the limit loads WINDOWED, using a bounded player-spawn search with the bounds'
    /// midpoint as fallback. <paramref name="windowed"/> and <paramref name="window"/> report which happened, so a caller
    /// can show the window extent to the user.</summary>
    public static MapDocument Load(string path, MapDocumentLoadOptions options,
        int wholeWorldTileLimit, int windowRadius, out bool windowed, out MapTileRect? window)
        => Load(path, options, wholeWorldTileLimit, windowRadius, DefaultPlayerSpawnSearchTileLimit,
            out windowed, out window);

    /// <summary>Loads a document with an explicit player-spawn search budget. Zero keeps the bounds-center
    /// anchor without reading any search tiles. The budget excludes tiles read to populate the final window.
    /// Occupied tiles are searched by squared distance from the bounds-center tile, then Z and X. Within a
    /// tile, the first enabled spawn in ordinal ID order wins. Whole loads do not run the search.</summary>
    public static MapDocument Load(string path, MapDocumentLoadOptions options,
        int wholeWorldTileLimit, int windowRadius, int playerSpawnSearchTileLimit,
        out bool windowed, out MapTileRect? window)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(playerSpawnSearchTileLimit);
        windowed = false;
        window = null;

        if (MapDocumentFile.DetectForm(path) != MapDocumentForm.Tiled)
            return MapDocumentFile.Load(path, options);

        using MapDocumentSource source = MapDocumentSource.OpenTiled(path, options);
        if (source.Tiles.Entries.Count <= wholeWorldTileLimit)
            return MapDocumentFile.LoadTiled(path, options);

        MapDocument manifest = source.Manifest;
        MapTileCoord center = MapTileGrid.CoordOf(
            (manifest.Bounds.MinX + manifest.Bounds.MaxX) * 0.5f,
            (manifest.Bounds.MinZ + manifest.Bounds.MaxZ) * 0.5f,
            manifest.TileSize);
        center = FindSpawnTile(source, center, playerSpawnSearchTileLimit);
        var rect = new MapTileRect(
            new MapTileCoord(center.X - windowRadius, center.Z - windowRadius),
            new MapTileCoord(center.X + windowRadius, center.Z + windowRadius));

        windowed = true;
        window = rect;
        return MapDocumentFile.LoadTiled(path, rect, options);
    }

    static MapTileCoord FindSpawnTile(MapDocumentSource source, MapTileCoord fallback, int budget)
    {
        foreach (MapTileEntry entry in MapWindowSpawnSearch.Nearest(source.Tiles.Entries, fallback, budget))
        {
            MapPlayerSpawn? spawn = source.ReadTile(entry.Coord).PlayerSpawns
                .Where(spawn => spawn.Enabled).OrderBy(spawn => spawn.Id, StringComparer.Ordinal).FirstOrDefault();
            if (spawn is not null) return entry.Coord;
        }
        return fallback;
    }

}
