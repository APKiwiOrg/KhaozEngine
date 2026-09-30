using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Store;

public class CachingPackStoreVerifiedWriteTests
{
    [Fact]
    public async Task A_valid_fetch_uses_the_verified_write_with_the_same_hash_bytes_and_cancellation_token()
    {
        CatalogPack pack = CatalogPack.Build();
        var local = new RecordingVerifiedStore();
        var remote = new CachingPackStoreTests.MemoryPackStore();
        remote.Plant(pack.TagChunk.Hash, pack.TagChunk.StoredFile.ToArray());
        var cache = new CachingPackStore(local, remote);
        using var cancel = new CancellationTokenSource();

        ReadOnlyMemory<byte>? fetched = await cache.GetAsync(pack.TagChunk.Hash, cancel.Token);

        Assert.NotNull(fetched);
        Assert.Equal(pack.TagChunk.StoredFile.ToArray(), fetched.Value.ToArray());
        Assert.Empty(local.PublicWrites);
        Write write = Assert.Single(local.VerifiedWrites);
        Assert.Equal(pack.TagChunk.Hash, write.Hash);
        Assert.Equal(fetched.Value, write.Bytes);
        Assert.Equal(cancel.Token, write.CancellationToken);
    }

    [Fact]
    public async Task A_store_without_the_capability_receives_the_public_write()
    {
        CatalogPack pack = CatalogPack.Build();
        var local = new RecordingStore();
        var remote = new CachingPackStoreTests.MemoryPackStore();
        remote.Plant(pack.TagChunk.Hash, pack.TagChunk.StoredFile.ToArray());
        var cache = new CachingPackStore(local, remote);
        using var cancel = new CancellationTokenSource();

        ReadOnlyMemory<byte>? fetched = await cache.GetAsync(pack.TagChunk.Hash, cancel.Token);

        Assert.NotNull(fetched);
        Write write = Assert.Single(local.PublicWrites);
        Assert.Equal(pack.TagChunk.Hash, write.Hash);
        Assert.Equal(fetched.Value, write.Bytes);
        Assert.Equal(cancel.Token, write.CancellationToken);
        Assert.True(await local.ExistsAsync(pack.TagChunk.Hash));
    }

    [Fact]
    public async Task A_corrupted_fetch_reaches_neither_write_path()
    {
        CatalogPack pack = CatalogPack.Build();
        var local = new RecordingVerifiedStore();
        var remote = new CachingPackStoreTests.MemoryPackStore();
        byte[] corrupt = pack.TagChunk.StoredFile.ToArray();
        corrupt[^1] ^= 0xff;
        remote.Plant(pack.TagChunk.Hash, corrupt);
        var refusals = new List<(string Hash, string Reason)>();
        var cache = new CachingPackStore(local, remote, (hash, reason) => refusals.Add((hash, reason)));

        Assert.Null(await cache.GetAsync(pack.TagChunk.Hash));

        Assert.Empty(local.PublicWrites);
        Assert.Empty(local.VerifiedWrites);
        Assert.False(await local.ExistsAsync(pack.TagChunk.Hash));
        Assert.Equal((pack.TagChunk.Hash, ContentPackReader.ReasonHashMismatch), Assert.Single(refusals));
    }

    [Fact]
    public async Task A_public_cache_write_retains_the_local_integrity_check()
    {
        CatalogPack pack = CatalogPack.Build();
        var local = new RecordingVerifiedStore();
        var cache = new CachingPackStore(local, new CachingPackStoreTests.MemoryPackStore());

        await Assert.ThrowsAsync<ContentPackException>(
            () => cache.PutAsync(pack.ItemChunkZero.Hash, pack.TagChunk.StoredFile));

        Assert.Empty(local.VerifiedWrites);
        Assert.Single(local.PublicWrites);
        Assert.False(await local.ExistsAsync(pack.ItemChunkZero.Hash));
    }

    readonly record struct Write(string Hash, ReadOnlyMemory<byte> Bytes, CancellationToken CancellationToken);

    class RecordingStore : IPackStore
    {
        protected CachingPackStoreTests.MemoryPackStore Inner { get; } = new();

        public List<Write> PublicWrites { get; } = [];

        public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
            => Inner.ExistsAsync(hash, cancellationToken);

        public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
            => Inner.GetAsync(hash, cancellationToken);

        public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            PublicWrites.Add(new Write(hash, bytes, cancellationToken));
            return Inner.PutAsync(hash, bytes, cancellationToken);
        }

        public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
            => Inner.ListAsync(versionNumber, cancellationToken);
    }

    sealed class RecordingVerifiedStore : RecordingStore, IVerifiedPackStoreWrite
    {
        public List<Write> VerifiedWrites { get; } = [];

        public Task PutVerifiedAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifiedWrites.Add(new Write(hash, bytes, cancellationToken));
            Inner.Plant(hash, bytes.ToArray());
            return Task.CompletedTask;
        }
    }
}
