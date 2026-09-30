using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Store;

/// <summary>
/// Spec 8.8 row 3 for KECC: canonical bytes can digest correctly while the row table is malformed. The
/// client fetch must report the structural reason and leave those bytes out of its cache.
/// </summary>
public sealed class MalformedKeccFetchTests
{
    [Theory]
    [InlineData(ContentChunkCodec.ReasonRowTable)]
    [InlineData(ContentChunkCodec.ReasonRowOrder)]
    [InlineData(ContentChunkCodec.ReasonRowDuplicate)]
    [InlineData(ContentChunkCodec.ReasonRowOverflow)]
    public async Task A_matching_hash_over_a_malformed_kecc_is_refused_before_cache_write(string expectedReason)
    {
        CatalogPack pack = CatalogPack.Build();
        byte[] malformed = MalformedChunk(expectedReason);
        string chunkHash = ContentHash.OfChunk(malformed);
        ContentManifest manifest = WithTagChunk(pack.ClientManifest, chunkHash, malformed);
        string manifestHash = ContentManifestText.Hash(manifest);

        var remote = new FetchPackStore();
        remote.PlantPack(pack, manifest: false);
        remote.Plant(chunkHash, malformed);
        remote.Plant(manifestHash, ContentManifestCodec.Encode(manifest));
        var local = new FetchPackStore();
        var loop = new ContentFetchLoop(local, remote, pack.Registry, new ContentFetchOptions
        {
            ClientBuild = 12,
            Attempts = 1,
            BackoffBase = TimeSpan.Zero,
        });

        ContentFetchResult result = await loop.FetchAsync(
            new ContentVersionIdentity(CatalogPack.VersionNumber, manifestHash));

        // Before #954 was fixed the fetch reported success and cached the bytes. This diagnostic branch then
        // proved the first lazy row read paid a second decompression before finding the structural refusal.
        string? lazyReason = null;
        if (result.Success)
        {
            var reader = new ContentPackReader(loop.Store, pack.Registry, manifest, manifestHash);
            ContentRowRead lazy = await reader.ReadRowAsync(CatalogPack.TagType, 1);
            lazyReason = lazy.Reason;
        }

        bool cached = await local.ExistsAsync(chunkHash);
        Assert.False(
            result.Success,
            FormattableString.Invariant(
                $"the fetch succeeded with cached={cached}; the first lazy decode then answered '{lazyReason}'"));
        Assert.Equal(ContentFetchOutcome.ChunksMissing, result.Outcome);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(chunkHash, result.Hash);
        Assert.Equal(2, remote.GetCount(chunkHash));
        Assert.False(cached);

        ContentFetchFailure failure = Assert.Single(result.Failures);
        Assert.Equal(chunkHash, failure.Hash);
        Assert.Equal(expectedReason, failure.Reason);
    }

    [Fact]
    public async Task A_valid_kecc_is_still_returned_and_cached()
    {
        CatalogPack pack = CatalogPack.Build();
        var remote = new FetchPackStore();
        remote.Plant(pack.TagChunk.Hash, pack.TagChunk.StoredFile.Span);
        var local = new FetchPackStore();
        var store = new CachingPackStore(local, remote);

        ReadOnlyMemory<byte>? bytes = await store.GetAsync(pack.TagChunk.Hash);

        Assert.NotNull(bytes);
        Assert.Equal(pack.TagChunk.StoredFile, bytes.Value.ToArray());
        Assert.True(await local.ExistsAsync(pack.TagChunk.Hash));
    }

    static ContentManifest WithTagChunk(ContentManifest manifest, string hash, byte[] file)
    {
        ManifestTypeEntry[] types = manifest.Types.ToArray();
        int typeIndex = Array.FindIndex(types, type => type.TypeId == EngineContentTypes.TagTypeId);
        Assert.True(typeIndex >= 0);
        ManifestTypeEntry type = types[typeIndex];
        ManifestChunkEntry chunk = Assert.Single(type.Chunks);
        uint bodyBytes = BinaryPrimitives.ReadUInt32LittleEndian(
            file.AsSpan(28, sizeof(uint)));
        types[typeIndex] = type with
        {
            Chunks = [chunk with { UncompressedBytes = bodyBytes, Hash = hash }],
        };
        return manifest with { Types = types };
    }

    static byte[] MalformedChunk(string reason)
    {
        (byte[] body, uint rows) = reason switch
        {
            ContentChunkCodec.ReasonRowTable => (new byte[] { 0x01, 0x00, 0x01, 0x00 }, 1000u),
            ContentChunkCodec.ReasonRowDuplicate =>
                (new byte[] { 0x01, 0x00, 0x01, 0x01, 0x00, 0x01, 0x00, 0x00 }, 2u),
            ContentChunkCodec.ReasonRowOrder =>
                (new byte[] { 0x02, 0x00, 0x01, 0x01, 0x00, 0x01, 0x00, 0x00 }, 2u),
            ContentChunkCodec.ReasonRowOverflow => (new byte[] { 0x01, 0x00, 0x20, 0x00 }, 1u),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown malformed chunk case."),
        };

        byte[] file = new byte[ContentPackFormat.ChunkHeaderBytes + body.Length];
        ContentPackFormat.ChunkMagic.CopyTo(file);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), ContentPackFormat.ChunkFormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(6), EngineContentTypes.TagTypeId);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), TagContentType.DefaultChunkSlots);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), rows);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), (uint)body.Length);
        body.CopyTo(file.AsSpan(ContentPackFormat.ChunkHeaderBytes));
        return file;
    }
}
