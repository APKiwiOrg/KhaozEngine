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
            Key = new(i % 32768, i / 32768), OwnerRegion = new(i / perPack, 0),
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
    }

    internal static MapFoliagePackRef Pack() => new("cover", new(0, 0), Hash, 1, MapFoliageRegionPack.ByteLength(1));
    internal static MapFoliageChunkRef Chunk() => new("cover", new(0, 0), new(0, 0), [new(0, 0)], Hash, Hash, MapFoliageRegionPack.DataOffset(0));
}
