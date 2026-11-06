using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public sealed class MapWaterJsonRefusalTests
{
    const string Valid = "{\"media\":[{\"key\":\"water\",\"kind\":\"Water\",\"wadeSpeedScale\":1}],\"bodies\":[]}";
    static JsonSerializerOptions Options => new() { Converters = { new MapWaterSetJsonConverter() } };

    [Theory]
    [InlineData("object", MapDocumentRefusal.SchemaObjectRequired, "$")]
    [InlineData("missing", MapDocumentRefusal.SchemaMemberMissing, "$.bodies")]
    [InlineData("unknown", MapDocumentRefusal.SchemaMemberUnknown, "$.extra")]
    [InlineData("duplicate", MapDocumentRefusal.SchemaMemberDuplicate, "$.bodies")]
    [InlineData("array", MapDocumentRefusal.SchemaArrayRequired, "$.media")]
    [InlineData("item", MapDocumentRefusal.SchemaObjectRequired, "$.media[0]")]
    [InlineData("text", MapDocumentRefusal.SchemaValueRequired, "$.media[0].key")]
    [InlineData("enum", MapDocumentRefusal.SchemaEnumUnsupported, "$.media[0].kind")]
    [InlineData("number-type", MapDocumentRefusal.SchemaValueRequired, "$.media[0].wadeSpeedScale")]
    [InlineData("nonfinite", MapDocumentRefusal.ValueNonfinite, "$.media[0].wadeSpeedScale")]
    public void Converter_KeepsTheConcreteRuleAndKnownJsonPath(string fault, MapDocumentRefusal reason, string path)
    {
        string text = fault switch
        {
            "object" => "[]",
            "missing" => "{\"media\":[]}",
            "unknown" => "{\"media\":[],\"bodies\":[],\"extra\":0}",
            "duplicate" => "{\"media\":[],\"bodies\":[],\"bodies\":[]}",
            "array" => "{\"media\":{},\"bodies\":[]}",
            "item" => "{\"media\":[0],\"bodies\":[]}",
            "text" => Valid.Replace("\"key\":\"water\"", "\"key\":0", StringComparison.Ordinal),
            "enum" => Valid.Replace("\"Water\"", "\"Mud\"", StringComparison.Ordinal),
            "number-type" => Valid.Replace("\"wadeSpeedScale\":1", "\"wadeSpeedScale\":\"1\"", StringComparison.Ordinal),
            _ => Valid.Replace("\"wadeSpeedScale\":1", "\"wadeSpeedScale\":1e39", StringComparison.Ordinal),
        };
        var error = Assert.Throws<MapWaterJsonException>(() => JsonSerializer.Deserialize<MapWaterSet>(text, Options));
        Assert.IsAssignableFrom<JsonException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(path, error.FieldPath);
        if (fault == "enum") { Assert.Equal("Mud", error.ActualText); Assert.Equal(typeof(MapWaterMediumKind), error.EnumType); }
        if (fault == "nonfinite") Assert.Equal(1e39, error.ActualNumber);
        Assert.Single(JsonSerializer.Deserialize<MapWaterSet>(Valid, Options)!.Media);
    }

    [Fact]
    public void DecimalCoordinates_KeepTheirTextAndSignedLimits()
    {
        var source = WaterFixtures.OceanOverCaves().Water!;
        var water = new MapWaterSet();
        water.Bodies.Add(source.Bodies.Single(b => b.Id == "ocean"));
        var json = JsonNode.Parse(JsonSerializer.Serialize(water, Options))!;
        var rect = json["bodies"]![0]!["domain"]!["rects"]![0]!;
        rect["minX"] = "01";
        var error = Assert.Throws<MapWaterJsonException>(() => JsonSerializer.Deserialize<MapWaterSet>(json.ToJsonString(), Options));
        Assert.Equal(MapDocumentRefusal.SchemaInt64Malformed, error.Reason);
        Assert.Equal("$.bodies[0].domain.rects[0].minX", error.FieldPath);
        Assert.Equal("01", error.ActualText);
        Assert.Equal(long.MinValue, error.MinimumInteger);
        Assert.Equal(long.MaxValue, error.MaximumInteger);
        rect["minX"] = long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(long.MinValue, JsonSerializer.Deserialize<MapWaterSet>(json.ToJsonString(), Options)!.Bodies[0].Domain.Rects[0].MinX);
    }

    [Theory]
    [InlineData("missing", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("type", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("version", MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    public void Migration_ReportsItsActualVersionWithoutWritingTheSource(string fault, MapDocumentRefusal reason)
    {
        var source = new JsonObject();
        if (fault == "type") source["formatVersion"] = "5";
        else if (fault == "version") source["formatVersion"] = 4;
        string before = source.ToJsonString();
        var error = Assert.Throws<MapDocumentException>(() => MapWaterMigration.Upgrade(source));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("formatVersion", error.FieldPath);
        if (fault == "version") { Assert.Equal(4, error.ActualVersion); Assert.Equal(new[] { 5, 6 }, error.SupportedVersions); }
        Assert.Equal(before, source.ToJsonString());
        source["formatVersion"] = 5;
        Assert.Equal(6, MapWaterMigration.Upgrade(source)["formatVersion"]!.GetValue<int>());
        Assert.Equal(5, source["formatVersion"]!.GetValue<int>());
        source["formatVersion"] = 6;
        Assert.Equal(6, MapWaterMigration.Upgrade(source)["formatVersion"]!.GetValue<int>());
    }

    [Fact]
    public void DomainAdapters_ForwardTheFirstFindingAndTheFullOrderedSnapshot()
    {
        var body = WaterFixtures.OceanOverCaves().Water!.Bodies.Single(b => b.Id == "ocean");
        var domain = body.Domain with { Rects = [new(0, 0, 0, 1), new(0, 0, 1, 0)] };
        var error = Assert.Throws<MapDocumentException>(() => MapWaterDomain.Compile(domain));
        Assert.Equal(MapDocumentRefusal.BoundsInvalid, error.Reason);
        Assert.Equal("Rects[0]", error.FieldPath);
        Assert.Equal(2, error.Findings!.Count);
        Assert.Equal(new int?[] { 0, 1 }, error.Findings.Select(f => f.Index));
        var view = MapScopedSurfaces.CompleteView(new MapSurfaceSet());
        var effective = Assert.Throws<MapDocumentException>(() => MapEffectiveWater.Resolve(view, body with { Domain = domain }));
        Assert.Equal(error.Reason, effective.Reason);
        Assert.Equal(body.Id, effective.EntityId);
        Assert.Equal(2, effective.Findings!.Count);
        Assert.All(effective.Findings, f => Assert.Equal(body.Id, f.EntityId));
        Assert.NotEmpty(MapWaterDomain.Compile(body.Domain).Pieces);
        Assert.NotEmpty(MapEffectiveWater.Resolve(view, body).Domain.Pieces);
    }

    [Fact]
    public void ScalarConversionFallback_UsesSchemaMalformedWithoutInventingAPath()
    {
        var water = new MapWaterSet();
        water.Bodies.Add(WaterFixtures.OceanOverCaves().Water!.Bodies.Single(b => b.Id == "ocean"));
        var json = JsonNode.Parse(JsonSerializer.Serialize(water, Options))!;
        var unit = json["bodies"]![0]!["domain"]!["frame"]!["cellUnitMetres"]!;
        unit["numerator"] = "one";
        var error = Assert.Throws<MapWaterJsonException>(() => JsonSerializer.Deserialize<MapWaterSet>(json.ToJsonString(), Options));
        Assert.Equal(MapDocumentRefusal.SchemaMalformed, error.Reason);
        Assert.Null(error.FieldPath);
        unit["numerator"] = 1;
        Assert.Single(JsonSerializer.Deserialize<MapWaterSet>(json.ToJsonString(), Options)!.Bodies);
    }

    [Fact]
    public void DocumentWrapper_PreservesTheWaterCodecReasonAndPath()
    {
        string valid = MapDocumentFile.SaveText(WaterFixtures.OceanOverCaves());
        var json = JsonNode.Parse(valid)!;
        json["water"]!["media"]![0]!["kind"] = "Mud";
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(json.ToJsonString()));
        Assert.Equal(MapDocumentRefusal.SchemaEnumUnsupported, error.Reason);
        Assert.Equal("$.media[0].kind", error.FieldPath);
        Assert.Equal("Mud", error.ActualText);
        Assert.Equal(typeof(MapWaterMediumKind), error.EnumType);
        Assert.NotNull(MapDocumentFile.LoadText(valid).Water);
    }
}
