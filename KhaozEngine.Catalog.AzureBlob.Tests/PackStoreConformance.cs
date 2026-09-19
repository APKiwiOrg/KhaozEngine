using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.AzureBlob;

/// <summary>
/// The contract every writable pack store owes, run over BOTH providers that implement it. The blob store
/// is a second implementation of a seam the file system store already defines, and the only way a reader
/// can trust that phrase is a suite neither provider gets its own copy of: a divergence in the refusal, the
/// digest check or the delete answer then goes red on one subclass and green on the other.
/// </summary>
public abstract class PackStoreConformance : IDisposable
{
    /// <summary>A fresh, empty store of the provider under test.</summary>
    protected abstract IPackStore CreateStore();

    /// <inheritdoc />
    public virtual void Dispose()
    {
    }

    [Fact]
    public async Task PutAsync_verifies_the_digest_of_the_bytes_it_was_handed()
    {
        IPackStore store = CreateStore();
        StoredObject english = CatalogChunks.English();
        StoredObject french = CatalogChunks.French();

        ContentPackException thrown = await Assert.ThrowsAsync<ContentPackException>(
            () => store.PutAsync(french.Hash, english.File));

        Assert.Equal(french.Hash, thrown.Hash);
        Assert.Equal(ContentPackReader.ReasonHashMismatch, thrown.Reason);
        Assert.False(await store.ExistsAsync(french.Hash));
        Assert.Null(await store.GetAsync(french.Hash));
    }

    [Fact]
    public async Task PutAsync_refuses_a_name_that_is_not_a_content_address()
    {
        IPackStore store = CreateStore();
        StoredObject english = CatalogChunks.English();

        ContentPackException thrown = await Assert.ThrowsAsync<ContentPackException>(
            () => store.PutAsync("../../etc/passwd", english.File));

        Assert.Equal(ContentPackReader.ReasonHashMismatch, thrown.Reason);
        Assert.Null(await store.GetAsync("../../etc/passwd"));
        Assert.Empty(await Enumerate(store));
    }

    [Fact]
    public async Task PutAsync_of_a_hash_that_exists_is_a_no_op()
    {
        IPackStore store = CreateStore();
        StoredObject english = CatalogChunks.English();

        await store.PutAsync(english.Hash, english.File);
        await store.PutAsync(english.Hash, english.File);

        // The name IS the content, so the second put has nothing to change. What it must not do is fail,
        // and what the store must not end up with is a second copy under the same address.
        ReadOnlyMemory<byte>? read = await store.GetAsync(english.Hash);
        Assert.NotNull(read);
        Assert.True(english.File.AsSpan().SequenceEqual(read.Value.Span));
        Assert.Equal(english.Hash, Assert.Single(await Enumerate(store)));
    }

    [Fact]
    public async Task GetAsync_returns_null_for_an_absent_hash()
    {
        IPackStore store = CreateStore();

        Assert.Null(await store.GetAsync(new string('a', 64)));
        Assert.False(await store.ExistsAsync(new string('a', 64)));
    }

    [Fact]
    public async Task ExistsAsync_tracks_a_put_and_a_delete()
    {
        IPackStore store = CreateStore();
        StoredObject english = CatalogChunks.English();

        Assert.False(await store.ExistsAsync(english.Hash));
        await store.PutAsync(english.Hash, english.File);
        Assert.True(await store.ExistsAsync(english.Hash));

        Assert.True(await ((IPackStorePruning)store).DeleteAsync(english.Hash));
        Assert.False(await store.ExistsAsync(english.Hash));
        Assert.False(await ((IPackStorePruning)store).DeleteAsync(english.Hash));
    }

    [Fact]
    public async Task Pruning_enumerates_every_hash_and_deletes_one()
    {
        IPackStore store = CreateStore();
        StoredObject english = CatalogChunks.English();
        StoredObject french = CatalogChunks.French();
        StoredObject rules = CatalogChunks.Rules();
        await store.PutAsync(english.Hash, english.File);
        await store.PutAsync(french.Hash, french.File);
        await store.PutAsync(rules.Hash, rules.File);

        List<string> held = await Enumerate(store);

        Assert.Equal(
            new[] { english.Hash, french.Hash, rules.Hash }.OrderBy(h => h, StringComparer.Ordinal),
            held.OrderBy(h => h, StringComparer.Ordinal));

        Assert.True(await ((IPackStorePruning)store).DeleteAsync(french.Hash));
        Assert.DoesNotContain(french.Hash, await Enumerate(store));
        Assert.True(await store.ExistsAsync(english.Hash));
        Assert.True(await store.ExistsAsync(rules.Hash));
    }

    static async Task<List<string>> Enumerate(IPackStore store)
    {
        var held = new List<string>();
        await foreach (string hash in ((IPackStorePruning)store).EnumerateAsync())
        {
            held.Add(hash);
        }

        return held;
    }
}
