using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KhaozEngine.Serialization;

namespace KhaozEngine.MapDoc;

/// <summary>Options for <see cref="MapDocumentFile.Load"/>: the feature registry and any format-version
/// migrations. Migrations transform the raw JSON object from version N to N+1 and must form a contiguous
/// run up to <see cref="MapDocumentFile.CurrentFormatVersion"/> (checked at load).</summary>
public sealed class MapDocumentLoadOptions
{
    public MapDocRegistry Registry { get; set; } = MapDocRegistry.CreateDefault();

    internal readonly SortedDictionary<int, Func<JsonObject, JsonObject>> Migrations = new();

    /// <summary>Creates load options with the built-in format migrations pre-registered, so an on-disk
    /// document at any prior format version loads without the caller wiring the engine's own steps. A game
    /// may still register additional steps (for its own synthetic older versions) on top.</summary>
    public MapDocumentLoadOptions()
    {
        RegisterMigration(1, MigrateV1ToV2);
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
}

/// <summary>Load/save for zone map documents. Loading parses (JSONC-tolerant), migrates old format versions,
/// deserializes, and validates. Every failure throws <see cref="MapDocumentException"/> with the source path:
/// map documents are dev-authored content, so a bad document fails a boot loudly instead of being quarantined.</summary>
public static class MapDocumentFile
{
    /// <summary>The format version this engine build reads and writes. v2 added the
    /// <see cref="MapDocument.TerrainOverrides"/> sculpt/delta layer; v1 documents migrate to it with an
    /// empty layer (byte-identical terrain).</summary>
    public const int CurrentFormatVersion = 2;

    public static MapDocument Load(string path, MapDocumentLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MapDocumentException($"{path}: cannot read map document. {ex.Message}", ex);
        }
        return LoadText(json, options, path);
    }

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

    public static void Save(MapDocument doc, string path, MapDocRegistry? registry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllText(path, SaveText(doc, registry));
    }

    public static string SaveText(MapDocument doc, MapDocRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        registry ??= MapDocRegistry.CreateDefault();
        IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
        if (errors.Count > 0)
            throw new MapDocumentException("refusing to save an invalid map document:\n  " + string.Join("\n  ", errors));
        return JsonSerializer.Serialize(doc, CreateOptions(registry, write: true));
    }

    internal static JsonSerializerOptions CreateOptions(MapDocRegistry registry, bool write)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            AllowOutOfOrderMetadataProperties = true,
            WriteIndented = write,
            // Omit nulls on write: absent means "default" (ground-snap Y, all-layers filter, no $schema),
            // and the JSON schema types $schema as string, so an emitted null would fail validation.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new MapFeatureConverter(registry));
        return options;
    }
}
