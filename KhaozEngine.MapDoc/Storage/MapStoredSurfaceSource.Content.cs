using System;
using System.Globalization;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc.Identity;

namespace KhaozEngine.MapDoc.Storage;

public sealed partial class MapStoredSurfaceSource
{
    /// <summary>The authored content digest over the tile files the pinned generation names, one tile at a time.
    /// Each file is read by its pinned name and its content is checked against the pinned tile hash before any
    /// entry reaches the digest. The manifest is never reread and no directory is listed.</summary>
    internal string IdentityContentDigest()
    {
        if (_pinnedTiles.SchemeVersion != MapDocumentHash.SchemeVersion)
            throw new MapDocumentException($"Whole authored identity requires tile hash scheme {MapDocumentHash.SchemeVersion}, " +
                $"the pinned manifest uses {_pinnedTiles.SchemeVersion}.");
        MapDocRegistry registry = MapDocRegistry.CreateDefault();
        var digest = new MapAuthoredContentDigest();
        foreach (MapTileEntry entry in _pinnedTiles.Entries)
        {
            MapTileContent content = PinnedTile(entry, registry);
            if (digest.Add(content.Placements, content.Spawns, content.PlayerSpawns, content.SculptTiles) is not null)
                throw PinnedTileRefusal(MapPatchStatus.Corrupt, entry, null);
        }
        return digest.Finish();
    }

    MapTileContent PinnedTile(MapTileEntry entry, MapDocRegistry registry)
    {
        // The hash names the file, so a value that is not a digest could name a path outside the shard.
        if (entry.Hash is not { Length: 64 } || entry.Hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw PinnedTileRefusal(MapPatchStatus.Corrupt, entry, null);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(MapTileFile.PathOf(_directory, entry.Coord, entry.Hash)); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw PinnedTileRefusal(MapPatchStatus.Missing, entry, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PinnedTileRefusal(MapPatchStatus.Corrupt, entry, ex);
        }
        try
        {
            string file = MapTileFile.FileName(entry.Coord, entry.Hash);
            using var reader = new StreamReader(new MemoryStream(bytes, writable: false), detectEncodingFromByteOrderMarks: true);
            MapTileContent content = MapTileFile.Parse(reader.ReadToEnd(), "pinned tile", entry.Coord, entry.Hash, registry);
            MapTileValidator.Validate(content, "pinned tile", file, _pinnedTiles.TileSize, _sculptCellSize);
            // A tile hash is canonical over the four lists, so the check runs on the parsed lists.
            if (!string.Equals(MapDocumentHash.OfLists(content.Lists, registry), entry.Hash, StringComparison.Ordinal))
                throw new MapDocumentException("tile content does not match its pinned hash");
            return content;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            throw PinnedTileRefusal(MapPatchStatus.Corrupt, entry, ex);
        }
    }

    static MapDocumentException PinnedTileRefusal(MapPatchStatus status, MapTileEntry entry, Exception? inner) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{status} pinned tile ({entry.Coord.X}, {entry.Coord.Z})."), inner!);
}
