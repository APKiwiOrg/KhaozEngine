using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneCapacityBoundaryTests
{
    [Fact]
    public void SelectedCapacity_StopsHierarchyBeforeListingAllOverlappingRows()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        for (int i = 0; i < 136; i++)
            fixture.Document.Placements.Add(new()
            {
                Id = "overlap-" + i,
                Kind = "cosmetic",
                AssetId = "branch",
                X = 0,
                Y = 0,
                Z = 0,
                NumericId = ++fixture.Document.NumericIdHighWaterMark
            });
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var source = MapSceneSource.Capture(document, fixture.Assets, new(MaxSelectedResources: 0)).Source!;
        var result = source.Acquire(MapSceneScopeTests.Window());
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Null(result.Scene);
        Assert.Equal(0, result.Work.PayloadReads);
        Assert.InRange(result.Work.IndexNodesVisited, 1L, 6L);
    }

    [Fact]
    public void PartialInMemorySource_KeepsReleasedLoadedEntriesAndContentHashes()
    {
        using var fixture = NativeSceneFixtures.Create("growth");
        using var disk = NativeSceneFixtures.OpenTiled(fixture);
        var coord = disk.Tiles.Entries.First().Coord;
        var partial = MapDocumentFile.LoadTiled(disk.Tiles.SourceDirectory!, new MapTileRect(coord, coord));
        Assert.True(partial.Tiles!.IsPartial);
        partial.Placements.Single().Y = 123;
        using var memory = MapDocumentSource.FromDocument(partial);
        var entry = Assert.Single(memory.Tiles.Entries);
        Assert.False(memory.Tiles.IsPartial);
        Assert.True(entry.Loaded);
        Assert.Equal(coord, entry.Coord);
        Assert.NotEqual(partial.Tiles.Entries.Single(e => e.Coord == coord).Hash, entry.Hash);
        Assert.Equal(123, Assert.Single(memory.ReadTile(coord).Placements).Y);
        var unloaded = partial.Tiles.Entries.First(e => !e.Loaded).Coord;
        Assert.Throws<MapDocumentException>(() => memory.ReadTile(unloaded));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptiedLoadedTile_CapturesWithoutThrowing(bool partialLoad)
    {
        using var fixture = NativeSceneFixtures.Create("growth");
        using var disk = NativeSceneFixtures.OpenTiled(fixture);
        var coord = disk.Tiles.Entries.First().Coord;
        var document = partialLoad
            ? MapDocumentFile.LoadTiled(disk.Tiles.SourceDirectory!, new MapTileRect(coord, coord))
            : MapDocumentFile.LoadTiled(disk.Tiles.SourceDirectory!);
        document.Placements.Clear();
        using var memory = MapDocumentSource.FromDocument(document);
        var result = MapSceneSource.Capture(memory, fixture.Assets);
        Assert.Equal(partialLoad ? MapSceneSourceStatus.Incomplete : MapSceneSourceStatus.Ready, result.Status);
        Assert.Empty(memory.Tiles.Entries);
    }
}
