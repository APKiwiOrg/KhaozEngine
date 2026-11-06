using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliagePackRefusalTests
{
    [Theory]
    [InlineData("drop", MapDocumentRefusal.FoliagePartialSaveUnsafe)]
    [InlineData("padding", MapDocumentRefusal.FoliagePaddingNonzero)]
    public void PartialPackPreparation_PreservesUnavailableRowsAndChecksTheirNewPadding(string fault, MapDocumentRefusal reason)
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageFixtures.Create();
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        var partial = MapDocumentFile.LoadTiled(dir.Root, new MapTileRect(new(100, 100), new(100, 100)));
        var original = partial.Foliage!;
        var layer = original.Layers[0];
        Assert.Empty(layer.Chunks);
        if (fault == "drop") partial.Foliage = null;
        else
        {
            var metadata = new FoliageLayerDraft(layer) { Width = 34 }.Build();
            partial.Foliage = new(1, [MapFoliageLayer.FromStorage(metadata, layer.ChunkDigests, [])]);
        }
        var proposed = partial.Foliage;
        string before = FoliageStorageFixtures.Snapshot(partial);
        var error = Assert.Throws<MapDocumentException>(() => MapFoliagePackPreparation.Prepare(partial, dir.Root));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(layer.Id, error.LayerId);
        Assert.NotNull(error.FoliageChunk);
        if (fault == "drop") Assert.Equal(layer.ChunkDigests[0].Sha256, error.ExpectedHash);
        else
        {
            Assert.Equal(new MapFoliageChunkKey(1, 0), error.FoliageChunk);
            Assert.Equal(2, error.Index);
            Assert.Equal(2, error.CellX);
            Assert.Equal(34L, error.ActualInteger);
            Assert.Single(error.Findings!);
        }
        Assert.Same(proposed, partial.Foliage);
        Assert.Equal(before, FoliageStorageFixtures.Snapshot(partial));
        partial.Foliage = original;
        Assert.NotNull(MapFoliagePackPreparation.Prepare(partial, dir.Root).Index);
    }

    [Fact]
    public void PackMaterialization_RefusesMissingDensityAndAcceptsTheSameResidentBytes()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageFixtures.Create();
        var layer = fixture.Document.Foliage!.Layers[0];
        fixture.Document.Foliage = new(1, [MapFoliageLayer.FromStorage(layer, layer.ChunkDigests, [])]);
        var original = fixture.Document.Foliage;
        string before = FoliageStorageFixtures.Snapshot(fixture.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapFoliagePackPreparation.Prepare(fixture.Document, dir.Root));
        Assert.Equal(MapDocumentRefusal.FoliageChunkUnavailable, error.Reason);
        Assert.Equal(layer.Id, error.LayerId);
        Assert.Contains(layer.ChunkDigests, d => d.Key == error.FoliageChunk && d.Sha256 == error.ExpectedHash);
        Assert.Same(original, fixture.Document.Foliage);
        Assert.Equal(before, FoliageStorageFixtures.Snapshot(fixture.Document));
        fixture.Document.Foliage = new(1, [layer]);
        Assert.NotEmpty(MapFoliagePackPreparation.Prepare(fixture.Document, dir.Root).Index.Chunks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MembershipBudget_ReportsTheFirstNegativeInput(bool count)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageManifestBudget.CheckMemberships(count ? -1 : 0, count ? 130 : -1));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
        Assert.Equal(count ? "Count" : "MaxUtf8Bytes", error.FieldPath);
        Assert.Equal(-1L, error.ActualInteger);
        Assert.Equal(0L, error.MinimumInteger);
        MapFoliageManifestBudget.CheckMemberships(0, count ? 130 : 0);
    }
}
