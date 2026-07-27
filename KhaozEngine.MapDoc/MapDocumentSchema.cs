using System;
using System.IO;
using System.Text.Json.Nodes;

namespace KhaozEngine.MapDoc;

/// <summary>Access to the packaged map document JSON schemas: the monolithic document, the tiled manifest,
/// and a tile file. Consumers materialize them into their data directory (<see cref="WriteTo"/> /
/// <see cref="WriteAllTo"/>) so map files' <c>$schema</c> references resolve for build-time validation
/// (KhaozEngine.Content) and editor/AI tooling.
/// <para>ONE schema is authored (<c>mapdoc.schema.json</c>). The other two are DERIVED from it at runtime,
/// which is the only way three schemas describing overlapping content stay in agreement: a hand-maintained
/// copy of the placement, spawn and sculpt item shapes would rot the moment one of them gained a
/// field.</para></summary>
public static class MapDocumentSchema
{
    const string ResourceName = "KhaozEngine.MapDoc.mapdoc.schema.json";
    const string BaseId = "https://khaozengine.dev/schemas/";

    /// <summary>File name the derived manifest schema is written under, and what a manifest's
    /// <c>$schema</c> points at.</summary>
    public const string ManifestFileName = "mapdoc.manifest.schema.json";

    /// <summary>File name the derived tile schema is written under, and what a tile file's <c>$schema</c>
    /// points at (from two directories down, inside its shard).</summary>
    public const string TileFileName = "mapdoc.tile.schema.json";

    /// <summary>File name the authored document schema is written under.</summary>
    public const string DocumentFileName = "mapdoc.schema.json";

    /// <summary>The monolithic document schema, as authored.</summary>
    public static string GetJson()
    {
        using Stream stream = typeof(MapDocumentSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The tiled manifest schema: the document schema without the four bucketed content lists (they
    /// live in tile files), plus <c>schemeVersion</c>, <c>sculptCellSize</c> and the occupied-tile
    /// index.</summary>
    public static string GetManifestJson()
    {
        JsonObject root = Root();
        root["$id"] = BaseId + ManifestFileName;
        root["title"] = "KhaozEngine tiled zone map manifest";

        JsonObject properties = Member(root, "properties");
        properties.Remove("placements");
        properties.Remove("spawns");
        properties.Remove("playerSpawns");
        properties.Remove("terrainOverrides");

        properties["schemeVersion"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 };
        properties["sculptCellSize"] = new JsonObject { ["type"] = "number", ["exclusiveMinimum"] = 0 };
        properties["tiles"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("x", "z", "hash"),
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["x"] = new JsonObject { ["type"] = "integer" },
                    ["z"] = new JsonObject { ["type"] = "integer" },
                    ["hash"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                },
            },
        };
        return root.ToJsonString(WriteOptions);
    }

    /// <summary>A tile file's schema: an optional <c>$schema</c> annotation plus exactly the four content
    /// lists, referencing the same item definitions the document schema uses.</summary>
    public static string GetTileJson()
    {
        JsonObject source = Root();
        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = BaseId + TileFileName,
            ["title"] = "KhaozEngine zone map tile",
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["$schema"] = new JsonObject { ["type"] = "string" },
                ["placements"] = Ref("placements"),
                ["spawns"] = Ref("spawns"),
                ["playerSpawns"] = Ref("playerSpawns"),
                ["sculpt"] = Ref("sculptTiles"),
            },
            ["$defs"] = Detach(source, "$defs"),
        };
        return root.ToJsonString(WriteOptions);
    }

    /// <summary>Writes the monolithic document schema to <paramref name="path"/>, creating the directory if
    /// needed.</summary>
    public static void WriteTo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, GetJson());
    }

    /// <summary>Writes all three schemas into a directory under their canonical file names, which is what
    /// makes the <c>$schema</c> references a tiled document writes resolve.</summary>
    public static void WriteAllTo(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, DocumentFileName), GetJson());
        File.WriteAllText(Path.Combine(directory, ManifestFileName), GetManifestJson());
        File.WriteAllText(Path.Combine(directory, TileFileName), GetTileJson());
    }

    static readonly System.Text.Json.JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    static JsonObject Root() =>
        JsonNode.Parse(GetJson()) as JsonObject
            ?? throw new InvalidOperationException($"'{ResourceName}' root is not a JSON object.");

    static JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };

    /// <summary>An object member of the authored schema, still attached, for in-place edits.</summary>
    static JsonObject Member(JsonObject root, string name)
    {
        JsonNode node = root[name] ?? throw new InvalidOperationException(
            $"'{ResourceName}' has no '{name}' member, so the derived schemas cannot be built. " +
            "The derivation and the authored schema must move together.");
        return node as JsonObject ?? throw new InvalidOperationException($"'{ResourceName}' member '{name}' is not an object.");
    }

    /// <summary>An object member REMOVED from its parent, so it can be reparented into a derived schema (a
    /// <see cref="JsonNode"/> cannot have two parents).</summary>
    static JsonObject Detach(JsonObject root, string name)
    {
        JsonObject node = Member(root, name);
        root.Remove(name);
        return node;
    }
}
