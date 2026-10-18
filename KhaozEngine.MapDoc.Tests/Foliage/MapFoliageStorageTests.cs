using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageStorageTests
{
    [Fact]
    public void ChunkedMonolithicAndTiled_RoundTripEveryByteAndSetting()
    {
        FoliageFixture fixture = FoliageFixtures.Create("decorated");
        string expected = FoliageStorageFixtures.Json(fixture.Document.Foliage!);
        foreach (bool tiled in new[] { false, true })
        {
            FoliageFixture loaded = FoliageFixtures.Reload(fixture, tiled);
            Assert.Equal(expected, FoliageStorageFixtures.Json(loaded.Document.Foliage!));
            MapFoliageLayer layer = Assert.Single(loaded.Document.Foliage!.Layers);
            for (int z = 0; z < 3; z++)
                for (int x = 0; x < 35; x++) Assert.Equal((byte)(z * 35 + x), layer.DensityAt(x, z));
            Assert.Equal(layer.Chunks.Select(c => new MapFoliageChunkDigest(c.Key, FoliageStorageFixtures.Hash(c.CopyDensity()))), layer.ChunkDigests);
        }
    }

    [Fact]
    public void RegionPack_RewriteAndPartialCarryPreserveLogicalDigests()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        FoliageFixture fixture = FoliageStorageFixtures.RegionFixture();
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        var before = fixture.Document.Foliage!.Layers[0].ChunkDigests.ToArray();
        var partial = MapDocumentFile.LoadTiled(dir.Root, new MapTileRect(new(1, 0), new(1, 0)));
        Assert.True(partial.Tiles!.Foliage!.IsPartial);
        var layer = partial.Foliage!.Layers[0];
        Assert.Equal(new[] { new MapFoliageChunkKey(7, 0), new(8, 0) }, layer.Chunks.Select(c => c.Key));
        Assert.Throws<MapDocumentException>(() => layer.DensityAt(0, 0));
        Assert.Equal(MapFoliageReadStatus.Absent, partial.Tiles.Foliage.Read(layer.Id, new(3, 0)).Status);
        Assert.Equal(before, layer.ChunkDigests);
        MapDocumentFile.SaveTiled(partial, dir.Root);
        Assert.Equal(before, MapDocumentFile.LoadTiled(dir.Root).Foliage!.Layers[0].ChunkDigests);
        byte[] changed = layer.Chunks[0].CopyDensity(); changed[0] = 200;
        partial.Foliage = new(1, [MapFoliageLayer.FromStorage(layer, layer.ChunkDigests,
            [new(new(7, 0), changed), layer.Chunks[1]])]);
        MapDocumentFile.SaveTiled(partial, dir.Root);
        MapDocument full = MapDocumentFile.LoadTiled(dir.Root);
        Assert.Equal((byte)10, full.Foliage!.Layers[0].DensityAt(0, 0));
        Assert.Equal((byte)200, full.Foliage.Layers[0].DensityAt(224, 0));
        Assert.Equal(before[0], full.Foliage.Layers[0].ChunkDigests[0]);
        Assert.Equal(before[2], full.Foliage.Layers[0].ChunkDigests[2]);
        Assert.Empty(MapDocumentFile.VerifyTiled(dir.Root));
        MapDocumentFile.SaveTiled(full, dir.Root);
        Assert.Empty(MapDocumentFile.VerifyTiled(dir.Root));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("stale")]
    public void AdvertisedChunkMissing_RefusesWithoutPublishingZero(string failure)
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageStorageFixtures.RegionFixture();
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        MapDocument partial = MapDocumentFile.LoadTiled(dir.Root, new MapTileRect(new(4, 4), new(4, 4)));
        Assert.True(partial.Tiles!.IsPartial);
        Assert.Empty(partial.Foliage!.Layers[0].Chunks);
        Assert.Throws<MapDocumentException>(() => partial.Foliage.Layers[0].DensityAt(0, 0));
        Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(partial, fixture.Assets, FoliageFixtures.Options()));
        string hash = partial.Tiles.Foliage!.Packs.Single(p => p.Region == new MapTileCoord(0, 0)).PayloadSha256;
        string path = Path.Combine(dir.Root, "tiles", "foliage", "r_0_0", hash + ".pack");
        if (failure == "missing") File.Delete(path);
        if (failure == "corrupt") File.WriteAllBytes(path, new byte[12]);
        if (failure == "stale") File.AppendAllText(Path.Combine(dir.Root, "map.json"), " ");
        byte[] beforeBytes = File.ReadAllBytes(Path.Combine(dir.Root, "map.json"));
        var read = partial.Tiles.Foliage!.Read(FoliageFixtures.LayerId, new(0, 0));
        if (failure != "stale") Assert.Contains(read.Status, new[] { MapFoliageReadStatus.Unavailable, MapFoliageReadStatus.Invalid });
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(partial, dir.Root));
        Assert.Equal(beforeBytes, File.ReadAllBytes(Path.Combine(dir.Root, "map.json")));
    }

    [Fact]
    public void RegionPack_UsesExactLayoutAndRawChunkDigests()
    {
        var chunks = Enumerable.Range(0, 64).Select(i => new MapFoliageChunk(new(i % 8, i / 8),
            Enumerable.Repeat((byte)i, 1024).ToArray())).ToArray();
        byte[] defaultRegionPack = MapFoliageRegionPack.Encode(chunks);
        Assert.Equal(68_108, defaultRegionPack.Length);
        Assert.Equal("KEFOLPK1", Encoding.ASCII.GetString(defaultRegionPack, 0, 8));
        Assert.Equal(64, BinaryPrimitives.ReadInt32LittleEndian(defaultRegionPack.AsSpan(8)));
        Assert.Equal(Convert.FromHexString(FoliageStorageFixtures.Hash(chunks[1].CopyDensity())), defaultRegionPack.AsSpan(1084, 32).ToArray());
        Assert.Equal(chunks[1].CopyDensity(), defaultRegionPack.AsSpan(1116, 1024).ToArray());
        Assert.Equal(chunks.Select(c => c.Key), MapFoliageRegionPack.Decode(defaultRegionPack).Select(c => c.Key));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("count")]
    [InlineData("capacity")]
    [InlineData("ordering")]
    [InlineData("duplicate")]
    [InlineData("digest")]
    public void RegionPack_RefusesMalformedRecords(string failure)
    {
        byte[] bytes = MapFoliageRegionPack.Encode(FoliageFixtures.BasicChunks());
        switch (failure)
        {
            case "length": Array.Resize(ref bytes, bytes.Length - 1); break;
            case "count": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1); break;
            case "capacity": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 16_385); break;
            case "ordering": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 2); break;
            case "duplicate": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1076), 0); break;
            case "digest": bytes[52] ^= 1; break;
        }
        Assert.Throws<MapDocumentException>(() => MapFoliageRegionPack.Decode(bytes));
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("orphan")]
    [InlineData("tmp")]
    public void VerifyTiled_RegistersAndChecksEveryFoliagePack(string failure)
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageStorageFixtures.RegionFixture();
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        Assert.Equal(2, FoliageStorageFixtures.Packs(dir.Root).Length);
        string path = FoliageStorageFixtures.Packs(dir.Root)[0];
        if (failure == "missing") File.Delete(path);
        if (failure == "corrupt") { byte[] bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes); }
        if (failure == "orphan") File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, new string('f', 64) + ".pack"));
        if (failure == "tmp") File.Copy(path, path + ".tmp");
        var errors = MapDocumentFile.VerifyTiled(dir.Root);
        if (failure == "clean") Assert.Empty(errors);
        else Assert.Contains(errors, e => e.Contains("foliage", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("chunk-digest")]
    [InlineData("count")]
    [InlineData("length")]
    [InlineData("owner")]
    [InlineData("membership")]
    [InlineData("duplicate")]
    public void Manifest_RefusesInvalidAdvertisedReferences(string failure)
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        MapDocumentFile.SaveTiled(FoliageStorageFixtures.RegionFixture().Document, dir.Root);
        JsonObject manifest = FoliageStorageFixtures.Manifest(dir.Root);
        JsonNode foliage = manifest["foliage"]!;
        JsonObject chunk = foliage["layers"]![0]!["chunks"]![0]!.AsObject();
        JsonObject pack = foliage["packs"]![0]!.AsObject();
        switch (failure)
        {
            case "offset": chunk["dataOffset"] = 53; break;
            case "chunk-digest": chunk["sha256"] = new string('0', 64); break;
            case "count": pack["chunkCount"] = 1; break;
            case "length": pack["byteLength"] = 12; break;
            case "owner": chunk["ownerRegion"]!["x"] = 100; break;
            case "membership": chunk["regions"] = new JsonArray(); break;
            case "duplicate": foliage["layers"]![0]!["chunks"]!.AsArray().Add(chunk.DeepClone()); break;
        }
        File.WriteAllText(Path.Combine(dir.Root, "map.json"), manifest.ToJsonString());
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadTiled(dir.Root));
        Assert.Contains(MapDocumentFile.VerifyTiled(dir.Root), e => e.Contains("foliage", StringComparison.Ordinal));
    }

    [Fact]
    public void RegionMembership_IncludesTerminalSampleAndRefusesCapacityBeforeEnumeration()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageStorageFixtures.RegionFixture();
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        var refs = fixture.Document.Tiles!.Foliage!.Chunks;
        Assert.Equal(new MapTileCoord(0, 0), refs[1].OwnerRegion);
        Assert.Equal(new[] { new MapTileCoord(0, 0), new(1, 0) }, refs[1].Regions);
        fixture.Document.Foliage = new(1, [FoliageStorageFixtures.Change(fixture.Document.Foliage!.Layers[0], l => l["cellSize"] = 100_000)]);
        byte[] before = File.ReadAllBytes(Path.Combine(dir.Root, "map.json"));
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(fixture.Document, dir.Root));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(dir.Root, "map.json")));
    }

    [Theory]
    [InlineData("pack-type")]
    [InlineData("region-type")]
    public void StoredDecoder_RefusesMalformedObjectTypesAsDocumentErrors(string failure)
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        MapDocumentFile.SaveTiled(FoliageFixtures.Create().Document, dir.Root);
        JsonObject root = FoliageStorageFixtures.Manifest(dir.Root);
        if (failure == "pack-type") root["foliage"]!["packs"]![0] = 1;
        else root["foliage"]!["packs"]![0]!["region"] = 1;
        using var json = System.Text.Json.JsonDocument.Parse(root["foliage"]!.ToJsonString());
        Assert.Throws<MapDocumentException>(() => MapFoliageStorage.ReadManifest(json.RootElement));
    }

    [Fact]
    public void SharedContentAddressedPacks_AreNamedOnceAndVerifiedForEveryLayer()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageStorageFixtures.RegionFixture();
        var first = fixture.Document.Foliage!.Layers[0];
        fixture.Document.Foliage = new(1, [first, FoliageStorageFixtures.Change(first, l => l["id"] = "other-layer")]);
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        Assert.Equal(4, fixture.Document.Tiles!.Foliage!.Packs.Count);
        Assert.Equal(2, FoliageStorageFixtures.Packs(dir.Root).Length);
        Assert.Empty(MapDocumentFile.VerifyTiled(dir.Root));
        Assert.Equal(2, MapDocumentFile.LoadTiled(dir.Root).Foliage!.Layers.Count);
    }

    [Fact]
    public void OversizedPinnedPack_ReturnsCapacityExceededBeforeReadingDensity()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        MapDocumentFile.SaveTiled(FoliageStorageFixtures.RegionFixture().Document, dir.Root);
        var partial = MapDocumentFile.LoadTiled(dir.Root, new MapTileRect(new(4, 4), new(4, 4)));
        string hash = partial.Tiles!.Foliage!.Packs.Single(p => p.Region == new MapTileCoord(0, 0)).PayloadSha256;
        string path = Path.Combine(dir.Root, "tiles", "foliage", "r_0_0", hash + ".pack");
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.SetLength(17_432_589);
        Assert.Equal(MapFoliageReadStatus.CapacityExceeded, partial.Tiles.Foliage.Read(FoliageFixtures.LayerId, new(0, 0)).Status);
    }

    [Fact]
    public void PackEncoder_RefusesUnrepresentableChunkKeys()
    {
        Assert.Throws<MapDocumentException>(() => MapFoliageRegionPack.Encode([new(new(32768, 0), new byte[1024])]));
    }

    [Fact]
    public void StoredManifest_ChargesUtf8BytesAndMembershipsBeforePublication()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageFixtures.Create();
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        var set = fixture.Document.Foliage!;
        var index = fixture.Document.Tiles!.Foliage!;
        using var bytes = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(bytes)) MapFoliageStorage.WriteManifest(writer, set, index);
        long exact = bytes.Length;
        MapFoliageManifestBudget.Check(set, index, exact);
        Assert.Throws<MapDocumentException>(() => MapFoliageManifestBudget.Check(set, index, exact - 1));
        // Every coordinate row needs at least 13 UTF-8 bytes, even before commas and field names.
        MapFoliageManifestBudget.CheckMemberships(10, 130);
        Assert.Throws<MapDocumentException>(() => MapFoliageManifestBudget.CheckMemberships(11, 130));
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("density")]
    public void SaveCallback_RefusesChangedFoliageBeforeManifestPublication(string change)
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageFixtures.Create();
        MapDocument document = fixture.Document;
        MapDocumentFile.SaveTiled(document, dir.Root);
        byte[] before = File.ReadAllBytes(Path.Combine(dir.Root, "map.json"));
        document.Foliage = new(1, [FoliageStorageFixtures.Change(document.Foliage!.Layers[0], l => l["seed"] = 18)]);
        var save = new MapDocumentSaveOptions
        {
            OnStep = step =>
            {
                if (step != MapTiledSaveStep.BeforeManifestRename) return;
                MapFoliageLayer layer = document.Foliage!.Layers[0];
                if (change == "seed") document.Foliage = new(1, [FoliageStorageFixtures.Change(layer, l => l["seed"] = 42)]);
                else
                {
                    byte[] bytes = layer.Chunks[0].CopyDensity(); bytes[0] ^= 1;
                    document.Foliage = new(1, [MapFoliageLayer.FromStorage(layer, layer.ChunkDigests,
                        [new(layer.Chunks[0].Key, bytes), layer.Chunks[1]])]);
                }
            },
        };
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(document, dir.Root, save: save));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(dir.Root, "map.json")));
        Assert.Equal(17, MapDocumentFile.LoadTiled(dir.Root).Foliage!.Layers[0].Seed);
    }

    [Fact]
    public void ManifestOnlyIndex_ReadsPinnedPacksWithoutChangingResidency()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        MapDocumentFile.SaveTiled(FoliageStorageFixtures.RegionFixture().Document, dir.Root);
        var source = MapDocumentSource.OpenTiled(dir.Root);
        MapFoliageStorageIndex index = source.Tiles.Foliage!;
        Assert.True(index.IsPartial);
        MapFoliageChunkRead read = index.Read(FoliageFixtures.LayerId, new(0, 0));
        Assert.Equal(MapFoliageReadStatus.Found, read.Status);
        Assert.Equal((byte)10, read.Chunk!.DensityAt(0, 0));
        Assert.True(index.IsPartial);
        Assert.Empty(source.Manifest.Foliage!.Layers[0].Chunks);
    }

    [Fact]
    public void ContentValidation_AppliesTheFoliageResolverGate()
    {
        var fixture = FoliageFixtures.Create();
        fixture.Document.ResolverIdentity = new(1, 1);
        var content = new MapTileContent(default, [], [], [], []);
        var surfaces = fixture.Document.Surfaces.Refs.ToDictionary(s => s.Id);
        var errors = MapDocumentValidator.ValidateContent(fixture.Document, surfaces, content, []);
        Assert.Contains(errors, e => e.StartsWith("foliage: requires resolver identity", StringComparison.Ordinal));
        Assert.Contains("foliage: requires resolver identity", Assert.Throws<MapDocumentException>(() =>
            MapBoundDocumentValidation.ValidateContent(fixture.Document, surfaces, content, [], fixture.Assets)).Message, StringComparison.Ordinal);
    }
}
