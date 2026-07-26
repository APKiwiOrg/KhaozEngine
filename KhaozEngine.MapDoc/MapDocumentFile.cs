using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KhaozEngine.Serialization;

namespace KhaozEngine.MapDoc;

/// <summary>Options for <see cref="MapDocumentFile.Load"/>: the feature registry, any format-version
/// migrations, and the opt-in tile-hash check. Migrations transform the raw JSON object from version N to
/// N+1 and must form a contiguous run up to <see cref="MapDocumentFile.CurrentFormatVersion"/> (checked at
/// load).</summary>
public sealed class MapDocumentLoadOptions
{
    public MapDocRegistry Registry { get; set; } = MapDocRegistry.CreateDefault();

    /// <summary>Re-derive every tile's canonical hash on a tiled read and fail when it disagrees with the
    /// manifest. Off by default, because verification means re-serializing the parsed tile canonically and
    /// that is exactly the per-load cost the tiled format exists to remove. Verification is a content check,
    /// not a hot path: <see cref="MapDocumentFile.VerifyTiled"/> is the whole-world form.</summary>
    public bool VerifyTileHashes { get; set; }

    internal readonly SortedDictionary<int, Func<JsonObject, JsonObject>> Migrations = new();

    /// <summary>Creates load options with the built-in format migrations pre-registered, so an on-disk
    /// document at any prior format version loads without the caller wiring the engine's own steps. A game
    /// may still register additional steps (for its own synthetic older versions) on top.</summary>
    public MapDocumentLoadOptions()
    {
        RegisterMigration(1, MigrateV1ToV2);
        RegisterMigration(2, MigrateV2ToV3);
    }

    /// <summary>Registers the transform from <paramref name="fromVersion"/> to fromVersion + 1. The step does
    /// only the data change; the loader stamps formatVersion afterwards.</summary>
    /// <exception cref="ArgumentException">A step from this version is already registered.</exception>
    public void RegisterMigration(int fromVersion, Func<JsonObject, JsonObject> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (Migrations.ContainsKey(fromVersion))
            throw new ArgumentException($"A migration from formatVersion {fromVersion} is already registered.", nameof(fromVersion));
        Migrations.Add(fromVersion, step);
    }

    /// <summary>v1 -> v2: v1 documents carried no sculpt layer (terrainOverrides was reserved and had to be
    /// absent or null), so a migrated v1 document simply has no overrides. Drop any placeholder null key so
    /// it deserializes as no sculpt layer and the terrain stays byte-identical to the analytic field.</summary>
    static JsonObject MigrateV1ToV2(JsonObject root)
    {
        if (root["terrainOverrides"] is null)
            root.Remove("terrainOverrides");
        return root;
    }

    /// <summary>v2 -> v3: stamps <see cref="MapDocumentFile.DefaultTileSize"/> and nothing else. Any default
    /// is as arbitrary as any other for a document that had no tile concept, so the rule is "deterministic
    /// and documented" rather than "derived".</summary>
    static JsonObject MigrateV2ToV3(JsonObject root)
    {
        root["tileSize"] = MapDocumentFile.DefaultTileSize;
        return root;
    }
}

/// <summary>Load/save for zone map documents, in both on-disk forms. Loading parses (JSONC-tolerant),
/// migrates old format versions, deserializes, and validates. Every failure throws
/// <see cref="MapDocumentException"/> with the source path: map documents are dev-authored content, so a bad
/// document fails a boot loudly instead of being quarantined.
/// <para><b>A document is either a single file or a directory, and both are first class.</b> Which form a
/// path holds is decided by what is on disk (<see cref="DetectForm"/>) or named by the caller
/// (<see cref="SaveAs"/>), and by nothing else. <b>No entry point here inspects a file extension</b>:
/// <c>Path.GetExtension("island.map")</c> is <c>".map"</c>, not empty, so an "extension-less path means
/// tiled" heuristic sends a directory named <c>island.map/</c> to a FILE write.</para></summary>
public static class MapDocumentFile
{
    /// <summary>The format version this engine build reads and writes. v2 added the
    /// <see cref="MapDocument.TerrainOverrides"/> sculpt/delta layer; v3 added the root
    /// <see cref="MapDocument.TileSize"/>, which per-tile hashing needs even for a monolithic document or a
    /// monolithic and a tiled copy of the same world would hash differently. Version and layout are
    /// independent axes: a v3 monolithic file is legal and is what <see cref="Save"/> writes.</summary>
    public const int CurrentFormatVersion = 3;

    /// <summary>The document tile edge, in world meters, a document gets when it does not declare one (and
    /// what the v2 to v3 migration stamps). At a heavily authored density a fully authored 512 m tile is
    /// about the size of a whole hand-authored zone document today, which parses in tens of milliseconds on
    /// a worker thread. Smaller tiles multiply file count without making any single load meaningfully
    /// cheaper; larger tiles push a single load past a frame budget.</summary>
    public const float DefaultTileSize = 512f;

    /// <summary><see cref="MapDocumentForm.Tiled"/> for an existing directory,
    /// <see cref="MapDocumentForm.Monolithic"/> for an existing file, <see cref="MapDocumentForm.None"/> for
    /// a path that does not exist. NEVER inspects the extension.</summary>
    public static MapDocumentForm DetectForm(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Directory.Exists(path)) return MapDocumentForm.Tiled;
        if (File.Exists(path)) return MapDocumentForm.Monolithic;
        return MapDocumentForm.None;
    }

    /// <summary>Loads whichever form is at the path: a directory loads tiled (and fails loudly if it has no
    /// <c>map.json</c>), a file loads monolithic.</summary>
    public static MapDocument Load(string path, MapDocumentLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (DetectForm(path) == MapDocumentForm.Tiled) return MapTiledFile.Load(path, window: null, options);

        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MapDocumentException($"{path}: cannot read map document. {ex.Message}", ex);
        }
        return LoadText(json, options, path);
    }

    /// <summary>Loads the manifest plus every occupied tile.</summary>
    public static MapDocument LoadTiled(string directory, MapDocumentLoadOptions? options = null) =>
        MapTiledFile.Load(directory, window: null, options);

    /// <summary>Loads the manifest plus the tiles in the window. Unloaded tiles keep index entries, so the
    /// document knows they exist and a later <see cref="SaveTiled"/> to the SAME directory carries them
    /// through untouched.</summary>
    public static MapDocument LoadTiled(string directory, MapTileRect window, MapDocumentLoadOptions? options = null) =>
        MapTiledFile.Load(directory, window, options);

    public static MapDocument LoadText(string json, MapDocumentLoadOptions? options = null, string? sourcePath = null)
    {
        options ??= new MapDocumentLoadOptions();
        string where = sourcePath ?? "(inline)";

        JsonObject root;
        try
        {
            JsonNode? node = Jsonc.ParseNode(json);
            root = node as JsonObject
                ?? throw new MapDocumentException($"{where}: document root must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new MapDocumentException($"{where}: invalid JSON. {ex.Message}", ex);
        }

        root = Migrate(root, options, where);

        MapDocument doc;
        try
        {
            doc = root.Deserialize<MapDocument>(CreateOptions(options.Registry, write: false))
                ?? throw new MapDocumentException($"{where}: document deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new MapDocumentException($"{where}: {ex.Message}", ex);
        }

        IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, options.Registry);
        if (errors.Count > 0)
            throw new MapDocumentException($"{where}: invalid map document:\n  " + string.Join("\n  ", errors));
        return doc;
    }

    /// <summary>The version gate and the migration chain, shared by the monolithic loader and the tiled
    /// manifest reader so a manifest climbs exactly the same steps a whole document does.</summary>
    internal static JsonObject Migrate(JsonObject root, MapDocumentLoadOptions options, string where)
    {
        if (root["formatVersion"] is not JsonValue versionValue || !versionValue.TryGetValue(out int version))
            throw new MapDocumentException($"{where}: missing or non-integer formatVersion.");

        if (version > CurrentFormatVersion)
            throw new MapDocumentException(
                $"{where}: formatVersion {version} is newer than this engine supports ({CurrentFormatVersion}). Update the engine.");

        while (version < CurrentFormatVersion)
        {
            if (!options.Migrations.TryGetValue(version, out Func<JsonObject, JsonObject>? step))
                throw new MapDocumentException(
                    $"{where}: formatVersion {version} needs a migration to {version + 1} and none is registered.");
            root = step(root) ?? throw new MapDocumentException($"{where}: migration from formatVersion {version} returned null.");
            version++;
            root["formatVersion"] = version;
        }
        return root;
    }

    /// <summary>Writes the monolithic form, reimplemented over <see cref="SaveTo"/>: the whole-document write
    /// path no longer builds a multi-gigabyte string.</summary>
    public static void Save(MapDocument doc, string path, MapDocRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        registry ??= MapDocRegistry.CreateDefault();
        // Prepared (validated, guarded) BEFORE the file is opened: a refusal must not truncate the target.
        MapDocument writable = PrepareWholeWrite(doc, registry);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteWhole(writable, stream, registry);
    }

    /// <summary>Serializes straight into the stream through a <see cref="Utf8JsonWriter"/>, so no
    /// intermediate string exists at any point. Peak managed memory is the writer's buffer, not the
    /// document's serialized size, which moves the monolithic ceiling off the .NET single-object element
    /// count and onto disk.</summary>
    public static void SaveTo(MapDocument doc, Stream stream, MapDocRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(stream);
        registry ??= MapDocRegistry.CreateDefault();
        WriteWhole(PrepareWholeWrite(doc, registry), stream, registry);
    }

    static void WriteWhole(MapDocument writable, Stream stream, MapDocRegistry registry)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, writable, CreateOptions(registry, write: true));
    }

    /// <summary>The monolithic form as one string. Kept because deep comparison in tests and existing
    /// consumers use it, and documented as SMALL DOCUMENTS ONLY: it is no longer the way to obtain bytes to
    /// hash (<see cref="MapDocumentHash.OfWorld"/> is), and it is subject to the single-object ceiling
    /// <see cref="SaveTo"/> exists to clear.</summary>
    public static string SaveText(MapDocument doc, MapDocRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        registry ??= MapDocRegistry.CreateDefault();
        MapDocument writable = PrepareWholeWrite(doc, registry);
        return JsonSerializer.Serialize(writable, CreateOptions(registry, write: true));
    }

    /// <summary>Writes the tiled form: a directory holding <c>map.json</c> plus content-addressed tile files.
    /// Peak SERIALIZATION buffer is one tile, and a tile whose canonical hash is unchanged is not rewritten
    /// at all, so a windowed save over a huge world touches only what the author actually edited.</summary>
    public static void SaveTiled(MapDocument doc, string directory, MapDocRegistry? registry = null,
                                 MapDocumentSaveOptions? save = null) =>
        MapTiledFile.Save(doc, directory, registry ?? MapDocRegistry.CreateDefault(), save);

    /// <summary>Dispatches on <see cref="DetectForm"/>: a directory to <see cref="SaveTiled"/>, a file to
    /// <see cref="Save"/>, and a path that does not exist THROWS. What an editor or tool calls, so a document
    /// saves back in the form it opened. It never invents a form for a path that does not exist, because
    /// there is no honest way to guess one.</summary>
    public static void SaveAuto(MapDocument doc, string path, MapDocRegistry? registry = null,
                                MapDocumentSaveOptions? save = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        MapDocumentForm form = DetectForm(path);
        if (form == MapDocumentForm.None)
            throw new MapDocumentException(
                $"{path}: nothing exists here, so this path has no form to save back into. Name one with SaveAs.");
        SaveAs(doc, path, form, registry, save);
    }

    /// <summary>Writes in an EXPLICITLY named form, whatever is or is not already at the path. This is what a
    /// conversion verb calls. <see cref="MapDocumentForm.None"/> throws.</summary>
    public static void SaveAs(MapDocument doc, string path, MapDocumentForm form,
                              MapDocRegistry? registry = null, MapDocumentSaveOptions? save = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        switch (form)
        {
            case MapDocumentForm.Tiled: SaveTiled(doc, path, registry, save); break;
            case MapDocumentForm.Monolithic: Save(doc, path, registry); break;
            default:
                throw new MapDocumentException($"{path}: MapDocumentForm.None is not a form a document can be written in.");
        }
    }

    /// <summary>Re-derives every tile hash from its file and reports mismatches, plus orphan files under
    /// <c>tiles/</c> the manifest does not name, any stray <c>*.tmp</c> left by a crashed save, and ids
    /// duplicated across tiles. Empty means clean. Reports, never deletes.</summary>
    public static IReadOnlyList<string> VerifyTiled(string directory, MapDocRegistry? registry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return MapTiledFile.Verify(directory, registry ?? MapDocRegistry.CreateDefault());
    }

    /// <summary>Validates, refuses a partial document, and normalizes the sculpt block for a WHOLE-document
    /// write.
    /// <para>The partial-document guard is stated on the DOCUMENT, not on one writer, because the data-loss
    /// path is a windowed document reaching a whole-document writer: that write silently drops every unloaded
    /// tile and looks like a successful save. A save entry point added later inherits the guard by calling
    /// one of these.</para>
    /// <para>The normalization: an EMPTY sculpt block at the default cell size is written as no block at all,
    /// which is the same world (<see cref="MapRuntime.BuildSculpt"/> returns null for both) and the same hash
    /// (<see cref="MapDocumentHash"/> normalizes a null block to exactly that). It makes a round trip through
    /// the tiled form byte-stable, since the tiled reader always rebuilds a block header. An empty block at a
    /// NON-default cell size is kept, because that cell size is authored information a save must not
    /// silently drop.</para></summary>
    static MapDocument PrepareWholeWrite(MapDocument doc, MapDocRegistry registry)
    {
        if (doc.Tiles is { IsPartial: true })
            throw new MapDocumentException(
                "refusing to write a windowed document as a whole document: it would silently drop every tile the " +
                "window did not load. Save it back to its own directory with SaveTiled, or load the whole world first.");

        IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
        if (errors.Count > 0)
            throw new MapDocumentException("refusing to save an invalid map document:\n  " + string.Join("\n  ", errors));

        if (doc.TerrainOverrides is not { IsEmpty: true, CellSize: MapTerrainOverrides.DefaultCellSize })
            return doc;

        MapDocument copy = MapTiledFile.GlobalsOnly(doc);
        copy.Placements = doc.Placements;
        copy.Spawns = doc.Spawns;
        copy.PlayerSpawns = doc.PlayerSpawns;
        copy.TerrainOverrides = null;
        return copy;
    }

    internal static JsonSerializerOptions CreateOptions(MapDocRegistry registry, bool write) =>
        BuildOptions(registry, indented: write);

    /// <summary>The compact, never-indented options the canonical hash is taken over. Distinct from the
    /// indented bytes on disk on purpose: indentation must never affect identity, because a format that
    /// sells itself as human-diffable will get hand-edited and reindented.</summary>
    internal static JsonSerializerOptions CreateCompactOptions(MapDocRegistry registry) =>
        BuildOptions(registry, indented: false);

    static JsonSerializerOptions BuildOptions(MapDocRegistry registry, bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            AllowOutOfOrderMetadataProperties = true,
            WriteIndented = indented,
            // Omit nulls on write: absent means "default" (ground-snap Y, all-layers filter, no $schema),
            // and the JSON schema types $schema as string, so an emitted null would fail validation.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new MapFeatureConverter(registry));
        return options;
    }
}
