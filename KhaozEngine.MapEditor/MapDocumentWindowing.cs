using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>The shared "how big is too big to whole-load" policy for a tiled map document. Both
/// <see cref="MapEditorScene"/> and <c>ke-mapedit</c>'s <c>MapEditSession</c> open a document through this, so
/// the interactive editor and the MCP tool can never disagree about when a world is too large to load whole.
/// <para>Below <c>wholeWorldTileLimit</c> occupied tiles a tiled document loads WHOLE, exactly like a
/// monolithic one. Above it, <see cref="Load"/> opens a WINDOW instead: the manifest (cheap, always read) plus
/// only the tiles inside a square centered on the document bounds' midpoint, radius
/// <c>windowRadius</c> tiles either side. Every world small enough to author today keeps loading whole. A
/// world that would have paid the whole-load cost the tiled format exists to remove instead opens a bounded
/// slice of itself.</para>
/// <para>The anchor is the document bounds' center, not "the first enabled player spawn" the design doc names
/// as the ideal: a player spawn is per-tile content (one of the four point-shaped lists a tile file carries),
/// so finding one before any tile is read is the exact chicken-and-egg problem windowing exists to avoid, and
/// the interactive editor's per-session camera bookmarks (<see cref="MapEditorScene"/>'s <c>_bookmarks</c>)
/// are session-only and never persisted, so there is nothing to restore at open time either. The bounds
/// center is always known from the manifest alone and is a reasonable place to start editing.</para></summary>
public static class MapDocumentWindowing
{
    /// <summary>Default occupied-tile ceiling below which a tiled document loads whole.</summary>
    public const int DefaultWholeWorldTileLimit = 512;

    /// <summary>Default tile radius either side of the window center when a document loads windowed.</summary>
    public const int DefaultEditorWindowRadius = 2;

    /// <summary>Loads <paramref name="path"/>. A monolithic file, a nonexistent path, or a tiled directory at
    /// or under <paramref name="wholeWorldTileLimit"/> occupied tiles loads WHOLE (dispatches to
    /// <see cref="MapDocumentFile.Load"/> / <see cref="MapDocumentFile.LoadTiled(string, MapDocumentLoadOptions?)"/>).
    /// A tiled directory over the limit loads WINDOWED, centered on the tile containing the document bounds'
    /// midpoint. <paramref name="windowed"/> and <paramref name="window"/> report which happened, so a caller
    /// can show the window extent to the user.</summary>
    public static MapDocument Load(string path, MapDocumentLoadOptions options,
        int wholeWorldTileLimit, int windowRadius, out bool windowed, out MapTileRect? window)
    {
        windowed = false;
        window = null;

        if (MapDocumentFile.DetectForm(path) != MapDocumentForm.Tiled)
            return MapDocumentFile.Load(path, options);

        int occupied;
        MapDocument manifest;
        using (MapDocumentSource peek = MapDocumentSource.OpenTiled(path, options))
        {
            occupied = peek.Tiles.Entries.Count;
            manifest = peek.Manifest;
        }

        if (occupied <= wholeWorldTileLimit)
            return MapDocumentFile.LoadTiled(path, options);

        MapTileCoord center = MapTileGrid.CoordOf(
            (manifest.Bounds.MinX + manifest.Bounds.MaxX) * 0.5f,
            (manifest.Bounds.MinZ + manifest.Bounds.MaxZ) * 0.5f,
            manifest.TileSize);
        var rect = new MapTileRect(
            new MapTileCoord(center.X - windowRadius, center.Z - windowRadius),
            new MapTileCoord(center.X + windowRadius, center.Z + windowRadius));

        windowed = true;
        window = rect;
        return MapDocumentFile.LoadTiled(path, rect, options);
    }
}
