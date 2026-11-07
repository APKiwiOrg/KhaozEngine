using System.IO;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class TilePersistenceAdmissionTests
{
    [Fact]
    public void TileIndex_IdentifiesTheRepeatedCoordinate()
    {
        var tile = new MapTileCoord(-2, 3);
        MapTileEntry first = new(tile, "first", true);
        var error = Assert.Throws<MapDocumentException>(() => new MapTileIndex(512, 1, "source", [first, first]));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal(tile, error.Tile);
        Assert.Equal("tile index entry", error.SubjectKind);
        Assert.Equal("source", error.SourceName);
        var control = new MapTileIndex(512, 1, "source", [first, first with { Coord = new(-1, 3) }]);
        Assert.Equal(2, control.Entries.Count);
    }

    [Fact]
    public void TileRead_IdentifiesTheMissingFileAndLoadsTheRepairedFile() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
        using var source = MapDocumentSource.OpenTiled(directory);
        MapTileEntry entry = source.Tiles.Entries[0];
        string path = MapTileFile.PathOf(directory, entry.Coord, entry.Hash);
        byte[] bytes = File.ReadAllBytes(path);
        File.Delete(path);
        var before = AssertFixtures.Files(directory);
        var error = Assert.Throws<MapDocumentException>(() => source.ReadTile(entry.Coord));
        Assert.Equal(MapDocumentRefusal.DocumentTileReadFailed, error.Reason);
        Assert.Equal(entry.Coord, error.Tile);
        Assert.Equal(directory, error.SourceName);
        Assert.Equal(path, error.Path);
        Assert.Equal(entry.Hash, error.ExpectedHash);
        Assert.Equal(before, AssertFixtures.Files(directory));
        File.WriteAllBytes(path, bytes);
        Assert.Equal(entry.Coord, source.ReadTile(entry.Coord).Coord);
    });
}
