using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Serialization;

namespace KhaozEngine.TileWorld;

/// <summary>Save/load of the one on-disk form: a directory with <c>world.json</c> and
/// <c>regions/r_&lt;rx&gt;_&lt;rz&gt;.json</c>. Every file lands via tmp + rename, the manifest last, and every
/// region's bytes are hashed against the manifest on load, so a torn write is refused by name rather than
/// loaded as a subtly different world. That is DETECTION, not rollback: region bytes are overwritten in
/// place, so a save interrupted part way through leaves the regions it already replaced replaced, and the
/// next load names the first file whose bytes disagree with the manifest.</summary>
public static class TileWorldFile
{
    /// <summary>Format version this engine writes, and the version every load is migrated up to.</summary>
    public const int CurrentFormatVersion = 1;
    /// <summary>Value of the manifest's <c>$schema</c> field.</summary>
    public const string SchemaUri = "https://khaozengine.dev/schemas/tileworld.world.schema.json";
    /// <summary>Name of the manifest file inside a world directory.</summary>
    public const string ManifestFileName = "world.json";
    /// <summary>Name of the subdirectory holding the region files.</summary>
    public const string RegionsDirectoryName = "regions";

    /// <summary>File name of one region, <c>r_&lt;rx&gt;_&lt;rz&gt;.json</c>. Invariant culture, because a
    /// negative coordinate under a culture with its own minus sign would write a name this engine could not
    /// read back.</summary>
    public static string RegionFileName(RegionCoord c) => string.Create(CultureInfo.InvariantCulture, $"r_{c.Rx}_{c.Rz}.json");
    /// <summary>Path of the manifest inside a world directory.</summary>
    public static string ManifestPath(string directory) => Path.Combine(directory, ManifestFileName);
    /// <summary>Path of one region file inside a world directory.</summary>
    public static string RegionPath(string directory, RegionCoord c) => Path.Combine(directory, RegionsDirectoryName, RegionFileName(c));

    /// <summary>True when the directory holds a world manifest.</summary>
    public static bool Exists(string directory) => File.Exists(ManifestPath(directory));

    /// <summary>Writes dirty regions (all when <paramref name="force"/>), removes files of regions the document
    /// no longer has, then the manifest. Clears <see cref="TileRegion.Dirty"/> on what it wrote.</summary>
    public static void Save(TileWorldDocument doc, string directory, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        // Every region is checked against the document's plane count BEFORE the first byte is written. A
        // document whose PlaneCount was changed after its regions were built would otherwise save a world
        // that every load refuses, and half of it would already be on disk by the time that showed up.
        foreach (TileRegion region in doc.Regions.Values)
        {
            if (region.Planes.Length != doc.PlaneCount)
                throw new TileWorldException($"region {region.Coord}: has {region.Planes.Length} planes, the document has {doc.PlaneCount}, refusing to save an inconsistent world");
        }
        string regionsDir = Path.Combine(directory, RegionsDirectoryName);
        Directory.CreateDirectory(regionsDir);

        Dictionary<RegionCoord, string> previous = ReadPreviousHashes(directory);
        var hashes = new Dictionary<RegionCoord, string>(doc.UnloadedRegionHashes);
        foreach (TileRegion region in doc.Regions.Values)
        {
            string path = RegionPath(directory, region.Coord);
            if (!region.Dirty && !force && File.Exists(path) && previous.TryGetValue(region.Coord, out string? prior))
            {
                hashes[region.Coord] = prior;
                continue;
            }
            byte[] bytes = TileRegionFile.WriteCanonical(region);
            WriteAtomic(path, bytes);
            hashes[region.Coord] = TileWorldHash.OfRegionBytes(bytes);
            region.Dirty = false;
        }

        // Materialised before the deletes: mutating a directory while its own enumeration is still open is
        // unspecified, and this loop deletes out of the directory it is walking.
        foreach (string stale in Directory.EnumerateFiles(regionsDir, "r_*.json").ToList())
        {
            if (!TryParseRegionFileName(Path.GetFileName(stale), out RegionCoord c) || hashes.ContainsKey(c)) continue;
            try { File.Delete(stale); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new TileWorldException($"{stale}: cannot delete the file of a region the world no longer has. {ex.Message}", ex);
            }
        }

        var manifest = new TileWorldManifest
        {
            Schema = SchemaUri, FormatVersion = CurrentFormatVersion, Id = doc.Id, DisplayName = doc.DisplayName,
            TileSize = doc.TileSize, PlaneCount = doc.PlaneCount, PlaneHeight = doc.PlaneHeight,
            CatalogPaths = doc.CatalogPaths.ToList(), NextObjectId = doc.NextObjectId,
            Regions = hashes.OrderBy(k => k.Key.Rz).ThenBy(k => k.Key.Rx)
                .Select(k => new TileWorldManifestRegion { Rx = k.Key.Rx, Rz = k.Key.Rz, Hash = k.Value }).ToList(),
        };
        WriteAtomic(ManifestPath(directory), JsonSerializer.SerializeToUtf8Bytes(manifest, TileWorldJson.Manifest));
    }

    /// <summary>Loads the manifest and every region, hash-checked.</summary>
    public static TileWorldDocument Load(string directory, TileWorldLoadOptions? options = null)
    {
        TileWorldSource source = TileWorldSource.Open(directory, options);
        foreach (RegionCoord c in source.KnownRegions.ToList()) source.EnsureLoaded(c);
        return source.Document;
    }

    internal static TileWorldManifest ReadManifest(string directory, TileWorldLoadOptions options)
    {
        string path = ManifestPath(directory);
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TileWorldException($"{path}: cannot read world manifest. {ex.Message}", ex);
        }
        JsonObject root;
        try { root = Jsonc.ParseNode(json) as JsonObject ?? throw new TileWorldException($"{path}: manifest is not a JSON object"); }
        catch (JsonException ex) { throw new TileWorldException($"{path}: {ex.Message}", ex); }

        JsonNode? versionNode = root["formatVersion"] ?? throw new TileWorldException($"{path}: manifest has no formatVersion");
        if (versionNode is not JsonValue versionValue || !versionValue.TryGetValue(out int version))
            throw new TileWorldException($"{path}: formatVersion must be an integer");
        if (version > CurrentFormatVersion)
            throw new TileWorldException($"{path}: formatVersion {version} is newer than this engine's {CurrentFormatVersion}");
        for (int v = version; v < CurrentFormatVersion; v++)
        {
            if (!options.Migrations.TryGetValue(v, out Func<JsonObject, JsonObject>? step))
                throw new TileWorldException($"{path}: formatVersion {version} needs a migration from {v} to {v + 1}, and none is registered");
            root = step(root);
            root["formatVersion"] = v + 1;
        }

        TileWorldManifest m;
        try { m = root.Deserialize<TileWorldManifest>(TileWorldJson.Manifest) ?? throw new TileWorldException($"{path}: empty manifest"); }
        catch (JsonException ex) { throw new TileWorldException($"{path}: {ex.Message}", ex); }
        if (m.PlaneCount < 1) throw new TileWorldException($"{path}: planeCount must be at least 1");
        if (!(m.TileSize > 0f)) throw new TileWorldException($"{path}: tileSize must be positive");
        if (string.IsNullOrWhiteSpace(m.Id)) throw new TileWorldException($"{path}: id is required");
        return m;
    }

    internal static byte[] ReadRegionBytes(string directory, RegionCoord c)
    {
        string path = RegionPath(directory, c);
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TileWorldException($"{path}: cannot read region {c}. {ex.Message}", ex);
        }
    }

    static Dictionary<RegionCoord, string> ReadPreviousHashes(string directory)
    {
        var result = new Dictionary<RegionCoord, string>();
        string path = ManifestPath(directory);
        if (!File.Exists(path)) return result;
        try
        {
            TileWorldManifest? m = JsonSerializer.Deserialize<TileWorldManifest>(File.ReadAllText(path), TileWorldJson.Manifest);
            if (m is null) return result;
            foreach (TileWorldManifestRegion r in m.Regions) result[new RegionCoord(r.Rx, r.Rz)] = r.Hash;
        }
        // An unreadable or corrupt previous manifest carries no hashes forward, so every loaded region is
        // rewritten. That is the safe direction: nothing is carried over from bytes we could not read.
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    static void WriteAtomic(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    internal static bool TryParseRegionFileName(string name, out RegionCoord c)
    {
        c = default;
        if (!name.StartsWith("r_", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) return false;
        string[] parts = name[2..^5].Split('_');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rx)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rz)) return false;
        c = new RegionCoord(rx, rz);
        return true;
    }
}
