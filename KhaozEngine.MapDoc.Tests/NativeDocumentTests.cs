using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed partial class NativeDocumentTests
{
    [Fact]
    public void NativeV4_MigrationIsPure_AnalyticAndIdsSurvive()
    {
        var original = JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();
        string before = original.ToJsonString();
        var upgraded = MapNativeMigration.Upgrade(original);
        Assert.Equal(before, original.ToJsonString());
        Assert.Equal(upgraded["bounds"]!.ToJsonString(), upgraded["playableBounds"]!.ToJsonString());
        var doc = MapDocumentFile.LoadText(upgraded.ToJsonString());
        Assert.Equal("old-inn", Assert.Single(doc.Placements).Id);
        Assert.Equal("building_inn", doc.Placements[0].Kind);
        Assert.Null(doc.Placements[0].NumericId);
        Assert.Null(doc.Placements[0].AssetId);
        Assert.Null(doc.Placements[0].DisplayName);
        Assert.Empty(doc.NativeAssets);
        Assert.Equal(0L, doc.NumericIdHighWaterMark);
        Assert.Throws<MapDocumentException>(() => { MapDocumentFile.LoadText("""{"formatVersion":2147483647}"""); });
        upgraded["bounds"]!["minX"] = -200;
        Assert.Equal(-100, original["bounds"]!["minX"]!.GetValue<int>());
        Assert.Equal(-100, upgraded["playableBounds"]!["minX"]!.GetValue<int>());
    }

    [Fact]
    public void NativeMigration_UnknownFieldsBoundsAndRepeatAreExplicit()
    {
        var v3 = JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();
        var v4 = MapNativeMigration.Upgrade(v3);
        Assert.Equal(4, v4["formatVersion"]!.GetValue<int>());
        Assert.Equal(v4.ToJsonString(), MapNativeMigration.Upgrade(v4).ToJsonString());
        var doc = MapDocumentFile.LoadText(v4.ToJsonString());
        Assert.Null(doc.ResolverIdentity);
        Assert.Equal(7, doc.Terrain.Seed);
        Assert.Equal(-0.5f, doc.Terrain.WaterLevel);
        TerrainField field = MapRuntime.BuildField(doc, MapDocRegistry.CreateDefault());
        Assert.Equal(1.5f, field.SampleHeight(-30, 20));
        var placed = Assert.Single(MapRuntime.BuildPlacements(doc, field));
        Assert.Equal("building_inn", placed.Id);
        Assert.Equal(new[] { "first", "second" }, doc.Placements[0].Tags);
        Assert.Equal(new Vector3(-30, 1.5f, 20), new Vector3(doc.Placements[0].X, doc.Placements[0].Y!.Value, doc.Placements[0].Z));
        Assert.Equal(0.371f, doc.Placements[0].Yaw);
        Assert.Equal(1.137f, doc.Placements[0].Scale);
        var unknown = (JsonObject)v4.DeepClone();
        unknown["misspelledNativeField"] = true;
        Assert.Throws<MapDocumentException>(() => { MapDocumentFile.LoadText(unknown.ToJsonString()); });
        var badBounds = (JsonObject)v4.DeepClone();
        badBounds["playableBounds"]!["minX"] = -101;
        Assert.Throws<MapDocumentException>(() => { MapDocumentFile.LoadText(badBounds.ToJsonString()); });
        Assert.Equal(v3["terrain"]!.ToJsonString(), v4["terrain"]!.ToJsonString());
        Assert.Equal(v3["placements"]!.ToJsonString(), v4["placements"]!.ToJsonString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Load_MigratesContiguousLegacyVersions(int version)
    {
        var root = JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();
        root["formatVersion"] = version;
        if (version < 3) root.Remove("tileSize");
        var doc = MapDocumentFile.LoadText(root.ToJsonString());
        Assert.Equal(5, doc.FormatVersion);
        Assert.Equal(version < 3 ? 512f : 64f, doc.TileSize);
        Assert.Equal(-100f, doc.PlayableBounds!.MinX);
        Assert.Equal(100f, doc.PlayableBounds.MaxZ);
        Assert.Equal("legacy", doc.Id);
        Assert.Equal("old-inn", Assert.Single(doc.Placements).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveLoad_RetainsNativeMetadataAndRestrictedPlayableBounds(bool emptySculpt)
    {
        var doc = MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json());
        doc.PlayableBounds = new MapBounds { MinX = -40, MinZ = -50, MaxX = 40, MaxZ = 50 };
        doc.NativeAssets.Add(new MapAssetRef("inn", "assets/inn.json", new string('a', 64), 1));
        doc.ResolverIdentity = new MapResolverIdentityDoc(1, 2);
        doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
        doc.NumericIdHighWaterMark = 9;
        doc.Placements[0].NumericId = 7;
        doc.Placements[0].AssetId = "inn";
        doc.Placements[0].DisplayName = "The inn";
        if (emptySculpt) doc.TerrainOverrides = new MapTerrainOverrides();
        string json = MapDocumentFile.SaveText(doc);
        var loaded = MapDocumentFile.LoadText(json);
        Assert.Equal(-40f, loaded.PlayableBounds!.MinX);
        Assert.Equal(50f, loaded.PlayableBounds.MaxZ);
        Assert.Equal(doc.NativeAssets[0], Assert.Single(loaded.NativeAssets));
        Assert.Equal(doc.ResolverIdentity, loaded.ResolverIdentity);
        Assert.Equal(doc.SupportRecipe, loaded.SupportRecipe);
        Assert.Equal(9L, loaded.NumericIdHighWaterMark);
        Assert.Equal(7L, loaded.Placements[0].NumericId);
        Assert.Equal("inn", loaded.Placements[0].AssetId);
        Assert.Equal("The inn", loaded.Placements[0].DisplayName);
        Assert.Equal(json, MapDocumentFile.SaveText(loaded));
    }

    [Fact]
    public void NativeDefaults_SaveAndLoadWithoutLaterTaskTypes()
    {
        var doc = MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json());
        doc.PlayableBounds = null;
        var loaded = MapDocumentFile.LoadText(MapDocumentFile.SaveText(doc));
        Assert.Null(loaded.PlayableBounds);
        Assert.Null(loaded.ResolverIdentity);
        Assert.Empty(loaded.NativeAssets);
        Assert.Equal(0L, loaded.NumericIdHighWaterMark);
    }

    [Theory]
    [InlineData("playableBounds")]
    [InlineData("resolverIdentity")]
    [InlineData("nativeAssets")]
    [InlineData("placements")]
    public void Load_RejectsUnknownNativeMembers(string member)
    {
        var root = MapNativeMigration.Upgrade(JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject());
        root["resolverIdentity"] = JsonNode.Parse("""{"payloadVersion":1,"resolverVersion":1}""");
        root["nativeAssets"] = JsonNode.Parse("""[{"id":"inn","path":"inn.json","sha256":"abc","payloadVersion":1}]""");
        JsonNode target = root[member]!;
        if (target is JsonArray list) target = list[0]!;
        target["typo"] = true;
        Assert.Throws<MapDocumentException>(() => { MapDocumentFile.LoadText(root.ToJsonString()); });
    }

    [Theory]
    [InlineData(-101f, 100f)]
    [InlineData(-100f, 101f)]
    [InlineData(0f, 0f)]
    [InlineData(1f, -1f)]
    [InlineData(float.NegativeInfinity, 100f)]
    [InlineData(-100f, float.PositiveInfinity)]
    [InlineData(float.NaN, 100f)]
    public void Validate_RejectsInvalidPlayableBoundsOnBothAxes(float min, float max)
    {
        var doc = MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json());
        doc.PlayableBounds = new MapBounds { MinX = min, MaxX = max, MinZ = -100, MaxZ = 100 };
        Assert.NotEmpty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
        Assert.Throws<MapDocumentException>(() => { MapDocumentFile.SaveText(doc); });
        doc.PlayableBounds = new MapBounds { MinX = -100, MaxX = 100, MinZ = min, MaxZ = max };
        Assert.NotEmpty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
    }

    [Fact]
    public void Upgrade_PreservesExplicitPlayableBoundsAndRejectsWrongSourceVersion()
    {
        var root = JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();
        root["playableBounds"] = JsonNode.Parse("""{"minX":-40,"minZ":-50,"maxX":40,"maxZ":50}""");
        Assert.Equal(root["playableBounds"]!.ToJsonString(), MapNativeMigration.Upgrade(root)["playableBounds"]!.ToJsonString());
        root["formatVersion"] = 5;
        Assert.Throws<MapDocumentException>(() => { MapNativeMigration.Upgrade(root); });
        root.Remove("formatVersion");
        Assert.Throws<MapDocumentException>(() => { MapNativeMigration.Upgrade(root); });
    }
}
