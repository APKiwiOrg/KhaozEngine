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
public static partial class TileWorldFile
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
    /// no longer has, then the manifest. Clears <see cref="TileRegion.Dirty"/> on what it wrote. Before writing
    /// anything it refuses a document a load would reject: a region whose plane count disagrees with the
    /// document's, or an object or marker anchored outside its own region or on a plane the world does not
    /// have.</summary>
    public static void Save(TileWorldDocument doc, string directory, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        doc.ValidateFoliageLayers();
        // Everything a load will refuse is checked BEFORE the first byte is written: the region's plane count,
        // and every object's and marker's anchor and plane. A document whose PlaneCount was changed after its
        // regions were built, or one whose object was moved out of its own region by a bare X assignment,
        // would otherwise save a world that every load refuses, and half of it would already be on disk by the
        // time that showed up. These are exactly the checks TileRegionFile.Parse makes on the way back in.
        foreach (TileRegion region in doc.Regions.Values)
        {
            if (region.Planes.Length != doc.PlaneCount)
                throw new TileWorldException($"region {region.Coord}: has {region.Planes.Length} planes, the document has {doc.PlaneCount}, refusing to save an inconsistent world");
            TileRect rect = region.Coord.Rect;
            foreach (TileObject o in region.Objects)
            {
                if (!rect.Contains(o.X, o.Z))
                    throw new TileWorldException($"region {region.Coord}: object {o.Id} '{o.ArchetypeId}' is anchored at ({o.X}, {o.Z}), outside its own region, refusing to save a world no load would accept");
                if ((uint)o.Plane >= (uint)doc.PlaneCount)
                    throw new TileWorldException($"region {region.Coord}: object {o.Id} '{o.ArchetypeId}' is on plane {o.Plane}, the document has {doc.PlaneCount}, refusing to save a world no load would accept");
            }
            foreach (TileMarker m in region.Markers)
            {
                if (!rect.Contains(m.X, m.Z))
                    throw new TileWorldException($"region {region.Coord}: marker '{m.Name}' is at ({m.X}, {m.Z}), outside its own region, refusing to save a world no load would accept");
                if ((uint)m.Plane >= (uint)doc.PlaneCount)
                    throw new TileWorldException($"region {region.Coord}: marker '{m.Name}' is on plane {m.Plane}, the document has {doc.PlaneCount}, refusing to save a world no load would accept");
            }
        }
        string regionsDir = Path.Combine(directory, RegionsDirectoryName);
        Directory.CreateDirectory(regionsDir);

        TileWorldManifest? before = ReadPreviousManifest(directory);
        Dictionary<RegionCoord, string> previous = PreviousHashes(before);
        var hashes = new Dictionary<RegionCoord, string>(doc.UnloadedRegionHashes);
        var written = new List<TileRegion>();
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
            written.Add(region);
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
            Markers = BuildMarkerIndex(doc, before, hashes),
            FoliageLayers = doc.FoliageLayers.Count == 0 ? null : doc.FoliageLayers.Select(FoliageToDto).ToList(),
        };
        WriteAtomic(ManifestPath(directory), JsonSerializer.SerializeToUtf8Bytes(manifest, TileWorldJson.Manifest));
        // Dirty clears only now, once the manifest naming these exact bytes is on disk. Clearing it as each
        // region landed would mean a throw anywhere before this line (a failed write, an undeletable stale
        // file) left clean regions sitting under the OLD manifest, and the next save would then see
        // not-dirty plus a file plus a prior hash, and carry that stale hash forward over the new bytes.
        // The manifest would be permanently wrong, and every later load would refuse the world.
        foreach (TileRegion region in written) region.Dirty = false;
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
            root = step(root) ?? throw new TileWorldException($"{path}: migration from formatVersion {v} returned null");
            root["formatVersion"] = v + 1;
        }

        TileWorldManifest m;
        try { m = root.Deserialize<TileWorldManifest>(TileWorldJson.Manifest) ?? throw new TileWorldException($"{path}: empty manifest"); }
        catch (JsonException ex) { throw new TileWorldException($"{path}: {ex.Message}", ex); }
        if (m.PlaneCount < 1) throw new TileWorldException($"{path}: planeCount must be at least 1");
        if (!(m.TileSize > 0f)) throw new TileWorldException($"{path}: tileSize must be positive");
        if (string.IsNullOrWhiteSpace(m.Id)) throw new TileWorldException($"{path}: id is required");
        ValidateFoliageDtos(m.FoliageLayers, m.PlaneCount, path);
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

    // The manifest as it stands on disk, or null when there is none or it cannot be read. An unreadable or corrupt
    // previous manifest carries nothing forward, so every loaded region is rewritten and only the markers of loaded
    // regions survive. That is the safe direction: nothing is carried over from bytes we could not read.
    static TileWorldManifest? ReadPreviousManifest(string directory)
    {
        string path = ManifestPath(directory);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<TileWorldManifest>(File.ReadAllText(path), TileWorldJson.Manifest); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static Dictionary<RegionCoord, string> PreviousHashes(TileWorldManifest? previous)
    {
        var result = new Dictionary<RegionCoord, string>();
        if (previous is null) return result;
        foreach (TileWorldManifestRegion r in previous.Regions) result[new RegionCoord(r.Rx, r.Rz)] = r.Hash;
        return result;
    }

    // The marker index, DERIVED from the regions the way the collision map is. A save usually holds only some of
    // the world's regions in memory, so the index is the union of two halves: every marker of every LOADED region,
    // rebuilt from what that region carries right now, plus the previous manifest's rows for regions this save
    // never materialised. Without the second half a streaming editor that touched one region would silently drop
    // the marker index for the rest of the world.
    //
    // Sorted by name and keyed by it, because a marker name is document-unique and the write has to be canonical.
    // A loaded region OUTRANKS a carried-forward row of the same name: the region in memory is the live truth and
    // the manifest row is a copy of what a file said last time. Rows naming a region the world no longer has are
    // dropped with it.
    static List<TileWorldManifestMarker> BuildMarkerIndex(TileWorldDocument doc, TileWorldManifest? previous,
        Dictionary<RegionCoord, string> hashes)
    {
        var byName = new SortedDictionary<string, TileWorldManifestMarker>(StringComparer.Ordinal);
        foreach (TileWorldManifestMarker m in previous?.Markers ?? new List<TileWorldManifestMarker>())
        {
            RegionCoord home = RegionCoord.Of(m.X, m.Z);
            if (doc.Regions.ContainsKey(home) || !hashes.ContainsKey(home)) continue;
            byName[m.Name] = m;
        }
        foreach (TileRegion region in doc.Regions.Values.OrderBy(r => r.Coord.Rz).ThenBy(r => r.Coord.Rx))
            foreach (TileMarker m in region.Markers)
                byName[m.Name] = new TileWorldManifestMarker
                {
                    Name = m.Name, X = m.X, Z = m.Z, Plane = m.Plane,
                    Tags = m.Tags is null ? null : new List<string>(m.Tags),
                };
        return byName.Values.ToList();
    }

    // The tmp name is unique per CALL. It used to be exactly path + ".tmp", which every writer into the directory
    // shared: two saves of one world (two tools, or a tool and the editor) then truncated each other's tmp, and
    // the loser moved half-written bytes over the target or failed on a file the winner had already renamed away.
    // A rename is atomic, so with a name of its own each writer either lands its whole file or none of it, and
    // the target is always one complete file whoever lands last. Which of two concurrent saves wins a whole world
    // is a wider question (#350); this is the write primitive's half of it.
    internal static void WriteAtomic(string path, byte[] bytes)
    {
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // The tmp belongs to this call alone, so cleaning it up cannot take another writer's file with it.
            // Swallowed on the way out: the original failure is the one worth reporting.
            try { File.Delete(tmp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static bool TryParseRegionFileName(string name, out RegionCoord c)
    {
        c = default;
        if (!name.StartsWith("r_", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) return false;
        string[] parts = name[2..^5].Split('_');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int rx)
            || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int rz)) return false;
        // The name must be exactly the one this engine writes for that coordinate. "r_+1_2.json" and
        // "r_01_2.json" both parse to (1, 2), and treating either as region (1, 2) would let the stale sweep
        // delete a file the manifest never named.
        var parsed = new RegionCoord(rx, rz);
        if (!string.Equals(RegionFileName(parsed), name, StringComparison.Ordinal)) return false;
        c = parsed;
        return true;
    }
}
