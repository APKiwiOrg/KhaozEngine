using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.AzureBlob;
using Xunit;

namespace KhaozEngine.Tests.Catalog.AzureBlob;

/// <summary>
/// What the blob store does that the conformance suite cannot ask of a provider in general: the conditional
/// upload, the immutable cache headers, the key layout a client already fetches by, and the two halves it
/// deliberately does NOT have.
/// </summary>
public sealed class AzureBlobPackStoreTests
{
    // A container is written by more than one publisher host, and two of them putting the same chunk is the
    // ordinary case rather than an error: the name is the content, so the loser of the race wrote the same
    // bytes. The upload is conditional so there is no read-then-write window at all, and the condition
    // failing is success.
    [Fact]
    public async Task PutAsync_uploads_if_absent_so_a_concurrent_writer_of_the_same_bytes_is_not_an_error()
    {
        var container = new InMemoryBlobContainer { ConcurrentWriterWins = true };
        var store = new AzureBlobPackStore(container);
        StoredObject english = CatalogChunks.English();

        await store.PutAsync(english.Hash, english.File);

        Assert.Equal(1, container.Uploads);
        Assert.Equal(0, container.Writes);
        Assert.True(await store.ExistsAsync(english.Hash));
    }

    [Fact]
    public async Task PutAsync_never_overwrites_a_hash_object()
    {
        var container = new InMemoryBlobContainer();
        var store = new AzureBlobPackStore(container);
        StoredObject english = CatalogChunks.English();

        await store.PutAsync(english.Hash, english.File);
        await store.PutAsync(english.Hash, english.File);
        await store.PutAsync(english.Hash, english.File);

        // Three calls, three conditional uploads, ONE write. A hash object is immutable, so the store must
        // never be the thing that rewrites one, and the condition is what holds that rather than a check.
        Assert.Equal(3, container.Uploads);
        Assert.Equal(1, container.Writes);
    }

    [Fact]
    public async Task A_hash_object_carries_the_immutable_cache_control_and_the_binary_content_type()
    {
        var container = new InMemoryBlobContainer();
        var store = new AzureBlobPackStore(container);
        StoredObject english = CatalogChunks.English();

        await store.PutAsync(english.Hash, english.File);

        BlobObjectHeaders headers = container.HeadersOf(Assert.Single(container.Keys));
        Assert.Equal("application/octet-stream", headers.ContentType);
        Assert.Equal("public, max-age=31536000, immutable", headers.CacheControl);
        Assert.Equal(AzureBlobPackStore.ObjectContentType, headers.ContentType);
        Assert.Equal(AzureBlobPackStore.ObjectCacheControl, headers.CacheControl);
    }

    // The point of the whole package: a client reads this container through HttpPackStore and needs no
    // change to do it. The assertion is not that the two layout rules agree, it is that a REAL HttpPackStore
    // fetches what a real AzureBlobPackStore wrote, over the keys the container actually holds.
    [Fact]
    public async Task The_keys_written_are_the_ones_HttpPackStore_requests()
    {
        var container = new InMemoryBlobContainer();
        var writer = new AzureBlobPackStore(container);
        StoredObject english = CatalogChunks.English();
        StoredObject rules = CatalogChunks.Rules();
        await writer.PutAsync(english.Hash, english.File);
        await writer.PutAsync(rules.Hash, rules.File);

        using var handler = new ContainerHandler(container);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://origin.example/pack/") };
        var reader = new HttpPackStore(client);

        ReadOnlyMemory<byte>? fetched = await reader.GetAsync(english.Hash);
        Assert.NotNull(fetched);
        Assert.True(english.File.AsSpan().SequenceEqual(fetched.Value.Span));

        ReadOnlyMemory<byte>? fetchedRules = await reader.GetAsync(rules.Hash);
        Assert.NotNull(fetchedRules);
        Assert.True(rules.File.AsSpan().SequenceEqual(fetchedRules.Value.Span));

        Assert.Equal(
            container.Keys.OrderBy(k => k, StringComparer.Ordinal),
            handler.Requested.OrderBy(k => k, StringComparer.Ordinal));
    }

    // The package's claim end to end, with a real component at every step: a fill out of the server's own
    // file system store, into a real AzureBlobPackStore, read back by a real ContentFetchLoop over a real
    // HttpPackStore whose transport serves the container by key. Nothing here rebuilds a key or a closure,
    // so a divergence between what the fill wrote and what a client asks for goes red.
    [Fact]
    public async Task A_fill_into_a_blob_store_is_what_a_client_reads_through_HttpPackStore()
    {
        using var sourceRoot = new TemporaryPackRoot();
        using var cacheRoot = new TemporaryPackRoot();
        var source = new FileSystemPackStore(sourceRoot.Path);
        ClientPack pack = ClientPack.Build();
        await pack.WriteAsync(source);

        var container = new InMemoryBlobContainer();
        var origin = new AzureBlobPackStore(container);

        ContentOriginFillResult fill = await ContentOriginFill.RunAsync(
            source, origin, pack.Version, pack.Registry);

        Assert.True(fill.Filled, fill.RefusalReason ?? "no reason");
        Assert.Equal(pack.Objects.Count, fill.ObjectsWritten);
        Assert.Equal(pack.Objects.Count, fill.ObjectsRequired);

        using var handler = new ContainerHandler(container);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://origin.example/pack/") };
        var loop = new ContentFetchLoop(
            new FileSystemPackStore(cacheRoot.Path),
            new HttpPackStore(client),
            pack.Registry,
            new ContentFetchOptions { ClientBuild = 10_000, Attempts = 1, BackoffBase = TimeSpan.Zero });

        ContentFetchResult fetched = await loop.FetchAsync(pack.Version);

        Assert.True(fetched.Success, fetched.Reason ?? "no reason");

        // The manifest is not in the required set, because it is what named the rest.
        Assert.Equal(pack.Objects.Count - 1, fetched.Progress.ChunksRequired);
        Assert.NotEmpty(handler.Requested);
        Assert.All(handler.Requested, key => Assert.NotNull(container.Read(key)));

        foreach (KeyValuePair<string, byte[]> stored in pack.Objects)
        {
            ReadOnlyMemory<byte>? cached = await loop.Store.Local.GetAsync(stored.Key);
            Assert.NotNull(cached);
            Assert.True(stored.Value.AsSpan().SequenceEqual(cached.Value.Span));
        }
    }

    [Fact]
    public async Task A_key_is_lower_case_with_forward_slashes_and_no_leading_slash()
    {
        var container = new InMemoryBlobContainer();
        var store = new AzureBlobPackStore(container);
        StoredObject english = CatalogChunks.English();

        await store.PutAsync(english.Hash, english.File);

        string hash = english.Hash;
        string key = Assert.Single(container.Keys);
        Assert.Equal(hash[..2] + "/" + hash.Substring(2, 2) + "/" + hash + ".kec", key);
        Assert.Equal(key.ToLowerInvariant(), key);
        Assert.DoesNotContain('\\', key);
        Assert.False(key.StartsWith('/'));
    }

    // A public origin gets no versions/<n> pointer, so there is nothing for a listing to answer FROM. The
    // empty sequence is the same answer a store whose pointer is unreadable gives, which is the publish
    // sweep's own skip condition: nothing here can ever authorize a delete.
    [Fact]
    public async Task ListAsync_yields_nothing_because_the_store_holds_no_version_pointer()
    {
        var container = new InMemoryBlobContainer();
        var store = new AzureBlobPackStore(container);
        StoredObject english = CatalogChunks.English();
        await store.PutAsync(english.Hash, english.File);

        var listed = new List<string>();
        await foreach (string hash in store.ListAsync(7))
        {
            listed.Add(hash);
        }

        Assert.Empty(listed);
    }

    // What this can pin is the CEILING the store hands down, which is the fetch path's own: a client reading
    // this container applies exactly that number, so a writer's store that would hand back more is a store
    // the client could not read. That the ceiling is applied to the declared length BEFORE a body is
    // buffered is the adapter's, against a real service, and is the live leg's to pin: the double does its
    // own length check, so BodiesMaterialized here says only that this double did not build one.
    [Fact]
    public async Task GetAsync_hands_the_fetch_paths_own_ceiling_down_to_the_container()
    {
        var container = new InMemoryBlobContainer();
        var store = new AzureBlobPackStore(container);
        string hash = new('a', 64);
        container.PlanOversize(
            FileSystemPackStore.RelativeKeyFor(hash),
            ContentPackFormat.MaxObjectBytes + 1L);

        Assert.Null(await store.GetAsync(hash));
        Assert.Equal(ContentPackFormat.MaxObjectBytes, container.CeilingAsked);
        Assert.Equal(0, container.BodiesMaterialized);
        Assert.True(await store.ExistsAsync(hash));
    }

    [Fact]
    public async Task EnumerateAsync_ignores_a_key_that_is_not_a_hash_object()
    {
        var container = new InMemoryBlobContainer();
        var store = new AzureBlobPackStore(container);
        StoredObject english = CatalogChunks.English();
        await store.PutAsync(english.Hash, english.File);
        container.PlanForeignObject("versions/7");
        container.PlanForeignObject("index.html");
        container.PlanForeignObject("ab/cd/not-a-hash.kec");

        var held = new List<string>();
        await foreach (string hash in ((IPackStorePruning)store).EnumerateAsync())
        {
            held.Add(hash);
        }

        Assert.Equal(english.Hash, Assert.Single(held));
    }

    // A container holds whatever a host put in it, and a .kec at any other depth is not this store's object:
    // the canonical key is derived from the hash, so a stray copy elsewhere would be enumerated under an
    // address whose real object is a different blob, and a prune driven off the enumeration would then delete
    // the canonical one.
    [Fact]
    public async Task EnumerateAsync_ignores_a_hash_object_that_is_not_at_its_canonical_key()
    {
        var container = new InMemoryBlobContainer();
        var store = new AzureBlobPackStore(container);
        StoredObject english = CatalogChunks.English();
        StoredObject french = CatalogChunks.French();
        await store.PutAsync(english.Hash, english.File);
        container.PlanForeignObject("junk/" + french.Hash + ".kec");
        container.PlanForeignObject(french.Hash + ".kec");
        container.PlanForeignObject("ab/cd/" + french.Hash + ".kec");

        var held = new List<string>();
        await foreach (string hash in ((IPackStorePruning)store).EnumerateAsync())
        {
            held.Add(hash);
        }

        Assert.Equal(english.Hash, Assert.Single(held));
    }

    // A read that FAULTED and a read that found nothing are ONE answer, which is what IPackStore.GetAsync
    // promises: null for absent, so a sweep or a validation pass is never taken down by one lookup. Both
    // incumbent providers keep that promise for a fault too.
    //
    // The translation lives in the ADAPTER, which is the one file that names an SDK exception, so the
    // in-memory double never sees one and this fact cannot exercise that catch. What the double reproduces is
    // the answer the adapter gives AFTERWARDS, over an object the container still holds, and what is asserted
    // directly is the adapter's classification, which is the closest a test without a service can get. The
    // catch itself against the real service is the live leg's, per BlobContainerAdapter's own doc.
    [Fact]
    public async Task A_read_fault_on_the_origin_is_an_absent_answer_and_never_a_throw()
    {
        var container = new InMemoryBlobContainer();
        var store = new AzureBlobPackStore(container);
        StoredObject english = CatalogChunks.English();
        await store.PutAsync(english.Hash, english.File);

        container.ReadsFault = true;

        Assert.False(await store.ExistsAsync(english.Hash));
        Assert.Null(await store.GetAsync(english.Hash));
        Assert.NotNull(container.Read(FileSystemPackStore.RelativeKeyFor(english.Hash)));

        // An expired signature, a throttle and a body that died mid transfer: the caller's next move is the
        // same for all of them and for an absent object, which is to retry or to try another source.
        Assert.True(BlobContainerAdapter.IsAbsentReadAnswer(new RequestFailedException(403, "signature expired")));
        Assert.True(BlobContainerAdapter.IsAbsentReadAnswer(new RequestFailedException(429, "throttled")));
        Assert.True(BlobContainerAdapter.IsAbsentReadAnswer(new RequestFailedException(503, "unavailable")));
        Assert.True(BlobContainerAdapter.IsAbsentReadAnswer(new RequestFailedException(404, "absent")));
        Assert.True(BlobContainerAdapter.IsAbsentReadAnswer(new IOException("the connection died")));
        Assert.True(BlobContainerAdapter.IsAbsentReadAnswer(new EndOfStreamException()));

        // A cancellation is the CALLER's own decision and is never absorbed.
        Assert.False(BlobContainerAdapter.IsAbsentReadAnswer(new OperationCanceledException()));
        Assert.False(BlobContainerAdapter.IsAbsentReadAnswer(new TaskCanceledException()));
    }

    // A store with no pointer half cannot be a ContentPublisher or a ContentPackRebuild target: both resolve
    // one and refuse with no-pack-store when there is none. That is the compile-time reason a full server
    // pack can never be published into a PUBLIC container by mistake, and it is asserted by interface NAME
    // because IPackVersionPointerStore lives in Catalog.Authoring, which this test project must not
    // reference at all.
    [Fact]
    public void The_store_is_not_a_publish_target_because_it_has_no_pointer_half()
    {
        string[] implemented = typeof(AzureBlobPackStore).GetInterfaces().Select(t => t.Name).ToArray();

        Assert.Contains("IPackStore", implemented);
        Assert.Contains("IPackStorePruning", implemented);
        Assert.DoesNotContain("IPackVersionPointerStore", implemented);
        Assert.DoesNotContain("IContentVersionPointerSource", implemented);
    }

    [Fact]
    public void A_store_built_over_no_container_refuses_to_exist()
    {
        Assert.Throws<ArgumentNullException>(() => new AzureBlobPackStore((BlobContainerClient)null!));
    }

    /// <summary>
    /// Serves the fake container's dictionary by key, which is what a blob container with public read does
    /// over HTTPS. It records every path asked for, so the key comparison is over what was REQUESTED rather
    /// than over what the test rebuilt.
    /// </summary>
    sealed class ContainerHandler(InMemoryBlobContainer container) : HttpMessageHandler
    {
        // A fetch loop asks for several objects at once, so the record is a concurrent one: a List here
        // fails a test for a reason that belongs to the test and not to the store.
        readonly ConcurrentQueue<string> _requested = new();

        /// <summary>Every key requested, without the base path, in arrival order.</summary>
        public IReadOnlyCollection<string> Requested => _requested;

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string key = request.RequestUri!.AbsolutePath.TrimStart('/');
            key = key.StartsWith("pack/", StringComparison.Ordinal) ? key["pack/".Length..] : key;
            _requested.Enqueue(key);

            byte[]? bytes = container.Read(key);
            var response = new HttpResponseMessage(bytes is null ? HttpStatusCode.NotFound : HttpStatusCode.OK);
            if (bytes is not null)
            {
                response.Content = new ByteArrayContent(bytes);
            }

            return Task.FromResult(response);
        }
    }
}
