using System;
using System.IO;
using System.Text.Json.Nodes;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using KhaozEngine.Tests.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class FoliageStorageSchemaTests
{
    [Fact]
    public void InlineAndStoredSchemas_AgreeWithTheirOwnDecoders()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageFixtures.Create("decorated");
        string inline = MapDocumentFile.SaveText(fixture.Document);
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        JsonObject manifest = FoliageStorageFixtures.Manifest(dir.Root);
        JsonObject mono = JsonNode.Parse(inline)!.AsObject();
        Assert.True(JsonSchemaValidator.Validate(inline, MapDocumentSchema.GetJson()).IsValid);
        Assert.True(JsonSchemaValidator.Validate(manifest.ToJsonString(), MapDocumentSchema.GetManifestJson()).IsValid);
        Assert.NotNull(manifest["foliage"]!["packs"]);
        var storedForInline = mono.DeepClone().AsObject(); storedForInline["foliage"] = manifest["foliage"]!.DeepClone();
        var inlineForStored = manifest.DeepClone().AsObject(); inlineForStored["foliage"] = mono["foliage"]!.DeepClone();
        Assert.False(JsonSchemaValidator.Validate(storedForInline.ToJsonString(), MapDocumentSchema.GetJson()).IsValid);
        Assert.False(JsonSchemaValidator.Validate(inlineForStored.ToJsonString(), MapDocumentSchema.GetManifestJson()).IsValid);
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(storedForInline.ToJsonString()));
        File.WriteAllText(Path.Combine(dir.Root, "map.json"), inlineForStored.ToJsonString());
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadTiled(dir.Root));
    }

    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(false, "null")]
    [InlineData(false, "duplicate")]
    [InlineData(true, "unknown")]
    [InlineData(true, "null")]
    [InlineData(true, "duplicate")]
    [InlineData(true, "chunk-inline")]
    [InlineData(true, "pack-unknown")]
    [InlineData(true, "pack-null")]
    public void ClosedStorageForms_RefuseUnknownNullAndDuplicateMembers(bool stored, string failure)
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageFixtures.Create();
        JsonObject root;
        if (stored) { MapDocumentFile.SaveTiled(fixture.Document, dir.Root); root = FoliageStorageFixtures.Manifest(dir.Root); }
        else root = JsonNode.Parse(MapDocumentFile.SaveText(fixture.Document))!.AsObject();
        Assert.True(JsonSchemaValidator.Validate(root.ToJsonString(), stored ? MapDocumentSchema.GetManifestJson() : MapDocumentSchema.GetJson()).IsValid);
        JsonObject foliage = root["foliage"]!.AsObject();
        switch (failure)
        {
            case "unknown": foliage["extra"] = 1; break;
            case "null": foliage["layers"] = null; break;
            case "chunk-inline": foliage["layers"]![0]!["chunks"]![0]!["density"] = Convert.ToBase64String(new byte[1024]); break;
            case "pack-unknown": foliage["packs"]![0]!["extra"] = 1; break;
            case "pack-null": foliage["packs"]![0]!["region"] = null; break;
        }
        string json = root.ToJsonString();
        if (failure == "duplicate") json = json.Replace("\"foliage\":{\"payloadVersion\":1,", "\"foliage\":{\"payloadVersion\":1,\"payloadVersion\":1,", StringComparison.Ordinal);
        // JSON Schema consumes a parsed value, so duplicate members are the documented runtime-only check.
        if (failure != "duplicate") Assert.False(JsonSchemaValidator.Validate(json, stored ? MapDocumentSchema.GetManifestJson() : MapDocumentSchema.GetJson()).IsValid);
        if (stored)
        {
            File.WriteAllText(Path.Combine(dir.Root, "map.json"), json);
            Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadTiled(dir.Root));
        }
        else Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(json));
    }
}
