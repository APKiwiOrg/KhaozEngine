using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class TerrainOverridesJsonRefusalTests
{
    [Theory]
    [InlineData("root", MapDocumentRefusal.SchemaObjectRequired, "$")]
    [InlineData("tiles", MapDocumentRefusal.SchemaArrayRequired, "$.tiles")]
    [InlineData("tile", MapDocumentRefusal.SchemaObjectRequired, "$.tiles[0]")]
    [InlineData("missing", MapDocumentRefusal.SchemaMemberMissing, "$.tiles[0].deltas")]
    [InlineData("deltas", MapDocumentRefusal.SchemaArrayRequired, "$.tiles[0].deltas")]
    [InlineData("short", MapDocumentRefusal.SchemaArrayLengthMismatch, "$.tiles[0].deltas")]
    [InlineData("long", MapDocumentRefusal.SchemaArrayLengthMismatch, "$.tiles[0].deltas")]
    public void TerrainOverrides_IdentifiesTheRefusedShapeAndAvailableTile(string variant, MapDocumentRefusal reason, string path)
    {
        const int count = TerrainSculpt.TileSize * TerrainSculpt.TileSize;
        string values = string.Join(",", Enumerable.Repeat("0", variant == "short" ? count - 1 : count + 1));
        string json = variant switch
        {
            "root" => "[]",
            "tiles" => "{\"tiles\":{}}",
            "tile" => "{\"tiles\":[0]}",
            "missing" => "{\"tiles\":[{\"tileX\":-2,\"tileZ\":3}]}",
            "deltas" => "{\"tiles\":[{\"tileX\":-2,\"tileZ\":3,\"deltas\":{}}]}",
            _ => "{\"tiles\":[{\"tileX\":-2,\"tileZ\":3,\"deltas\":[" + values + "]}]}",
        };
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MapTerrainOverridesConverter());
        var error = Assert.Throws<MapTerrainOverridesJsonException>(() => JsonSerializer.Deserialize<MapTerrainOverrides>(json, options));
        Assert.IsAssignableFrom<JsonException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(path, error.FieldPath);
        if (variant is "missing" or "deltas" or "short" or "long") Assert.Equal(new MapTileCoord(-2, 3), error.SculptTile);
        else Assert.Null(error.SculptTile);
        if (variant is "tile" or "missing" or "deltas" or "short" or "long") Assert.Equal(0, error.Index);
        if (variant is "short" or "long")
        {
            Assert.Equal(variant == "short" ? count - 1 : count + 1, error.ActualCount);
            Assert.Equal(count, error.ExpectedCount);
        }
        string control = "{\"tiles\":[{\"tileX\":-2,\"tileZ\":3,\"deltas\":[" + string.Join(",", Enumerable.Repeat("0", count)) + "]}]}";
        Assert.Equal((-2, 3), Tile(JsonSerializer.Deserialize<MapTerrainOverrides>(control, options)!));
        if (variant != "root")
        {
            var root = JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();
            root["terrainOverrides"] = JsonNode.Parse(json);
            var wrapped = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString(), sourcePath: "sculpt"));
            Assert.Equal(reason, wrapped.Reason);
            Assert.Equal(error.FieldPath, wrapped.FieldPath);
            Assert.Equal(error.SculptTile, wrapped.SculptTile);
            Assert.Equal(error.Index, wrapped.Index);
            Assert.Equal(error.ActualCount, wrapped.ActualCount);
            Assert.Equal(error.ExpectedCount, wrapped.ExpectedCount);
            Assert.Equal("sculpt", wrapped.SourceName);
            root["terrainOverrides"] = JsonNode.Parse(control);
            Assert.Equal((-2, 3), Tile(MapDocumentFile.LoadText(root.ToJsonString()).TerrainOverrides!));
        }
    }

    [Theory]
    [InlineData("{}", MapDocumentRefusal.SchemaMemberMissing, null)]
    [InlineData("{\"formatVersion\":null}", MapDocumentRefusal.SchemaValueRequired, null)]
    [InlineData("{\"formatVersion\":\"4\"}", MapDocumentRefusal.SchemaValueRequired, null)]
    [InlineData("{\"formatVersion\":6}", MapDocumentRefusal.DocumentMigrationSourceVersionInvalid, 6)]
    public void SurfaceMigration_IdentifiesTheSourceVersionWithoutChangingIt(string json, MapDocumentRefusal reason, int? version)
    {
        var source = JsonNode.Parse(json)!.AsObject();
        string before = source.ToJsonString();
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceMigration.Upgrade(source));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("$.formatVersion", error.FieldPath);
        Assert.Equal(version, error.ActualVersion);
        Assert.Equal(new[] { 4, 5 }, error.SupportedVersions);
        Assert.Equal(before, source.ToJsonString());
        source["formatVersion"] = 4;
        Assert.Equal(5, MapSurfaceMigration.Upgrade(source)["formatVersion"]!.GetValue<int>());
        source["formatVersion"] = 5;
        Assert.Equal(5, MapSurfaceMigration.Upgrade(source)["formatVersion"]!.GetValue<int>());
    }

    static (int, int) Tile(MapTerrainOverrides value)
    {
        var tile = Assert.Single(value.Tiles);
        return (tile.TileX, tile.TileZ);
    }
}
