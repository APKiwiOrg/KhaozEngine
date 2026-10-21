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
    public void PartialInMemorySource_RefusesReadingUnloadedTile()
    {
        using var fixture = NativeSceneFixtures.Create("growth");
        using var disk = NativeSceneFixtures.OpenTiled(fixture);
        var partial = MapDocumentFile.LoadTiled(disk.Tiles.SourceDirectory!, new MapTileRect(new(-1, -1), new(-1, -1)));
        using var memory = MapDocumentSource.FromDocument(partial);
        var unloaded = memory.Tiles.Entries.First(e => !e.Loaded).Coord;
        Assert.Throws<MapDocumentException>(() => memory.ReadTile(unloaded));
    }
}
