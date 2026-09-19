using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.AzureBlob;
using Xunit;

namespace KhaozEngine.Tests.Catalog.AzureBlob;

/// <summary>
/// The gated leg against a REAL container, which is the only place the adapter's own behaviour is exercised:
/// the conditional upload's status codes, the headers the service actually stored, the read faults that are
/// answered as absent, the declared length the ceiling is applied to, and a delete that answers whether the
/// object was there. Everything else in this project runs against the seam's in-memory double, which by
/// construction never reaches the adapter, so this leg is a REQUIRED run before a release that touches
/// <c>BlobContainerAdapter</c>.
/// <para>
/// It builds its own client, which is the whole point of the package taking one: the shipped package has no
/// Azure.Identity dependency, so the credential is the HOST's decision. This leg makes the same decision a
/// host would, from the URL it was given: a shared-access signature in the query authenticates itself, and a
/// bare container URL gets the ambient identity.
/// </para>
/// <para>
/// Every fact deletes exactly what it wrote, in a <c>finally</c>, and the attribute refuses a container whose
/// name does not carry the test marker, so a leg pointed at a live origin cannot prune a published chunk.
/// </para>
/// </summary>
public sealed class AzureBlobPackStoreLiveTests
{
    [AzureBlobPackStoreFact]
    public async Task A_live_container_round_trips_a_chunk_and_a_delete()
    {
        var store = new AzureBlobPackStore(Container());
        StoredObject english = CatalogChunks.English();

        try
        {
            await store.PutAsync(english.Hash, english.File);

            Assert.True(await store.ExistsAsync(english.Hash));
            ReadOnlyMemory<byte>? read = await store.GetAsync(english.Hash);
            Assert.NotNull(read);
            Assert.True(english.File.AsSpan().SequenceEqual(read.Value.Span));
        }
        finally
        {
            await ((IPackStorePruning)store).DeleteAsync(english.Hash);
        }

        Assert.False(await store.ExistsAsync(english.Hash));
    }

    // The conditional upload against the SERVICE, which is the arm the in-memory double can only describe:
    // the second put meets the object the first one wrote, and the 409 BlobAlreadyExists or the 412 that
    // comes back is the idempotent no-op the seam promises rather than an error.
    [AzureBlobPackStoreFact]
    public async Task A_live_put_of_a_hash_that_exists_is_a_no_op_and_not_an_error()
    {
        var store = new AzureBlobPackStore(Container());
        StoredObject english = CatalogChunks.English();

        try
        {
            await store.PutAsync(english.Hash, english.File);
            await store.PutAsync(english.Hash, english.File);

            ReadOnlyMemory<byte>? read = await store.GetAsync(english.Hash);
            Assert.NotNull(read);
            Assert.True(english.File.AsSpan().SequenceEqual(read.Value.Span));
        }
        finally
        {
            await ((IPackStorePruning)store).DeleteAsync(english.Hash);
        }
    }

    // The 404 arms, against the service rather than against a dictionary.
    [AzureBlobPackStoreFact]
    public async Task A_live_get_and_exists_of_an_absent_hash_answer_null_and_false()
    {
        var store = new AzureBlobPackStore(Container());
        string absent = new('b', 64);

        Assert.Null(await store.GetAsync(absent));
        Assert.False(await store.ExistsAsync(absent));
    }

    // The headers the SERVICE stored, read back through the SDK rather than through the seam, because what
    // the seam was handed is already pinned in memory and what matters here is what a CDN and a browser cache
    // will see on the object a client fetches.
    [AzureBlobPackStoreFact]
    public async Task A_live_object_carries_the_immutable_cache_control_and_the_binary_content_type()
    {
        BlobContainerClient container = Container();
        var store = new AzureBlobPackStore(container);
        StoredObject english = CatalogChunks.English();

        try
        {
            await store.PutAsync(english.Hash, english.File);

            BlobProperties properties = await container
                .GetBlobClient(FileSystemPackStore.RelativeKeyFor(english.Hash))
                .GetPropertiesAsync();

            Assert.Equal(AzureBlobPackStore.ObjectContentType, properties.ContentType);
            Assert.Equal(AzureBlobPackStore.ObjectCacheControl, properties.CacheControl);
        }
        finally
        {
            await ((IPackStorePruning)store).DeleteAsync(english.Hash);
        }
    }

    // The listing against the service's own paging, and the canonical key rule over a real key.
    [AzureBlobPackStoreFact]
    public async Task A_live_enumerate_yields_the_hash_that_was_put()
    {
        var store = new AzureBlobPackStore(Container());
        StoredObject english = CatalogChunks.English();

        try
        {
            await store.PutAsync(english.Hash, english.File);

            var held = new List<string>();
            await foreach (string hash in ((IPackStorePruning)store).EnumerateAsync())
            {
                held.Add(hash);
            }

            Assert.Contains(english.Hash, held);
        }
        finally
        {
            await ((IPackStorePruning)store).DeleteAsync(english.Hash);
        }
    }

    // DeleteIfExistsAsync's own answer, which is the one thing a prune reads: false means there was nothing
    // to delete rather than that the delete failed.
    [AzureBlobPackStoreFact]
    public async Task A_live_delete_of_an_absent_hash_answers_false()
    {
        var store = new AzureBlobPackStore(Container());

        Assert.False(await ((IPackStorePruning)store).DeleteAsync(new string('c', 64)));
    }

    // The package's whole claim, against the real service: a fill copies one version's client closure into
    // the container, and a real client fetch loop then completes off it. The manifest is built from
    // KhaozEngine.Catalog types alone, so this leg needs no publisher and no authoring reference.
    [AzureBlobPackStoreFact]
    public async Task A_live_fill_through_ContentOriginFill_is_readable_by_a_client_fetch_loop()
    {
        using var sourceRoot = new TemporaryPackRoot();
        using var cacheRoot = new TemporaryPackRoot();
        var source = new FileSystemPackStore(sourceRoot.Path);
        ClientPack pack = ClientPack.Build();
        await pack.WriteAsync(source);

        var origin = new AzureBlobPackStore(Container());

        try
        {
            ContentOriginFillResult fill = await ContentOriginFill.RunAsync(
                source, origin, pack.Version, pack.Registry);

            Assert.True(fill.Filled, fill.RefusalReason ?? "no reason");

            var loop = new ContentFetchLoop(
                new FileSystemPackStore(cacheRoot.Path),
                origin,
                pack.Registry,
                new ContentFetchOptions { ClientBuild = 10_000, Attempts = 1, BackoffBase = TimeSpan.Zero });
            ContentFetchResult fetched = await loop.FetchAsync(pack.Version);

            Assert.True(fetched.Success, fetched.Reason ?? "no reason");
        }
        finally
        {
            foreach (KeyValuePair<string, byte[]> stored in pack.Objects)
            {
                await ((IPackStorePruning)origin).DeleteAsync(stored.Key);
            }
        }
    }

    /// <summary>
    /// The container this leg writes into, built the way a HOST would: a shared-access signature in the query
    /// authenticates itself, and a bare container URL gets the ambient identity.
    /// </summary>
    static BlobContainerClient Container()
    {
        var container = new Uri(
            Environment.GetEnvironmentVariable(AzureBlobPackStoreFactAttribute.EnvironmentVariable)!);
        return string.IsNullOrEmpty(container.Query)
            ? new BlobContainerClient(container, new DefaultAzureCredential())
            : new BlobContainerClient(container);
    }
}
