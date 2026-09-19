using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
public class AzureBlobPackStoreTests
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

    [Fact]
    public async Task GetAsync_refuses_an_object_over_the_size_ceiling_before_buffering_it()
    {
        var container = new InMemoryBlobContainer();
        var store = new AzureBlobPackStore(container);
        string hash = new('a', 64);
        container.PlanOversize(FileSystemPackStore.RelativeKeyFor(hash), HttpPackStore.MaxObjectBytes + 1L);

        Assert.Null(await store.GetAsync(hash));
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
        container.PlanOversize("versions/7", 64);
        container.PlanOversize("index.html", 64);
        container.PlanOversize("ab/cd/not-a-hash.kec", 64);

        var held = new List<string>();
        await foreach (string hash in ((IPackStorePruning)store).EnumerateAsync())
        {
            held.Add(hash);
        }

        Assert.Equal(english.Hash, Assert.Single(held));
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
        readonly List<string> _requested = [];

        /// <summary>Every key requested, without the base path, in arrival order.</summary>
        public IReadOnlyList<string> Requested => _requested;

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string key = request.RequestUri!.AbsolutePath.TrimStart('/');
            key = key.StartsWith("pack/", StringComparison.Ordinal) ? key["pack/".Length..] : key;
            _requested.Add(key);

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
