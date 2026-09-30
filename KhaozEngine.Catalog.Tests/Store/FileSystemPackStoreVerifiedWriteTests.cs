using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Store;

public class FileSystemPackStoreVerifiedWriteTests
{
    [Theory]
    [InlineData(PackDurability.Default)]
    [InlineData(PackDurability.PowerFail)]
    public async Task A_verified_fetch_lands_the_file_and_remains_available_offline(PackDurability durability)
    {
        using var root = new TemporaryRoot();
        CatalogPack pack = CatalogPack.Build();
        var local = new FileSystemPackStore(root.Path, durability);
        var remote = new CachingPackStoreTests.MemoryPackStore();
        remote.Plant(pack.TagChunk.Hash, pack.TagChunk.StoredFile.ToArray());
        var cache = new CachingPackStore(local, remote);

        ReadOnlyMemory<byte>? fetched = await cache.GetAsync(pack.TagChunk.Hash);

        Assert.NotNull(fetched);
        Assert.Equal(pack.TagChunk.StoredFile.ToArray(), fetched.Value.ToArray());
        Assert.Equal(pack.TagChunk.StoredFile.ToArray(), await File.ReadAllBytesAsync(local.PathFor(pack.TagChunk.Hash)));
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp", SearchOption.AllDirectories));

        Assert.True(await remote.DeleteAsync(pack.TagChunk.Hash));
        ReadOnlyMemory<byte>? offline = await cache.GetAsync(pack.TagChunk.Hash);

        Assert.NotNull(offline);
        Assert.Equal(pack.TagChunk.StoredFile.ToArray(), offline.Value.ToArray());
        Assert.Equal(1, remote.Gets);
    }

    [Fact]
    public async Task A_corrupted_remote_object_creates_no_cache_file()
    {
        using var root = new TemporaryRoot();
        CatalogPack pack = CatalogPack.Build();
        var local = new FileSystemPackStore(root.Path);
        var remote = new CachingPackStoreTests.MemoryPackStore();
        remote.Plant(pack.ItemChunkZero.Hash, pack.TagChunk.StoredFile.ToArray());
        var cache = new CachingPackStore(local, remote);

        Assert.Null(await cache.GetAsync(pack.ItemChunkZero.Hash));

        Assert.Empty(Directory.GetFiles(root.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_public_write_refuses_invalid_bytes_even_when_the_address_already_exists()
    {
        using var root = new TemporaryRoot();
        CatalogPack pack = CatalogPack.Build();
        var store = new FileSystemPackStore(root.Path);
        await store.PutAsync(pack.ItemChunkZero.Hash, pack.ItemChunkZero.StoredFile);

        ContentPackException refusal = await Assert.ThrowsAsync<ContentPackException>(
            () => store.PutAsync(pack.ItemChunkZero.Hash, pack.TagChunk.StoredFile));

        Assert.Equal(pack.ItemChunkZero.Hash, refusal.Hash);
        Assert.Equal(ContentPackReader.ReasonHashMismatch, refusal.Reason);
        Assert.Equal(pack.ItemChunkZero.StoredFile.ToArray(), await File.ReadAllBytesAsync(store.PathFor(pack.ItemChunkZero.Hash)));
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_verified_cache_write_keeps_a_rivals_file_at_placement()
    {
        using var root = new TemporaryRoot();
        CatalogPack pack = CatalogPack.Build();
        var local = new FileSystemPackStore(root.Path);
        var remote = new CachingPackStoreTests.MemoryPackStore();
        remote.Plant(pack.TagChunk.Hash, pack.TagChunk.StoredFile.ToArray());
        byte[] rival = [1, 2, 3];
        local.BeforePlace = destination => File.WriteAllBytes(destination, rival);
        var cache = new CachingPackStore(local, remote);

        ReadOnlyMemory<byte>? fetched = await cache.GetAsync(pack.TagChunk.Hash);

        Assert.NotNull(fetched);
        Assert.Equal(pack.TagChunk.StoredFile.ToArray(), fetched.Value.ToArray());
        Assert.Equal(rival, await File.ReadAllBytesAsync(local.PathFor(pack.TagChunk.Hash)));
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_cancelled_verified_write_leaves_no_object_or_temporary()
    {
        using var root = new TemporaryRoot();
        CatalogPack pack = CatalogPack.Build();
        var store = new FileSystemPackStore(root.Path);
        var verified = Assert.IsAssignableFrom<IVerifiedPackStoreWrite>(store);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        OperationCanceledException refusal = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => verified.PutVerifiedAsync(pack.TagChunk.Hash, pack.TagChunk.StoredFile, cancel.Token));

        Assert.Equal(cancel.Token, refusal.CancellationToken);
        Assert.Empty(Directory.GetFiles(root.Path, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../../outside")]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_verified_write_still_refuses_an_invalid_address(string hash)
    {
        using var root = new TemporaryRoot();
        CatalogPack pack = CatalogPack.Build();
        var store = new FileSystemPackStore(root.Path);
        var verified = Assert.IsAssignableFrom<IVerifiedPackStoreWrite>(store);

        ContentPackException refusal = await Assert.ThrowsAsync<ContentPackException>(
            () => verified.PutVerifiedAsync(hash, pack.TagChunk.StoredFile, CancellationToken.None));

        Assert.Equal(hash, refusal.Hash);
        Assert.Equal(ContentPackReader.ReasonHashMismatch, refusal.Reason);
        Assert.Empty(Directory.GetFiles(root.Path, "*", SearchOption.AllDirectories));
    }
}
