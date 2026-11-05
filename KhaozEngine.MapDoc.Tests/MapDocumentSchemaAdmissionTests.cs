using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapDocumentSchemaAdmissionTests
{
    [Theory]
    [InlineData(false, MapDocumentRefusal.SchemaMemberMissing)]
    [InlineData(true, MapDocumentRefusal.SchemaMemberNull)]
    public void Native_required_members_identify_the_missing_or_null_field(bool nullValue, MapDocumentRefusal reason)
    {
        var root = JsonNode.Parse(MapDocumentFile.SaveText(NativeResolverFixtures.Create().Document))!.AsObject();
        JsonObject identity = root["resolverIdentity"]!.AsObject();
        JsonNode accepted = identity["payloadVersion"]!.DeepClone();
        if (nullValue) identity["payloadVersion"] = null;
        else identity.Remove("payloadVersion");
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString(), sourcePath: "native.json"));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("$.resolverIdentity.payloadVersion", error.FieldPath);
        Assert.Equal("native.json", error.SourceName);
        identity["payloadVersion"] = accepted;
        Assert.Equal("legacy", MapDocumentFile.LoadText(root.ToJsonString()).Id);
    }

    [Theory]
    [InlineData("manifest", "object")]
    [InlineData("tile", "object")]
    [InlineData("manifest", "syntax")]
    [InlineData("tile", "syntax")]
    [InlineData("document", "scalar")]
    [InlineData("manifest", "scalar")]
    [InlineData("tile", "scalar")]
    public void File_schema_admission_keeps_the_known_source_and_json_metadata(string form, string breakage)
    {
        using var storage = new NativeStorageFixture();
        MapTileEntry entry = storage.Complete.Tiles!.Entries[0];
        string path = form == "tile" ? MapTileFile.PathOf(storage.TiledPath, entry.Coord, entry.Hash)
            : Path.Combine(storage.TiledPath, "map.json");
        string accepted = form == "document" ? MapDocumentFile.SaveText(storage.Complete) : File.ReadAllText(path);
        var root = JsonNode.Parse(accepted)!.AsObject();
        root[form == "tile" ? "placements" : "id"] = new JsonObject();
        string refused = breakage switch { "object" => "[]", "syntax" => "{ invalid", _ => root.ToJsonString() };
        MapDocument Read(string text)
        {
            if (form == "document") return MapDocumentFile.LoadText(text, sourcePath: path);
            File.WriteAllText(path, text);
            return MapDocumentFile.LoadTiled(storage.TiledPath);
        }

        var error = Assert.Throws<MapDocumentException>(() => Read(refused));
        Assert.Equal(breakage == "object" ? MapDocumentRefusal.SchemaObjectRequired : MapDocumentRefusal.SchemaMalformed, error.Reason);
        Assert.Equal(form == "tile" ? storage.TiledPath : path, error.SourceName);
        if (form == "tile")
        {
            Assert.Equal(entry.Coord, error.Tile);
            Assert.Equal(path, error.Path);
        }
        if (breakage == "object") Assert.Equal("$", error.FieldPath);
        else
        {
            var json = Assert.IsAssignableFrom<JsonException>(error.InnerException);
            Assert.Equal(json.Path, error.FieldPath);
            Assert.Equal(json.LineNumber, error.LineNumber);
            Assert.Equal(json.BytePositionInLine, error.BytePositionInLine);
        }
        Assert.Equal(storage.Complete.Id, Read(accepted).Id);
    }

    [Fact]
    public void Nested_case_variant_duplicates_identify_the_containing_object_without_parsing_prose()
    {
        string accepted = NativeFixtures.AnalyticV3Json().Replace("\"seed\": 7", "\"seed\": 7, \"features\": [{\"type\":\"lake\",\"radius\":10}]");
        string refused = accepted.Replace("\"radius\":10", "\"radius\":10,\"RADIUS\":10");
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(refused));
        Assert.Equal(MapDocumentRefusal.SchemaMemberDuplicate, error.Reason);
        Assert.Equal("$.terrain.features[0]", error.FieldPath);
        Assert.Equal("(inline)", error.SourceName);
        Assert.Single(MapDocumentFile.LoadText(accepted).Terrain.Features);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registered_feature_json_failures_keep_the_original_document_refusal(bool manifest)
    {
        using var storage = new NativeStorageFixture();
        string path = Path.Combine(storage.TiledPath, "map.json");
        var root = JsonNode.Parse(manifest ? File.ReadAllText(path) : MapDocumentFile.SaveText(storage.Complete))!;
        root["terrain"]!["features"] = JsonNode.Parse("[{\"type\":\"refusal\",\"reject\":true}]");
        var options = new MapDocumentLoadOptions();
        options.Registry.RegisterFeature("refusal", typeof(RefusingFeature), _ => new LakeFeatureDoc { Radius = 1 }.Build());
        MapDocument Read()
        {
            if (!manifest) return MapDocumentFile.LoadText(root.ToJsonString(), options, path);
            File.WriteAllText(path, root.ToJsonString());
            return MapDocumentFile.LoadTiled(storage.TiledPath, options);
        }

        var error = Assert.Throws<MapDocumentException>(() => Read());
        Assert.Equal(MapDocumentRefusal.SchemaMemberMissing, error.Reason);
        Assert.Equal("$.custom.required", error.FieldPath);
        Assert.Equal(path, error.SourceName);
        Assert.Equal("custom.json", error.Path);
        Assert.Equal(2, error.ResolverVersion);
        var original = Assert.IsType<MapDocumentException>(error.InnerException);
        Assert.Equal(MapDocumentRefusal.SchemaMemberMissing, original.Reason);
        Assert.Equal("custom.json", original.SourceName);
        root["terrain"]!["features"]![0]!["reject"] = false;
        Assert.IsType<RefusingFeature>(Assert.Single(Read().Terrain.Features));
    }

    [Fact]
    public void A_null_deserialized_value_identifies_the_missing_root_and_keeps_tile_context()
    {
        var tile = new MapTileCoord(-4, 7);
        var error = Assert.Throws<MapDocumentException>(() =>
            MapDocumentJsonErrors.RequireDeserializedValue<MapDocument>(null, "missing document", "map.json", tile, "tile.json"));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal("$", error.FieldPath);
        Assert.Equal("map.json", error.SourceName);
        Assert.Equal(tile, error.Tile);
        Assert.Equal("tile.json", error.Path);
        var accepted = new MapDocument();
        Assert.Same(accepted, MapDocumentJsonErrors.RequireDeserializedValue(accepted, "missing document", "map.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_json_error_without_a_known_reason_uses_only_typed_json_metadata(bool legacyInner)
    {
        var original = new JsonException("unstructured JSON failure", "$.placements", 2, 17,
            legacyInner ? new MapDocumentException("consumer refusal") : null);
        var error = MapDocumentJsonErrors.Wrap(original, "map.json", "map.json: invalid JSON");
        Assert.Equal(MapDocumentRefusal.SchemaMalformed, error.Reason);
        Assert.Equal("$.placements", error.FieldPath);
        Assert.Equal(2L, error.LineNumber);
        Assert.Equal(17L, error.BytePositionInLine);
        Assert.Equal("map.json", error.SourceName);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public void Malformed_sculpt_payload_keeps_the_tile_file_and_array_position()
    {
        var coord = new MapTileCoord(-4, 7);
        const string refused = "{\"sculpt\":[{\"tileX\":-1,\"tileZ\":2,\"deltas\":[]}]}";
        var error = Assert.Throws<MapDocumentException>(() => MapTileFile.Parse(refused, "world", coord, "digest", MapDocRegistry.CreateDefault()));
        Assert.Equal(MapDocumentRefusal.SchemaMalformed, error.Reason);
        Assert.Equal("$.sculpt[0].deltas", error.FieldPath);
        Assert.Equal("world", error.SourceName);
        Assert.Equal(coord, error.Tile);
        Assert.Equal(Path.Combine("world", "tiles", "s_-1_0", "t_-4_7.digest.json"), error.Path);
        var root = JsonNode.Parse(refused)!;
        root["sculpt"]![0]!["deltas"] = JsonSerializer.SerializeToNode(new MapSculptTile(-1, 2).Deltas);
        Assert.Single(MapTileFile.Parse(root.ToJsonString(), "world", coord, "digest", MapDocRegistry.CreateDefault()).SculptTiles);
    }

    public sealed class RefusingFeature : MapFeature
    {
        public override string Type => "refusal";
        public bool Reject
        {
            get => false;
            set
            {
                if (value)
                    throw new JsonException("custom JSON failure", new MapDocumentException(MapDocumentRefusal.SchemaMemberMissing, "custom refusal")
                    { SourceName = "custom.json", Path = "custom.json", FieldPath = "$.custom.required", ResolverVersion = 2 });
            }
        }
    }
}
