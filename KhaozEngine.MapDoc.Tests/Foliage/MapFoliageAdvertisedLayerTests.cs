using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageAdvertisedLayerTests
{
    [Fact]
    public void WindowedLayer_CarriesOnlyMatchingSameLayerAdvertisedDensity()
    {
        var fixture = FoliageStorageFixtures.RegionFixture();
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        var window = MapDocumentFile.LoadTiled(dir.Root, new MapTileRect(new(1, 0), new(1, 0)));
        var advertised = window.Foliage!.Layers[0];
        Assert.True(advertised.Chunks.Count < advertised.ChunkDigests.Count);
        var full = MapDocumentFile.LoadTiled(dir.Root);
        var before = full.Foliage!.Layers[0];

        var changed = MapFoliageEdits.Prepare(full, fixture.Assets, new MapSetFoliageLayer(advertised));

        var accepted = Assert.Single(changed.After.Foliage!.Layers);
        Assert.Equal(before.ChunkDigests, accepted.ChunkDigests);
        Assert.Equal(before.Chunks.Select(c => Convert.ToBase64String(c.CopyDensity())),
            accepted.Chunks.Select(c => Convert.ToBase64String(c.CopyDensity())));
        Assert.Equal(before.ChunkDigests.Count, accepted.Chunks.Count);

        var missing = advertised.ChunkDigests.First(d => advertised.Chunks.All(c => c.Key != d.Key));
        var foreignDigest = MapFoliageLayer.FromStorage(advertised,
            advertised.ChunkDigests.Select(d => d.Key == missing.Key ? d with { Sha256 = new string('0', 64) } : d).ToArray(), advertised.Chunks);
        var foreignLayer = FoliageStorageFixtures.Change(before, l => l["id"] = "another-layer");
        foreignLayer = MapFoliageLayer.FromStorage(foreignLayer, advertised.ChunkDigests, advertised.Chunks);
        Assert.Contains("without its density bytes", Assert.Throws<MapDocumentException>(() =>
            MapFoliageEdits.Prepare(full, fixture.Assets, new MapSetFoliageLayer(foreignDigest))).Message, StringComparison.Ordinal);
        Assert.Contains("without its density bytes", Assert.Throws<MapDocumentException>(() =>
            MapFoliageEdits.Prepare(full, fixture.Assets, new MapSetFoliageLayer(foreignLayer))).Message, StringComparison.Ordinal);
    }
}
