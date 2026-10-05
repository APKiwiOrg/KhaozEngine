using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed partial class NativeDocumentTests
{
    [Fact]
    public void LegacyCasingAndMissingDefaults_RemainSupported()
    {
        const string json = """
            { "formatVersion": 3, "Id": "legacy", "Bounds": { "MinX": -100, "MinZ": -100, "MaxX": 100, "MaxZ": 100 },
              "Terrain": { "Seed": 7, "biomes": [{ "Biome": "Meadow", "BaseHeight": 1.5 }] },
              "Placements": [{ "Id": "old-inn", "Kind": "building_inn", "X": -30, "Z": 20 }] }
            """;
        var doc = MapDocumentFile.LoadText(json);
        Assert.Equal(7, doc.Terrain.Seed);
        Assert.Equal(1.5f, doc.Terrain.Biomes[0].BaseHeight);
        Assert.Equal("old-inn", doc.Placements[0].Id);
        Assert.Equal(-100f, doc.PlayableBounds!.MinX);
        Assert.Equal(100f, doc.PlayableBounds.MaxZ);
        var minimal = MapDocumentFile.LoadText("""
            { "formatVersion": 4, "id": "minimal", "bounds": { "maxX": 10, "maxZ": 10 } }
            """);
        Assert.Equal(1, minimal.Terrain.Seed);
        Assert.Equal(0f, minimal.Bounds.MinX);
    }

    [Fact]
    public void LegacyBoundsDefaults_SurviveMigrationIntoRequiredNativeBounds()
    {
        var doc = MapDocumentFile.LoadText("""
            { "formatVersion": 3, "id": "minimal", "bounds": { "MaxX": 10, "MaxZ": 10 } }
            """);
        Assert.Equal(0f, doc.Bounds.MinX);
        Assert.Equal(0f, doc.Bounds.MinZ);
        Assert.Equal(0f, doc.PlayableBounds!.MinX);
        Assert.Equal(0f, doc.PlayableBounds.MinZ);
        Assert.Equal(10f, doc.PlayableBounds.MaxX);
        Assert.Equal(10f, doc.PlayableBounds.MaxZ);
        Assert.Equal(1, doc.Terrain.Seed);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void NativeCasing_RetainsExplicitBoundsAndRecognizesRequiredMembers(int version)
    {
        var root = JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();
        root["formatVersion"] = version;
        root["PlayableBounds"] = JsonNode.Parse("""{ "MINX": -40, "minz": -50, "MAXX": 40, "maxz": 50 }""");
        root["ResolverIdentity"] = JsonNode.Parse("""{ "PayloadVersion": 1, "ResolverVersion": 2 }""");
        root["NativeAssets"] = JsonNode.Parse("""[{ "ID": "inn", "Path": "inn.json", "SHA256": "abc", "PayloadVersion": 1 }]""");
        var doc = MapDocumentFile.LoadText(root.ToJsonString());
        Assert.Equal(-40f, doc.PlayableBounds!.MinX);
        Assert.Equal(50f, doc.PlayableBounds.MaxZ);
        Assert.Equal(2, doc.ResolverIdentity!.ResolverVersion);
        Assert.Equal("inn", Assert.Single(doc.NativeAssets).Id);
    }

    [Theory]
    [InlineData("playableBounds", "{\"maxX\":10,\"maxZ\":10}")]
    [InlineData("resolverIdentity", "{}")]
    [InlineData("resolverIdentity", "{\"payloadVersion\":1}")]
    [InlineData("resolverIdentity", "{\"resolverVersion\":1}")]
    [InlineData("resolverIdentity", "{\"payloadVersion\":0,\"resolverVersion\":1}")]
    [InlineData("resolverIdentity", "{\"payloadVersion\":1,\"resolverVersion\":-1}")]
    [InlineData("nativeAssets", "null")]
    [InlineData("nativeAssets", "[null]")]
    [InlineData("nativeAssets", "[{}]")]
    [InlineData("nativeAssets", "[{\"path\":\"inn.json\",\"sha256\":\"abc\",\"payloadVersion\":1}]")]
    [InlineData("nativeAssets", "[{\"id\":\"inn\",\"sha256\":\"abc\",\"payloadVersion\":1}]")]
    [InlineData("nativeAssets", "[{\"id\":\"inn\",\"path\":\"inn.json\",\"payloadVersion\":1}]")]
    [InlineData("nativeAssets", "[{\"id\":\"inn\",\"path\":\"inn.json\",\"sha256\":\"abc\"}]")]
    [InlineData("nativeAssets", "[{\"id\":null,\"path\":\"inn.json\",\"sha256\":\"abc\",\"payloadVersion\":1}]")]
    [InlineData("nativeAssets", "[{\"id\":\"inn\",\"path\":\"\",\"sha256\":\"abc\",\"payloadVersion\":1}]")]
    [InlineData("nativeAssets", "[{\"id\":\"inn\",\"path\":\"inn.json\",\"sha256\":null,\"payloadVersion\":1}]")]
    [InlineData("nativeAssets", "[{\"id\":\"inn\",\"path\":\"inn.json\",\"sha256\":\"abc\",\"payloadVersion\":0}]")]
    [InlineData("numericIdHighWaterMark", "-1")]
    public void Load_RejectsInvalidNativePayloads(string property, string value)
    {
        var root = MapNativeMigration.Upgrade(JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject());
        root[property] = JsonNode.Parse(value);
        Assert.Throws<MapDocumentException>(() => { MapDocumentFile.LoadText(root.ToJsonString()); });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void LoadAndSave_RejectNonpositiveOptionalNumericId(long id)
    {
        var root = JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();
        root["placements"]![0]!["numericId"] = id;
        Assert.Throws<MapDocumentException>(() => { MapDocumentFile.LoadText(root.ToJsonString()); });
        var doc = MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json());
        doc.Placements[0].NumericId = id;
        Assert.Throws<MapDocumentException>(() => { MapDocumentFile.SaveText(doc); });
    }

    public static IEnumerable<object[]> InvalidNativeMutations()
    {
        yield return new object[] { (Action<MapDocument>)(doc => doc.NativeAssets = null!) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.NativeAssets.Add(null!)) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.NativeAssets.Add(new MapAssetRef(null!, "inn.json", "abc", 1))) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.NativeAssets.Add(new MapAssetRef("inn", null!, "abc", 1))) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.NativeAssets.Add(new MapAssetRef("inn", "inn.json", null!, 1))) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.NativeAssets.Add(new MapAssetRef("", "inn.json", "abc", 1))) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.NativeAssets.Add(new MapAssetRef("inn", "", "abc", 1))) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.NativeAssets.Add(new MapAssetRef("inn", "inn.json", "", 1))) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.NativeAssets.Add(new MapAssetRef("inn", "inn.json", "abc", 0))) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.NativeAssets.Add(new MapAssetRef("inn", "inn.json", "abc", -1))) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.ResolverIdentity = new MapResolverIdentityDoc(0, 1)) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.ResolverIdentity = new MapResolverIdentityDoc(-1, 1)) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.ResolverIdentity = new MapResolverIdentityDoc(1, 0)) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.ResolverIdentity = new MapResolverIdentityDoc(1, -1)) };
        yield return new object[] { (Action<MapDocument>)(doc => doc.NumericIdHighWaterMark = -1) };
    }

    [Theory]
    [MemberData(nameof(InvalidNativeMutations))]
    public void Save_RejectsInvalidNativeMetadata(Action<MapDocument> mutate)
    {
        var doc = MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json());
        mutate(doc);
        Assert.NotEmpty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
        Assert.Throws<MapDocumentException>(() => { MapDocumentFile.SaveText(doc); });
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("[{\"Id\":\"tree\",\"Weight\":1}]", true)]
    [InlineData("[{\"id\":\"tree\",\"weight\":1,\"typo\":true}]", false)]
    public void ScatterKinds_TraversesApplicableNullableUnion(string kinds, bool valid)
    {
        var root = JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();
        root["scatterOverrides"] = JsonNode.Parse("""[{"shape":{"type":"disc","radius":5}}]""");
        root["scatterOverrides"]![0]!["kinds"] = JsonNode.Parse(kinds);
        if (!valid)
        {
            Assert.Throws<MapDocumentException>(() => { MapDocumentFile.LoadText(root.ToJsonString()); });
            return;
        }
        var doc = MapDocumentFile.LoadText(root.ToJsonString());
        var loaded = MapDocumentFile.LoadText(MapDocumentFile.SaveText(doc));
        if (kinds == "null") Assert.Null(loaded.ScatterOverrides[0].Kinds);
        else Assert.Equal("tree", Assert.Single(loaded.ScatterOverrides[0].Kinds!).Id);
    }

    [Fact]
    public void RegistryFeaturePayload_RemainsOpenAndRoundtrips()
    {
        var root = JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();
        root["terrain"]!["features"] = JsonNode.Parse("""[{"type":"flatten","TargetHeight":8,"radius":2}]""");
        var doc = MapDocumentFile.LoadText(root.ToJsonString());
        var feature = Assert.IsType<FlattenFeatureDoc>(Assert.Single(doc.Terrain.Features));
        Assert.Equal(8f, feature.TargetHeight);
        var loaded = MapDocumentFile.LoadText(MapDocumentFile.SaveText(doc));
        Assert.Equal(8f, Assert.IsType<FlattenFeatureDoc>(Assert.Single(loaded.Terrain.Features)).TargetHeight);
    }
}
