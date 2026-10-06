using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
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
