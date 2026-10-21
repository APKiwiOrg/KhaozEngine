using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageReviewTests
{
    [Fact]
    public void StoredIdentity_AppliesTheDocumentGateOnceAcrossManyTiles()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageStorageFixtures.ManyPacks(withTiles: true);
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        var source = MapStoredSurfaceSource.Open(dir.Root);
        using var work = MapFoliageStorageWork.Capture();
        _ = MapAuthoredIdentityV2.Compute(source, fixture.Assets, FoliageFixtures.Options());
        Assert.Equal(1, work.DocumentGates);
    }

    [Fact]
    public void ManyPacks_LoadAndExistingPayloadLookupVisitLinearMetadata()
    {
        const int count = 64;
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageStorageFixtures.ManyPacks(count);
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        using (var work = MapFoliageStorageWork.Capture())
        {
            MapDocument loaded = MapDocumentFile.LoadTiled(dir.Root);
            Assert.Equal(count, loaded.Foliage!.Layers[0].Chunks.Count);
            Assert.Equal(count, work.ChunkInspections);
            Assert.Equal(count, work.PackReads);
        }
        var index = fixture.Document.Tiles!.Foliage!;
        var payloads = index.Packs.Select(p =>
        {
            string path = MapFoliageStorageIndex.RelativePath(p);
            return (path, (ReadOnlyMemory<byte>)File.ReadAllBytes(Path.Combine(dir.Root, path)));
        }).ToArray();
        var prepared = new MapFoliagePreparedStorage(index, payloads, new HashSet<string>());
        using (var work = MapFoliageStorageWork.Capture())
        {
            MapFoliageStorage.Write(dir.Root, prepared, new MapDocumentSaveOptions(), []);
            Assert.Equal(count, work.PackReads);
            Assert.Equal(0, work.PackWrites);
            Assert.InRange(work.PackPathFormats, count, 2 * count);
        }
    }

    [Fact]
    public void Save_UnchangedAndOneChunkEditsTouchOnlyExpectedPacks()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageStorageFixtures.ManyPacks();
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        using (var work = MapFoliageStorageWork.Capture())
        {
            MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
            Assert.Equal((0, 0, 0), (work.PackReads, work.PackWrites, work.PackEncodes));
        }
        ChangeFirst(fixture.Document);
        using (var work = MapFoliageStorageWork.Capture())
        {
            MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
            Assert.Equal((0, 1, 1), (work.PackReads, work.PackWrites, work.PackEncodes));
        }
        var partial = MapDocumentFile.LoadTiled(dir.Root, new MapTileRect(new(0, 0), new(0, 0)));
        Assert.True(partial.Tiles!.IsPartial);
        using (var work = MapFoliageStorageWork.Capture())
        {
            MapDocumentFile.SaveTiled(partial, dir.Root);
            Assert.Equal((0, 0, 0), (work.PackReads, work.PackWrites, work.PackEncodes));
        }
        ChangeFirst(partial);
        using (var work = MapFoliageStorageWork.Capture())
        {
            MapDocumentFile.SaveTiled(partial, dir.Root);
            Assert.Equal((0, 1, 1), (work.PackReads, work.PackWrites, work.PackEncodes));
        }
        Assert.Empty(MapDocumentFile.VerifyTiled(dir.Root));
    }

    [Fact]
    public void PartialPackEdit_ReadsOnlyItsHiddenRecords()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageStorageFixtures.RegionFixture();
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        var partial = MapDocumentFile.LoadTiled(dir.Root, new MapTileRect(new(1, 0), new(1, 0)));
        ChangeFirst(partial);
        using (var work = MapFoliageStorageWork.Capture())
        {
            MapDocumentFile.SaveTiled(partial, dir.Root);
            Assert.Equal((1, 1, 1), (work.PackReads, work.PackWrites, work.PackEncodes));
        }
        Assert.Equal((byte)10, MapDocumentFile.LoadTiled(dir.Root).Foliage!.Layers[0].DensityAt(0, 0));
        Assert.Empty(MapDocumentFile.VerifyTiled(dir.Root));
    }

    [Fact]
    public void StoredIdentity_DoesNotRetainCompleteFoliageOnPinnedSource()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageFixtures.Create();
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        var source = MapStoredSurfaceSource.Open(dir.Root);
        var globals = (MapDocument)typeof(MapStoredSurfaceSource).GetField("_globals", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        MapFoliageSet metadata = globals.Foliage!;
        Assert.Empty(metadata.Layers[0].Chunks);
        string first = MapAuthoredIdentityV2.Compute(source, fixture.Assets, FoliageFixtures.Options());
        Assert.Same(metadata, globals.Foliage);
        Assert.Empty(globals.Foliage!.Layers[0].Chunks);
        Assert.Equal(first, MapAuthoredIdentityV2.Compute(source, fixture.Assets, FoliageFixtures.Options()));
        Assert.Same(metadata, globals.Foliage);
    }

    [Fact]
    public void ImmutableDensityDigest_IsComputedOnceAcrossLayerCopies()
    {
        byte[] bytes = new byte[1024];
        string expected = FoliageStorageFixtures.Hash(bytes);
        using var work = MapFoliageStorageWork.Capture();
        var chunk = new MapFoliageChunk(new(0, 0), bytes);
        bytes[0] = 1;
        var layer = FoliageFixtures.BasicLayer([chunk]);
        var set = new MapFoliageSet(1, [layer]);
        Assert.Equal(expected, MapFoliageLayer.Digest(chunk));
        Assert.Equal(expected, set.Clone().Clone().Layers[0].ChunkDigests[0].Sha256);
        Assert.Equal(1, work.ChunkHashes);
    }

    static void ChangeFirst(MapDocument document)
    {
        var layer = document.Foliage!.Layers[0];
        MapFoliageChunk first = layer.Chunks[0];
        byte[] bytes = first.CopyDensity(); bytes[0] ^= 128;
        document.Foliage = new(1, [MapFoliageLayer.FromStorage(layer, layer.ChunkDigests,
            layer.Chunks.Select(c => c.Key == first.Key ? new MapFoliageChunk(c.Key, bytes) : c).ToArray())]);
    }
}
