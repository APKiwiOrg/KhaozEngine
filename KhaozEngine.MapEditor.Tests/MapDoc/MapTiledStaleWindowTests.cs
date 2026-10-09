using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapTiledStaleWindowTests
{
    static readonly MapTileRect OriginWindow = new(new MapTileCoord(0, 0), new MapTileCoord(0, 0));

    [Fact]
    public void StaleWindow_RefusesAfterAnotherWriterReplacedAnUnloadedTile() => TiledDocFixture.InDirectory(dir =>
    {
        string manifest = Path.Combine(dir, "map.json");
        MapDocument window = SaveAndOpenWindow(dir);
        ReplaceUnloadedTile(dir);
        byte[] newer = File.ReadAllBytes(manifest);
        var newerFiles = TiledDocFixture.TileFiles(dir);
        P(window, "p-a").Yaw = 2.75f;
        Assert.Contains("stale window", SaveRefusal(window, dir));
        Assert.Equal(newer, File.ReadAllBytes(manifest));
        Assert.Equal(newerFiles, TiledDocFixture.TileFiles(dir));
        Assert.Empty(MapDocumentFile.VerifyTiled(dir));
        MapDocument reloaded = MapDocumentFile.LoadTiled(dir);
        Assert.Equal(("barn", 0.5f), (P(reloaded, "p-c").Kind, P(reloaded, "p-a").Yaw));
    });

    [Fact]
    public void StaleWindow_RefusesBeforeTouchingTheManifestTemp() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocument window = SaveAndOpenWindow(dir);
        ReplaceUnloadedTile(dir);
        string temp = Path.Combine(dir, "map.json.tmp");
        File.WriteAllText(temp, "left by a crashed writer");
        Assert.Contains("stale window", SaveRefusal(window, dir));
        Assert.Equal("left by a crashed writer", File.ReadAllText(temp));
    });

    [Theory]
    [InlineData("garbage")]
    [InlineData("missing")]
    public void StaleWindow_RefusesWhenTheManifestIsUnreadableOrMissing(string fault) => TiledDocFixture.InDirectory(dir =>
    {
        MapDocument window = SaveAndOpenWindow(dir);
        string manifest = Path.Combine(dir, "map.json");
        if (fault == "garbage") File.WriteAllText(manifest, "{");
        else File.Delete(manifest);
        var files = TiledDocFixture.TileFiles(dir);
        P(window, "p-a").Yaw = 2.75f;
        Assert.Contains("stale window", SaveRefusal(window, dir));
        Assert.Equal(files, TiledDocFixture.TileFiles(dir));
        Assert.Equal(fault == "garbage", File.Exists(manifest));
        if (fault == "garbage") Assert.Equal("{", File.ReadAllText(manifest));
    });

    [Fact]
    public void CurrentWindow_SavesAndRefreshesItsGenerationWitness() => TiledDocFixture.InDirectory(dir =>
    {
        string manifest = Path.Combine(dir, "map.json");
        MapDocument window = SaveAndOpenWindow(dir);
        string loaded = Sha256(manifest);
        Assert.Equal(loaded, window.Tiles!.ManifestSha256);
        P(window, "p-a").Yaw = 2.75f;
        MapDocumentFile.SaveTiled(window, dir);
        Assert.True(window.Tiles!.IsPartial);
        Assert.Equal(Sha256(manifest), window.Tiles.ManifestSha256);
        Assert.NotEqual(loaded, window.Tiles.ManifestSha256);
        P(window, "p-a").Yaw = 3.5f;
        MapDocumentFile.SaveTiled(window, dir);
        MapDocument whole = MapDocumentFile.LoadTiled(dir);
        Assert.Equal((3.5f, "hut"), (P(whole, "p-a").Yaw, P(whole, "p-c").Kind));
    });

    [Fact]
    public void GenerationWitness_FlowsThroughEveryManifestReaderAndTransactionClone() => TiledDocFixture.InDirectory(dir =>
    {
        string manifest = Path.Combine(dir, "map.json");
        MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), dir);
        string first = Sha256(manifest);
        MapDocumentSource source = MapDocumentSource.OpenTiled(dir);
        Assert.Equal((first, first), (source.Tiles.ManifestSha256, source.Manifest.Tiles!.ManifestSha256));
        MapDocument whole = MapDocumentFile.LoadTiled(dir);
        Assert.Equal(first, whole.Tiles!.ManifestSha256);
        Assert.Same(whole.Tiles, NativeDocumentSnapshot.Clone(whole, MapDocRegistry.CreateDefault()).Tiles);
        P(whole, "p-b").Yaw = 1.25f;
        MapDocumentFile.SaveTiled(whole, dir);
        source.Refresh();
        Assert.Equal(Sha256(manifest), source.Tiles.ManifestSha256);
        Assert.NotEqual(first, source.Tiles.ManifestSha256);
        Assert.Null(MapDocumentSource.FromDocument(TiledDocFixture.SampleDoc()).Tiles.ManifestSha256);
    });

    [Fact]
    public void WholeDocumentAndSaveAsKeepReleasedBehaviour() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), dir);
        MapDocument older = MapDocumentFile.LoadTiled(dir), newer = MapDocumentFile.LoadTiled(dir);
        P(newer, "p-c").Kind = "barn";
        MapDocumentFile.SaveTiled(newer, dir);
        P(older, "p-a").Yaw = 2.75f;
        MapDocumentFile.SaveTiled(older, dir);
        Assert.Equal("hut", P(MapDocumentFile.LoadTiled(dir), "p-c").Kind);
        TiledDocFixture.InDirectory(other =>
        {
            string copy = Path.Combine(other, "copy");
            MapDocumentFile.SaveAs(older, copy, MapDocumentForm.Tiled);
            Assert.Empty(MapDocumentFile.VerifyTiled(copy));
        });
    });

    static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    static MapPlacement P(MapDocument doc, string id) => doc.Placements.Single(p => p.Id == id);

    static MapDocument SaveAndOpenWindow(string dir)
    {
        MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), dir);
        return MapDocumentFile.LoadTiled(dir, OriginWindow);
    }

    static void ReplaceUnloadedTile(string dir)
    {
        MapDocument whole = MapDocumentFile.LoadTiled(dir);
        P(whole, "p-c").Kind = "barn";
        MapDocumentFile.SaveTiled(whole, dir);
    }

    static string SaveRefusal(MapDocument doc, string dir) =>
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(doc, dir)).Message;
}
