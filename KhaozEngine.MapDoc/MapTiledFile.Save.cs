using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Serialization;

namespace KhaozEngine.MapDoc;

/// <summary>The tiled write ordering.
/// <para><b>The invariant:</b> at every instant the bytes on disk describe exactly one document, either the
/// complete previous version or the complete new one, never a mixture. The manifest names tiles by
/// coordinate, so if a tile's file name were fixed per coordinate the manifest could not tell old content
/// from new, and any window in which new bytes sit under an old manifest would violate the invariant by
/// construction. Therefore the file name encodes the version, and the canonical hash is already exactly that.
/// Two files with the same name have the same bytes, so a tile write is idempotent and a rename can never
/// destroy anything a manifest needs.</para>
/// <para>"Manifest last" alone is necessary and nowhere near sufficient, which is why every step below is
/// stated rather than implied.</para></summary>
internal static partial class MapTiledFile
{
    internal static void Save(MapDocument doc, string directory, MapDocRegistry registry, MapDocumentSaveOptions? save)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        save ??= new MapDocumentSaveOptions();
        string root = Normalize(directory);

        // 1. Validate, then the four guards.
        IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
        if (errors.Count > 0)
            throw new MapDocumentException("refusing to save an invalid map document:\n  " + string.Join("\n  ", errors));

        if (File.Exists(root))
            throw new MapDocumentException(
                $"{directory}: a file already exists at this path. A tiled document is a directory, remove " +
                "the file or choose a different path.");

        string manifestPath = Path.Combine(root, ManifestName);
        if (doc.Tiles is null && File.Exists(manifestPath))
            throw new MapDocumentException(
                $"{directory}: refusing to write a document with no tile index over an existing tiled document. " +
                "Load it first (MapDocumentFile.LoadTiled) so the write knows which tiles exist.");

        if (doc.Tiles is { IsPartial: true } partial)
        {
            if (partial.SourceDirectory is not { } source || !SamePath(source, root))
                throw new MapDocumentException(
                    $"{directory}: this document is a window onto a larger world and may only be written back to " +
                    $"'{partial.SourceDirectory ?? "(nowhere: the index was built in memory)"}'.");
            if (doc.TileSize != partial.TileSize)
                throw new MapDocumentException(
                    $"{directory}: this document's tileSize ({doc.TileSize}) no longer matches the loaded " +
                    $"window's tileSize ({partial.TileSize}). Retiling rewrites every tile, and a partial " +
                    "save would write the new size over an index built for the old one, so every unloaded tile " +
                    "would silently keep the wrong size. Load the whole world first, then retile.");
        }

        // 2. Bucket and hash.
        MapSpatialIndex spatial = MapSpatialIndex.Build(doc);
        var entries = new List<MapTileEntry>(spatial.OccupiedTiles.Count);
        foreach (MapTileCoord tile in spatial.OccupiedTiles)
            entries.Add(new MapTileEntry(tile, MapDocumentHash.OfTile(spatial, tile, registry), Loaded: true));

        if (doc.Tiles is { IsPartial: true } window)
        {
            GuardMovedContent(spatial, window);
            foreach (MapTileEntry entry in window.Entries)
                if (!entry.Loaded) entries.Add(entry);
        }
        entries.Sort(static (a, b) => a.Coord.Z != b.Coord.Z ? a.Coord.Z.CompareTo(b.Coord.Z) : a.Coord.X.CompareTo(b.Coord.X));

        // 3. Read the previous manifest. The ONLY source of the previous per-tile hashes: never a directory
        //    listing, never a parse of a file name.
        Directory.CreateDirectory(root);
        DeleteQuietly(Path.Combine(root, ManifestTempName));
        PreviousManifest? previous = ReadPrevious(manifestPath);

        // 4. Write changed tiles, at names nothing points at yet.
        JsonSerializerOptions indented = MapDocumentFile.CreateOptions(registry, write: true);
        var touchedShards = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapTileEntry entry in entries)
        {
            if (!entry.Loaded) continue;
            if (previous is { } p && p.Scheme == MapDocumentHash.SchemeVersion
                && p.Hashes.TryGetValue(entry.Coord, out string? old)
                && string.Equals(old, entry.Hash, StringComparison.Ordinal))
                continue;   // unchanged tiles are not written at all

            save.OnStep?.Invoke(MapTiledSaveStep.BeforeTileWrite);
            WriteTile(root, entry.Coord, entry.Hash, MapTileLists.Of(spatial, entry.Coord), indented, save.Durability);
            touchedShards.Add(Normalize(MapTileFile.ShardPath(root, entry.Coord)));
            save.OnStep?.Invoke(MapTiledSaveStep.AfterTileWrite);
        }

        // 5. Commit: the manifest rename, and nothing before this mutated anything live.
        WriteManifest(root, doc, entries, indented, save.Durability);
        save.OnStep?.Invoke(MapTiledSaveStep.BeforeManifestRename);
        File.Move(Path.Combine(root, ManifestTempName), manifestPath, overwrite: true);
        FlushDirectories(root, touchedShards, save.Durability);
        save.OnStep?.Invoke(MapTiledSaveStep.AfterManifestRename);

        // 6. Sweep, after the commit. Skipped when the previous manifest could not be read: deleting files on
        //    the authority of a manifest that failed to parse is how a bad save turns into a lost world.
        if (previous is not null) Sweep(root, entries, save);

        // The document now describes what is on disk. Refreshing the index here is what keeps
        // MapDocumentHash.OfWorld honest on an edited document: it reads stored hashes and never opens a
        // tile file, so a stale index would report the pre-edit world forever.
        doc.Tiles = new MapTileIndex(doc.TileSize, MapDocumentHash.SchemeVersion, root, entries);
    }

    static void GuardMovedContent(MapSpatialIndex spatial, MapTileIndex window)
    {
        foreach (MapTileCoord tile in spatial.OccupiedTiles)
        {
            if (!window.TryGet(tile, out MapTileEntry entry) || entry.Loaded) continue;
            string what = Describe(spatial, tile);
            throw new MapDocumentException(
                $"{what} now falls in tile ({tile.X}, {tile.Z}), which this window did not load. Writing that " +
                "tile would replace its real content with just the moved item. Widen the window (set_window) " +
                "and save again.");
        }
    }

    static string Describe(MapSpatialIndex spatial, MapTileCoord tile)
    {
        foreach (MapPlacement p in spatial.PlacementsIn(tile)) return $"placement '{p.Id}'";
        foreach (MapSpawn s in spatial.SpawnsIn(tile)) return $"spawn '{s.Id}'";
        foreach (MapPlayerSpawn s in spatial.PlayerSpawnsIn(tile)) return $"player spawn '{s.Id}'";
        foreach (MapSculptTile t in spatial.SculptTilesIn(tile)) return $"sculpt tile ({t.TileX}, {t.TileZ})";
        return "content";
    }

    /// <summary>The <c>$schema</c> annotations the writer emits. Relative, so a document directory that
    /// materialized the schemas beside itself resolves them, and absent schemas simply do not resolve rather
    /// than pointing at a URL that must stay alive. Never hashed.</summary>
    const string ManifestSchemaRef = "mapdoc.manifest.schema.json";
    const string TileSchemaRef = "../../mapdoc.tile.schema.json";

    static void WriteTile(string root, MapTileCoord coord, string hash, in MapTileLists lists,
                          JsonSerializerOptions options, MapSaveDurability durability)
    {
        Directory.CreateDirectory(MapTileFile.ShardPath(root, coord));
        string final = MapTileFile.PathOf(root, coord, hash);
        string temp = final + MapTileFile.TempSuffix;
        MapTileLists local = lists;
        WriteThroughTemp(temp, final, durability, w => MapCanonical.WriteTileBody(w, local, options, TileSchemaRef));
    }

    static void WriteManifest(string root, MapDocument doc, List<MapTileEntry> entries,
                              JsonSerializerOptions options, MapSaveDurability durability)
    {
        string temp = Path.Combine(root, ManifestTempName);
        WriteThroughTemp(temp, moveTo: null, durability, w =>
        {
            w.WriteStartObject();
            w.WriteString("$schema", ManifestSchemaRef);
            w.WriteNumber("formatVersion", doc.FormatVersion);
            w.WriteString("id", doc.Id);
            if (!string.IsNullOrEmpty(doc.DisplayName)) w.WriteString("displayName", doc.DisplayName);
            w.WriteNumber("schemeVersion", MapDocumentHash.SchemeVersion);
            MapCanonical.WriteGlobals(w, doc, options);
            w.WriteStartArray("tiles");
            foreach (MapTileEntry entry in entries)
            {
                w.WriteStartObject();
                w.WriteNumber("x", entry.Coord.X);
                w.WriteNumber("z", entry.Coord.Z);
                w.WriteString("hash", entry.Hash);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    static void WriteThroughTemp(string temp, string? moveTo, MapSaveDurability durability,
                                 Action<Utf8JsonWriter> body)
    {
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                body(writer);
                writer.Flush();
            }
            if (durability == MapSaveDurability.PowerFail) stream.Flush(flushToDisk: true);
        }
        if (moveTo is not null) File.Move(temp, moveTo, overwrite: true);
    }

    /// <summary>Best-effort directory fsync, so a rename is durable and not only ordered: the root (the
    /// manifest rename lands there), <c>tiles/</c> (a new shard directory's own creation is itself a rename
    /// into it), and every shard directory that received a tile-file rename this save. Linux and macOS only,
    /// via <see cref="UnixDirectorySync"/>: Windows has no directory-fsync primitive at all and NTFS orders
    /// metadata through its own journal instead, so there the guarantee is per-file flush only, never dressed
    /// up as anything stronger.</summary>
    static void FlushDirectories(string root, IReadOnlyCollection<string> touchedShards, MapSaveDurability durability)
    {
        if (durability != MapSaveDurability.PowerFail) return;
        if (OperatingSystem.IsWindows()) return;
        UnixDirectorySync.Flush(root);
        UnixDirectorySync.Flush(Path.Combine(root, MapTileFile.TilesDirectory));
        foreach (string shard in touchedShards) UnixDirectorySync.Flush(shard);
    }

    static void Sweep(string root, List<MapTileEntry> entries, MapDocumentSaveOptions save)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapTileEntry entry in entries) keep.Add(Normalize(MapTileFile.PathOf(root, entry.Coord, entry.Hash)));

        string tiles = Path.Combine(root, MapTileFile.TilesDirectory);
        if (Directory.Exists(tiles))
            foreach (string file in Directory.EnumerateFiles(tiles, "*", SearchOption.AllDirectories))
                if (file.EndsWith(MapTileFile.TempSuffix, StringComparison.Ordinal) || !keep.Contains(Normalize(file)))
                {
                    save.OnStep?.Invoke(MapTiledSaveStep.DuringSweep);
                    DeleteReporting(file, save);
                }

        DeleteReporting(Path.Combine(root, ManifestTempName), save);
    }

    static void DeleteReporting(string path, MapDocumentSaveOptions save)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            save.OnSweepFailure?.Invoke($"could not delete '{path}': {ex.Message}");
        }
    }

    static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The previous occupied set and per-tile hashes, or null when there is no readable previous
    /// version. Deliberately parses only <c>schemeVersion</c> and <c>tiles</c>: a previous manifest whose
    /// globals no longer validate must not block a save that fixes them.</summary>
    static PreviousManifest? ReadPrevious(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            if (Jsonc.ParseNode(File.ReadAllText(manifestPath)) is not JsonObject root) return null;
            var hashes = new Dictionary<MapTileCoord, string>();
            if (root["tiles"] is JsonArray array)
                foreach (JsonNode? node in array)
                {
                    if (node is not JsonObject o) return null;
                    if (o["x"] is not JsonValue xv || !xv.TryGetValue(out int x)) return null;
                    if (o["z"] is not JsonValue zv || !zv.TryGetValue(out int z)) return null;
                    if (o["hash"]?.GetValue<string>() is not { Length: > 0 } hash) return null;
                    hashes[new MapTileCoord(x, z)] = hash;
                }
            int scheme = root["schemeVersion"] is JsonValue sv && sv.TryGetValue(out int s) ? s : MapDocumentHash.SchemeVersion;
            return new PreviousManifest(scheme, hashes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    sealed record PreviousManifest(int Scheme, Dictionary<MapTileCoord, string> Hashes);
}
