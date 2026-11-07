using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageIndexRefusalTests
{
    static readonly string Hash = new('a', 64);

    [Theory]
    [InlineData("missing", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("length", MapDocumentRefusal.SchemaArrayLengthMismatch)]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate)]
    public void PackReference_SelectsItsMissingShapeOrIdentityRule(string fault, MapDocumentRefusal reason)
    {
        var pack = Pack();
        MapFoliagePackRef[] packs = fault == "duplicate" ? [pack, pack] :
            [fault == "missing" ? pack with { LayerId = "" } : pack with { ByteLength = pack.ByteLength - 1 }];
        var error = Assert.Throws<MapDocumentException>(() => new MapFoliageStorageIndex(packs, [Chunk()]));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(packs[0].LayerId, error.LayerId);
        Assert.Equal(pack.Region, error.Tile);
        Assert.Equal(fault == "missing" ? "LayerId" : fault == "length" ? "ByteLength" : "Packs", error.FieldPath);
        if (fault == "length")
        {
            Assert.Equal((long)pack.ByteLength - 1, error.ActualByteCount);
            Assert.Equal((long)pack.ByteLength, error.ExpectedByteCount);
        }
        Assert.Single(new MapFoliageStorageIndex([pack], [Chunk()]).Packs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DocumentCapacity_AcceptsExactlyEachIndependentCollectionCap(bool chunkLimit)
    {
        int limit = MapFoliagePayloadLimits.MaxDocumentChunks;
        int perPack = chunkLimit ? MapFoliagePayloadLimits.MaxChunksPerLayer : 1;
        var packs = Enumerable.Range(0, limit / perPack).Select(i => Pack() with
        { Region = new(i, 0), ChunkCount = perPack, ByteLength = MapFoliageRegionPack.ByteLength(perPack) }).ToArray();
        var chunks = Enumerable.Range(0, limit).Select(i => Chunk() with
        {
            Key = new(i % 32768, i / 32768),
            OwnerRegion = new(i / perPack, 0),
            DataOffset = MapFoliageRegionPack.DataOffset(i % perPack),
        }).ToArray();
        var error = Assert.Throws<MapDocumentException>(() => new MapFoliageStorageIndex(
            chunkLimit ? packs : [.. packs, packs[0]], chunkLimit ? [.. chunks, chunks[0]] : chunks));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal(chunkLimit ? "Chunks" : "Packs", error.FieldPath);
        Assert.Equal((long)limit + 1, error.ActualCount);
        Assert.Equal((long)limit, error.Limit);
        var control = new MapFoliageStorageIndex(packs, chunks);
        Assert.Equal(limit, control.Chunks.Count);
        Assert.Equal(limit / perPack, control.Packs.Count);
        var belowPacks = chunkLimit ? packs.ToArray() : packs[..^1];
        if (chunkLimit) belowPacks[^1] = belowPacks[^1] with
        { ChunkCount = perPack - 1, ByteLength = MapFoliageRegionPack.ByteLength(perPack - 1) };
        var below = new MapFoliageStorageIndex(belowPacks, chunks[..^1]);
        Assert.Equal(limit - 1, below.Chunks.Count);
        Assert.Equal(chunkLimit ? packs.Length : packs.Length - 1, below.Packs.Count);
    }

    [Theory]
    [InlineData("x-low", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("x-high", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("z-low", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("z-high", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("empty-regions", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("regions", MapDocumentRefusal.WorkCapacityExceeded)]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("pack", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("digest", MapDocumentRefusal.DigestMismatch)]
    public void ChunkReference_SelectsTheFirstInvalidBranch(string fault, MapDocumentRefusal reason)
    {
        var chunk = Chunk();
        if (fault.StartsWith("x-", StringComparison.Ordinal)) chunk = chunk with { Key = new(fault == "x-low" ? -1 : 32768, 0) };
        if (fault.StartsWith("z-", StringComparison.Ordinal)) chunk = chunk with { Key = new(0, fault == "z-low" ? -1 : 32768) };
        if (fault == "empty-regions") chunk = chunk with { Regions = [] };
        if (fault == "regions") chunk = chunk with { Regions = Enumerable.Range(0, 4097).Select(x => new MapTileCoord(x, 0)).ToArray() };
        if (fault == "pack") chunk = chunk with { OwnerRegion = new(1, 0) };
        if (fault == "digest") chunk = chunk with { PackSha256 = new string('b', 64) };
        var error = Assert.Throws<MapDocumentException>(() => new MapFoliageStorageIndex([Pack()], fault == "duplicate" ? [chunk, chunk] : [chunk]));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(chunk.LayerId, error.LayerId);
        Assert.Equal(chunk.Key, error.FoliageChunk);
        if (fault.StartsWith("x-", StringComparison.Ordinal) || fault.StartsWith("z-", StringComparison.Ordinal))
        {
            bool x = fault[0] == 'x';
            Assert.Equal(x ? "Key.X" : "Key.Z", error.FieldPath);
            Assert.Equal((long)(x ? chunk.Key.X : chunk.Key.Z), error.ActualInteger);
            Assert.Equal(0L, error.MinimumInteger);
            Assert.Equal(32767L, error.MaximumInteger);
            chunk = chunk with { Key = new(x && fault == "x-high" ? 32767 : 0, !x && fault == "z-high" ? 32767 : 0) };
        }
        else if (fault is "empty-regions" or "regions")
        {
            Assert.Equal("Regions", error.FieldPath);
            Assert.Equal(fault == "regions" ? 4097L : 0L, error.ActualCount);
            Assert.Equal(1L, error.MinimumInteger);
            Assert.Equal(4096L, error.MaximumInteger);
            if (fault == "regions") { Assert.Equal(4096L, error.Limit); Assert.Equal("foliage chunk regions", error.Budget); }
            chunk = chunk with { Regions = Enumerable.Range(0, fault == "regions" ? 4096 : 1).Select(x => new MapTileCoord(x, 0)).ToArray() };
        }
        else if (fault == "pack") { Assert.Equal(chunk.OwnerRegion, error.Tile); chunk = chunk with { OwnerRegion = Pack().Region }; }
        else if (fault == "digest")
        {
            Assert.Equal(Pack().PayloadSha256, error.ExpectedHash);
            Assert.Equal(chunk.PackSha256, error.ActualHash);
            chunk = chunk with { PackSha256 = Pack().PayloadSha256 };
        }
        Assert.Single(new MapFoliageStorageIndex([Pack()], [chunk]).Chunks);
        if (fault == "regions")
        {
            var below = chunk with { Regions = chunk.Regions.Take(4095).ToArray() };
            Assert.Equal(4095, Assert.Single(new MapFoliageStorageIndex([Pack()], [below]).Chunks).Regions.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackCount_RetainsTheAdvertisedAndPresentCounts(bool empty)
    {
        var pack = empty ? Pack() : Pack() with { ChunkCount = 2, ByteLength = MapFoliageRegionPack.ByteLength(2) };
        var error = Assert.Throws<MapDocumentException>(() => new MapFoliageStorageIndex([pack], empty ? [] : [Chunk()]));
        Assert.Equal(MapDocumentRefusal.SchemaArrayLengthMismatch, error.Reason);
        Assert.Equal(pack.LayerId, error.LayerId);
        Assert.Equal(pack.Region, error.Tile);
        Assert.Equal(empty ? 0L : 1L, error.ActualCount);
        Assert.Equal((long)pack.ChunkCount, error.ExpectedCount);
        Assert.Single(new MapFoliageStorageIndex([Pack()], [Chunk()]).Chunks);
    }

    [Fact]
    public void ChunkOffset_RetainsTheCanonicalAndSubmittedAddress()
    {
        var chunk = Chunk() with { DataOffset = MapFoliageRegionPack.DataOffset(0) + 1 };
        var error = Assert.Throws<MapDocumentException>(() => new MapFoliageStorageIndex([Pack()], [chunk]));
        Assert.Equal(MapDocumentRefusal.FoliagePackOffsetMismatch, error.Reason);
        Assert.Equal(chunk.LayerId, error.LayerId);
        Assert.Equal(chunk.Key, error.FoliageChunk);
        Assert.Equal(0, error.Index);
        Assert.Equal((long)chunk.DataOffset, error.ActualInteger);
        Assert.Equal(52L, error.MinimumInteger);
        Assert.Equal(52L, error.MaximumInteger);
        Assert.Single(new MapFoliageStorageIndex([Pack()], [Chunk()]).Chunks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResidentDensity_DistinguishesMissingReferenceFromMismatchedDigest(bool missing)
    {
        var layer = FoliageFixtures.BasicLayer([new(new(0, 0), new byte[MapFoliageChunk.ByteLength])]);
        var chunk = Chunk() with { LayerId = layer.Id };
        var pack = Pack() with { LayerId = layer.Id };
        var set = new MapFoliageSet(1, [layer]);
        var error = Assert.Throws<MapDocumentException>(() => new MapFoliageStorageIndex(
            missing ? [] : [pack], missing ? [] : [chunk], resident: set));
        Assert.Equal(missing ? MapDocumentRefusal.ReferenceMissing : MapDocumentRefusal.DigestMismatch, error.Reason);
        Assert.Equal(layer.Id, error.LayerId);
        Assert.Equal(chunk.Key, error.FoliageChunk);
        if (!missing)
        {
            Assert.Equal(chunk.Sha256, error.ExpectedHash);
            Assert.Equal(FoliageStorageFixtures.Hash(layer.Chunks[0].CopyDensity()), error.ActualHash);
        }
        var control = new MapFoliageStorageIndex([pack], [chunk with { Sha256 = FoliageStorageFixtures.Hash(layer.Chunks[0].CopyDensity()) }], resident: set);
        Assert.Equal(MapFoliageReadStatus.Found, control.Read(layer.Id, chunk.Key).Status);
    }

    internal static MapFoliagePackRef Pack() => new("cover", new(0, 0), Hash, 1, MapFoliageRegionPack.ByteLength(1));
    internal static MapFoliageChunkRef Chunk() => new("cover", new(0, 0), new(0, 0), [new(0, 0)], Hash, Hash, MapFoliageRegionPack.DataOffset(0));
}
