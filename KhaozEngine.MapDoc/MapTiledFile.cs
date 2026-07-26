using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Serialization;

namespace KhaozEngine.MapDoc;

/// <summary>The tiled on-disk form: a directory holding <c>map.json</c> (the root manifest, and the ONLY file
/// a save ever mutates) plus content-addressed tile files under <c>tiles/s_&lt;sx&gt;_&lt;sz&gt;/</c>.
/// <para>Which form a document is in is decided by the PATH, not by a flag inside it: a flag would be
/// redundant with the path and could disagree with it.</para>
/// <para>This half reads and verifies. <c>MapTiledFile.Save.cs</c> writes.</para></summary>
internal static partial class MapTiledFile
{
    internal const string ManifestName = "map.json";
    internal const string ManifestTempName = ManifestName + MapTileFile.TempSuffix;

    /// <summary>Reads and validates <c>map.json</c>, returning the globals as a document (its four
    /// point-shaped lists empty) and the occupied-tile index with every entry marked unloaded.</summary>
    internal static MapDocument ReadManifest(string directory, MapDocumentLoadOptions options, out MapTileIndex index)
    {
        string path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path))
            throw new MapDocumentException(
                $"{directory}: not a tiled map document (no {ManifestName}). A directory without a manifest has no form.");

        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MapDocumentException($"{path}: cannot read map manifest. {ex.Message}", ex);
        }

        JsonObject root;
        try
        {
            root = Jsonc.ParseNode(json) as JsonObject
                ?? throw new MapDocumentException($"{path}: manifest root must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new MapDocumentException($"{path}: invalid JSON. {ex.Message}", ex);
        }

        root = MapDocumentFile.Migrate(root, options, path);

        int schemeVersion = ReadInt(root, "schemeVersion") ?? MapDocumentHash.SchemeVersion;
        float sculptCellSize = ReadFloat(root, "sculptCellSize") ?? MapTerrainOverrides.DefaultCellSize;
        List<MapTileEntry> entries = ReadTileEntries(root, path);

        root.Remove("schemeVersion");
        root.Remove("sculptCellSize");
        root.Remove("tiles");
        // $schema is a file-level annotation on the MANIFEST, not document content: the writer emits it and
        // the reader ignores it, exactly as for a tile file. Carrying it onto the document would point a
        // later monolithic save at the manifest's schema.
        root.Remove("$schema");

        MapDocument doc;
        try
        {
            doc = root.Deserialize<MapDocument>(MapDocumentFile.CreateOptions(options.Registry, write: false))
                ?? throw new MapDocumentException($"{path}: manifest deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new MapDocumentException($"{path}: {ex.Message}", ex);
        }

        // The tiled form hoists the sculpt cell size to the manifest, because per-tile files must not each
        // restate it. The reader rebuilds the block header and the tile reads fill it.
        try { doc.TerrainOverrides = new MapTerrainOverrides(sculptCellSize); }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new MapDocumentException($"{path}: sculptCellSize {sculptCellSize} is not positive and finite.", ex);
        }

        IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, options.Registry);
        if (errors.Count > 0)
            throw new MapDocumentException($"{path}: invalid map manifest:\n  " + string.Join("\n  ", errors));

        index = new MapTileIndex(doc.TileSize, schemeVersion, Normalize(directory), entries);
        return doc;
    }

    /// <summary>Loads the manifest plus tiles: every occupied tile when <paramref name="window"/> is null,
    /// otherwise only the tiles in the window, with the rest keeping index entries so the document knows they
    /// exist and a later save to the SAME directory carries them through untouched.</summary>
    internal static MapDocument Load(string directory, MapTileRect? window, MapDocumentLoadOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        options ??= new MapDocumentLoadOptions();
        MapDocument doc = ReadManifest(directory, options, out MapTileIndex index);

        // A windowed load at a mismatched hash scheme REFUSES: a partial save carries stored hashes through
        // verbatim while recomputing the loaded ones, so it cannot upgrade what it cannot read. A whole load
        // is fine, and the next full save upgrades the document.
        if (window is not null && index.SchemeVersion != MapDocumentHash.SchemeVersion)
            throw new MapDocumentException(
                $"{directory}: manifest hashes were written under hash scheme {index.SchemeVersion}, this engine composes scheme " +
                $"{MapDocumentHash.SchemeVersion}. Load the whole document and save it to upgrade before windowing.");

        MapTerrainOverrides overrides = doc.TerrainOverrides!;
        float sculptCellSize = overrides.CellSize;
        var entries = new List<MapTileEntry>(index.Entries.Count);
        foreach (MapTileEntry entry in index.Entries)
        {
            bool load = window is null || window.Value.Contains(entry.Coord);
            if (load)
            {
                MapTileContent content = MapTileFile.Read(directory, entry.Coord, entry.Hash, options,
                                                          index.TileSize, sculptCellSize);
                doc.Placements.AddRange(content.Placements);
                doc.Spawns.AddRange(content.Spawns);
                doc.PlayerSpawns.AddRange(content.PlayerSpawns);
                foreach (MapSculptTile tile in content.SculptTiles) overrides.PutTile(tile);
            }
            entries.Add(entry with { Loaded = load });
        }

        doc.Tiles = new MapTileIndex(index.TileSize, index.SchemeVersion, Normalize(directory), entries);

        IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, options.Registry);
        if (errors.Count > 0)
            throw new MapDocumentException($"{directory}: invalid map document:\n  " + string.Join("\n  ", errors));
        return doc;
    }

    /// <summary>Re-derives every tile hash from its file and reports mismatches, plus orphan files under
    /// <c>tiles/</c> the manifest does not name and any stray <c>*.tmp</c> left by a crashed save. Reports,
    /// never deletes: deleting on the authority of a manifest that failed to parse is how a bad save turns
    /// into a lost world.</summary>
    internal static IReadOnlyList<string> Verify(string directory, MapDocRegistry registry)
    {
        var report = new List<string>();
        var options = new MapDocumentLoadOptions { Registry = registry };

        MapDocument doc;
        MapTileIndex index;
        try { doc = ReadManifest(directory, options, out index); }
        catch (MapDocumentException ex) { return new[] { ex.Message }; }

        float sculptCellSize = MapCanonical.SculptCellSizeOf(doc);
        var named = new HashSet<string>(StringComparer.Ordinal);
        var ids = new Dictionary<string, MapTileCoord>(StringComparer.Ordinal);

        foreach (MapTileEntry entry in index.Entries)
        {
            string path = MapTileFile.PathOf(directory, entry.Coord, entry.Hash);
            named.Add(Normalize(path));
            if (!File.Exists(path))
            {
                report.Add($"tile ({entry.Coord.X}, {entry.Coord.Z}): the manifest names '{MapTileFile.FileName(entry.Coord, entry.Hash)}' and no such file exists.");
                continue;
            }

            MapTileContent content;
            try
            {
                content = MapTileFile.Read(directory, entry.Coord, entry.Hash,
                                           new MapDocumentLoadOptions { Registry = registry },
                                           index.TileSize, sculptCellSize);
            }
            catch (MapDocumentException ex) { report.Add(ex.Message); continue; }

            string actual = MapDocumentHash.OfLists(content.Lists, registry);
            if (!string.Equals(actual, entry.Hash, StringComparison.Ordinal))
                report.Add($"tile ({entry.Coord.X}, {entry.Coord.Z}): content hashes to {actual}, the manifest says {entry.Hash}.");

            CollectIds(content, ids, report);
        }

        CollectStrays(directory, named, report);
        return report;
    }

    /// <summary>A shallow copy of a document carrying only the globals: same references for terrain, layers
    /// and shapes, with the four point-shaped lists empty and the sculpt block reduced to its header.</summary>
    internal static MapDocument GlobalsOnly(MapDocument doc) => new()
    {
        Schema = doc.Schema,
        FormatVersion = doc.FormatVersion,
        Id = doc.Id,
        DisplayName = doc.DisplayName,
        Bounds = doc.Bounds,
        TileSize = doc.TileSize,
        Terrain = doc.Terrain,
        ScatterLayers = doc.ScatterLayers,
        CompanionLayers = doc.CompanionLayers,
        Exclusions = doc.Exclusions,
        ScatterOverrides = doc.ScatterOverrides,
        Regions = doc.Regions,
        TerrainOverrides = new MapTerrainOverrides(MapCanonical.SculptCellSizeOf(doc)),
    };

    internal static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Path equality for the "a partial document may only be written back where it came from" guard.
    /// Case-insensitive except on Linux, matching how the platforms actually resolve names.</summary>
    internal static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    static void CollectIds(MapTileContent content, Dictionary<string, MapTileCoord> ids, List<string> report)
    {
        foreach (MapPlacement p in content.Placements) Claim(p.Id, "placement", content.Coord, ids, report);
        foreach (MapSpawn s in content.Spawns) Claim(s.Id, "spawn", content.Coord, ids, report);
        foreach (MapPlayerSpawn s in content.PlayerSpawns) Claim(s.Id, "player spawn", content.Coord, ids, report);
    }

    static void Claim(string id, string what, MapTileCoord coord, Dictionary<string, MapTileCoord> ids, List<string> report)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (ids.TryGetValue(id, out MapTileCoord other))
            report.Add($"duplicate {what} id '{id}' in tiles ({other.X}, {other.Z}) and ({coord.X}, {coord.Z}).");
        else ids[id] = coord;
    }

    static void CollectStrays(string directory, HashSet<string> named, List<string> report)
    {
        string tiles = Path.Combine(directory, MapTileFile.TilesDirectory);
        if (Directory.Exists(tiles))
            foreach (string file in Directory.EnumerateFiles(tiles, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(MapTileFile.TempSuffix, StringComparison.Ordinal))
                    report.Add($"stray temp file from a crashed save: {Path.GetRelativePath(directory, file)}");
                else if (!named.Contains(Normalize(file)))
                    report.Add($"orphan tile file the manifest does not name: {Path.GetRelativePath(directory, file)}");
            }

        string manifestTemp = Path.Combine(directory, ManifestTempName);
        if (File.Exists(manifestTemp))
            report.Add($"stray temp file from a crashed save: {ManifestTempName}");
    }

    static List<MapTileEntry> ReadTileEntries(JsonObject root, string where)
    {
        var entries = new List<MapTileEntry>();
        if (root["tiles"] is not JsonArray array) return entries;
        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject o)
                throw new MapDocumentException($"{where}: every tiles[] entry must be a JSON object.");
            int? x = ReadInt(o, "x"), z = ReadInt(o, "z");
            string? hash = o["hash"]?.GetValue<string>();
            if (x is null || z is null || string.IsNullOrEmpty(hash))
                throw new MapDocumentException($"{where}: every tiles[] entry needs integer x, integer z and a non-empty hash.");
            entries.Add(new MapTileEntry(new MapTileCoord(x.Value, z.Value), hash, Loaded: false));
        }
        return entries;
    }

    static int? ReadInt(JsonObject root, string name) =>
        root[name] is JsonValue v && v.TryGetValue(out int i) ? i : null;

    static float? ReadFloat(JsonObject root, string name) =>
        root[name] is JsonValue v && v.TryGetValue(out float f) ? f : null;
}
