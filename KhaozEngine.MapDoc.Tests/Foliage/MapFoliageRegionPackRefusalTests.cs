using System;
using System.Buffers.Binary;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageRegionPackRefusalTests
{
    [Theory]
    [InlineData(0, MapDocumentRefusal.ValueOutsideRange, 1)]
    [InlineData(16385, MapDocumentRefusal.WorkCapacityExceeded, 16384)]
    public void ChunkCount_SeparatesTheEmptyDomainFromCapacity(int count, MapDocumentRefusal reason, int control)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageRegionPack.ByteLength(count));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(count, error.ActualCount);
        if (count > 0) { Assert.Equal("foliage pack chunks", error.Budget); Assert.Equal(MapFoliagePayloadLimits.MaxChunksPerLayer, error.Limit); }
        else Assert.Equal(1, error.MinimumInteger);
        Assert.Equal(MapFoliageRegionPack.HeaderLength + control * MapFoliageRegionPack.RecordLength, MapFoliageRegionPack.ByteLength(control));
    }

    [Theory]
    [InlineData(-1, 0, "X", 0, 0)]
    [InlineData(32768, 0, "X", 32767, 0)]
    [InlineData(0, -1, "Z", 0, 0)]
    [InlineData(0, 32768, "Z", 0, 32767)]
    public void Encode_KeyBoundsIdentifyTheFailedAxis(int x, int z, string axis, int controlX, int controlZ)
    {
        var chunk = new MapFoliageChunk(new(x, z), new byte[MapFoliageChunk.ByteLength]);
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageRegionPack.Encode([chunk]));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
        Assert.Equal(chunk.Key, error.FoliageChunk);
        Assert.Equal("Key." + axis, error.FieldPath);
        Assert.Equal(axis == "X" ? x : z, error.ActualInteger);
        Assert.Equal(0, error.MinimumInteger);
        Assert.Equal(32767, error.MaximumInteger);
        var control = new MapFoliageChunk(new(controlX, controlZ), chunk.CopyDensity());
        Assert.Equal(control.Key, Assert.Single(MapFoliageRegionPack.Decode(MapFoliageRegionPack.Encode([control]))).Key);
    }

    [Fact]
    public void Encode_DuplicateKeysRemainAnIdentityRule()
    {
        var chunk = FoliageFixtures.BasicChunks()[0];
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageRegionPack.Encode([chunk, chunk]));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal(chunk.Key, error.FoliageChunk);
        Assert.Equal(1, error.Index);
        Assert.Equal(2, MapFoliageRegionPack.Decode(MapFoliageRegionPack.Encode(FoliageFixtures.BasicChunks())).Count);
    }

    [Theory]
    [InlineData("short-header", MapDocumentRefusal.SchemaArrayLengthMismatch)]
    [InlineData("byte-cap", MapDocumentRefusal.WorkCapacityExceeded)]
    [InlineData("magic", MapDocumentRefusal.SchemaMalformed)]
    [InlineData("length", MapDocumentRefusal.SchemaArrayLengthMismatch)]
    [InlineData("key-x", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("key-z", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("order", MapDocumentRefusal.OrderInvalid)]
    [InlineData("digest", MapDocumentRefusal.DigestMismatch)]
    public void Decode_IdentifiesTheFirstEnvelopeOrRecordRule(string fault, MapDocumentRefusal reason)
    {
        var chunks = FoliageFixtures.BasicChunks();
        byte[] valid = MapFoliageRegionPack.Encode(chunks);
        byte[] bytes = (byte[])valid.Clone();
        switch (fault)
        {
            case "short-header": bytes = new byte[MapFoliageRegionPack.HeaderLength - 1]; break;
            case "byte-cap": bytes = new byte[MapFoliageRegionPack.MaxByteLength + 1]; valid.AsSpan().CopyTo(bytes); break;
            case "magic": bytes[0] = 0; break;
            case "length": Array.Resize(ref bytes, bytes.Length - 1); break;
            case "key-x": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), -1); break;
            case "key-z": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 32768); break;
            case "order": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12 + MapFoliageRegionPack.RecordLength), 0); break;
            case "digest": bytes.AsSpan(20, 32).Clear(); break;
        }
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageRegionPack.Decode(bytes));
        Assert.Equal(reason, error.Reason);
        if (fault is "short-header" or "byte-cap" or "length") Assert.Equal(bytes.Length, error.ActualByteCount);
        if (fault == "length") Assert.Equal(valid.Length, error.ExpectedByteCount);
        if (fault == "byte-cap") Assert.Equal(MapFoliageRegionPack.MaxByteLength, error.Limit);
        if (fault is "key-x" or "key-z")
        {
            Assert.Equal(fault == "key-x" ? new MapFoliageChunkKey(-1, 0) : new(0, 32768), error.FoliageChunk);
            Assert.Equal(0, error.Index);
            Assert.Equal(0, error.MinimumInteger);
            Assert.Equal(32767, error.MaximumInteger);
        }
        if (fault == "order") { Assert.Equal(1, error.Index); Assert.Equal(new MapFoliageChunkKey(0, 0), error.FoliageChunk); }
        if (fault == "digest")
        {
            Assert.Equal(new string('0', 64), error.ExpectedHash);
            Assert.Equal(chunks[0].Sha256, error.ActualHash);
            Assert.Equal(chunks[0].Key, error.FoliageChunk);
        }
        Assert.Equal(2, MapFoliageRegionPack.Decode(valid).Count);
        if (fault == "byte-cap")
        {
            // The maximum legal byte length is a full pack of unique ordered zero-density chunks.
            var full = new MapFoliageChunk[MapFoliagePayloadLimits.MaxChunksPerLayer];
            for (int i = 0; i < full.Length; i++) full[i] = new(new(i, 0), new byte[MapFoliageChunk.ByteLength]);
            byte[] packed = MapFoliageRegionPack.Encode(full);
            Assert.Equal(MapFoliageRegionPack.MaxByteLength, packed.Length);
            Assert.Equal(full.Length, MapFoliageRegionPack.Decode(packed).Count);
        }
    }

    [Theory]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void DigestNames_KeepTheRefusedText(string digest)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageRegionPack.RequireDigest(digest));
        Assert.Equal(MapDocumentRefusal.DigestMalformed, error.Reason);
        Assert.Equal(digest, error.ActualHash);
        MapFoliageRegionPack.RequireDigest(new string('a', 64));
    }
}
