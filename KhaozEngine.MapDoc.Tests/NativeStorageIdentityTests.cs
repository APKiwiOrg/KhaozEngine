using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeStorageIdentityTests
{
    [Fact]
    public void NativeIdentity_PartialDocumentCannotClaimCompleteWorld()
    {
        using var storage = new NativeStorageFixture();
        var f = NativeResolverFixtures.Create();
        var partial = storage.LoadWindow();
        Assert.True(partial.Tiles!.IsPartial);
        Assert.Single(partial.Placements);
        Assert.Throws<MapDocumentException>(() => MapAuthoredIdentity.Compute(partial, f.Assets, f.Options));
        Assert.Throws<MapDocumentException>(() => MapResolver.Resolve(partial, f.Assets, (_, _) => 0, f.Options));
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(partial));
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(f.Document, storage.TiledPath));
    }

    [Fact]
    public void NativeIdentity_CompleteTiledDirtyClosureAndRetileAreDistinct()
    {
        using var storage = new NativeStorageFixture();
        var f = NativeResolverFixtures.Create();
        var monolithic = MapDocumentFile.LoadText(MapDocumentFile.SaveText(storage.Complete));
        var tiled = MapDocumentFile.LoadTiled(storage.TiledPath);
        string expected = MapAuthoredIdentity.Compute(monolithic, f.Assets, f.Options);
        Assert.Equal(expected, MapAuthoredIdentity.Compute(tiled, f.Assets, f.Options));
        var before = MapResolver.Resolve(tiled, f.Assets, (_, _) => 7, f.Options);
        tiled.Placements[0].X += 0.23f;
        Assert.NotEqual(expected, MapAuthoredIdentity.Compute(tiled, f.Assets, f.Options));
        tiled = MapDocumentFile.LoadTiled(storage.TiledPath);
        tiled.DisplayName = "label only";
        tiled.Schema = "label-only-schema.json";
        tiled.Placements[0].DisplayName = "placement label";
        Assert.Equal(expected, MapAuthoredIdentity.Compute(tiled, f.Assets, f.Options));
        var changed = NativeResolverFixtures.WithChangedClosure(tiled, f.Assets);
        Assert.NotEqual(expected, MapAuthoredIdentity.Compute(changed.Document, changed.Assets, f.Options));
        Assert.Throws<MapDocumentException>(() => MapAuthoredIdentity.Compute(tiled, changed.Assets, f.Options));
        Assert.Throws<MapDocumentException>(() => MapResolver.Resolve(tiled, changed.Assets, (_, _) => 7, f.Options));
        tiled.TileSize = 128;
        Assert.NotEqual(expected, MapAuthoredIdentity.Compute(tiled, f.Assets, f.Options));
        Assert.Equal(before.Placements.Select(p => p.Transform), MapResolver.Resolve(tiled, f.Assets, (_, _) => 7, f.Options).Placements.Select(p => p.Transform));
    }

    [Theory]
    [InlineData("tags")]
    [InlineData("terrain")]
    [InlineData("bounds")]
    [InlineData("high-water")]
    [InlineData("builder")]
    [InlineData("builder-version")]
    [InlineData("options")]
    public void NativeIdentity_CoversCompleteContentAndBuildInputs(string change)
    {
        var f = NativeResolverFixtures.Create();
        string before = MapAuthoredIdentity.Compute(f.Document, f.Assets, f.Options);
        switch (change)
        {
            case "tags": f.Document.Placements[0].Tags.Reverse(); break;
            case "terrain": f.Document.Terrain.Seed++; break;
            case "bounds": f.Document.PlayableBounds!.MinX++; break;
            case "high-water": f.Document.NumericIdHighWaterMark++; break;
            case "builder": f.Options = f.Options with { BuilderId = "other" }; break;
            case "builder-version": f.Options = f.Options with { BuilderVersion = 2 }; break;
            case "options": f.Options = f.Options with { OptionsHash = "other" }; break;
        }
        Assert.NotEqual(before, MapAuthoredIdentity.Compute(f.Document, f.Assets, f.Options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeStorage_ExactInt64DeletionReloadNeverReuses(bool tiled)
    {
        using var storage = new NativeStorageFixture();
        var doc = MapDocumentFile.LoadTiled(storage.TiledPath);
        doc.Placements[0].NumericId = 9007199254740993L;
        MapNumericIds.Reserve(doc, Array.Empty<long>());
        string text;
        if (tiled)
        {
            MapDocumentFile.SaveTiled(doc, storage.TiledPath);
            text = File.ReadAllText(Path.Combine(storage.TiledPath, "map.json"));
            Assert.Contains(Directory.GetFiles(storage.TiledPath, "t_*.json", SearchOption.AllDirectories),
                p => File.ReadAllText(p).Contains("\"numericId\": \"9007199254740993\"", StringComparison.Ordinal));
            doc = MapDocumentFile.LoadTiled(storage.TiledPath);
        }
        else
        {
            text = MapDocumentFile.SaveText(doc);
            doc = MapDocumentFile.LoadText(text);
        }
        Assert.Equal("9007199254740993", JsonNode.Parse(text)!["numericIdHighWaterMark"]!.GetValue<string>());
        Assert.Contains(doc.Placements, p => p.NumericId == 9007199254740993L);
        doc.Placements.RemoveAll(p => p.NumericId == 9007199254740993L);
        if (tiled)
        {
            MapDocumentFile.SaveTiled(doc, storage.TiledPath);
            doc = MapDocumentFile.LoadTiled(storage.TiledPath);
        }
        else doc = MapDocumentFile.LoadText(MapDocumentFile.SaveText(doc));
        Assert.Equal(9007199254740994L, MapNumericIds.Allocate(doc));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("native-ref")]
    [InlineData("tile")]
    [InlineData("placement")]
    public void NativeStorage_LoadActuallyRejectsUnknownMembers(string target)
    {
        using var storage = new NativeStorageFixture();
        string path = target is "manifest" or "native-ref" ? Path.Combine(storage.TiledPath, "map.json") :
            Directory.GetFiles(storage.TiledPath, "t_*.json", SearchOption.AllDirectories)[0];
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        JsonObject modified = target switch
        {
            "native-ref" => root["nativeAssets"]![0]!.AsObject(),
            "placement" => root["placements"]![0]!.AsObject(),
            _ => root
        };
        modified["unknownNativeMember"] = 17;
        File.WriteAllText(path, root.ToJsonString());
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadTiled(storage.TiledPath));
    }
}
