using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Store;

/// <summary>
/// The verifying decorator of spec 8.4: a LOCAL store in front of a REMOTE one, with the hash checked on
/// every READ and not only on write.
/// <para>
/// The verification is the whole value of the decorator. A mismatching object is never cached and never
/// used, which is the entire defence against a poisoned CDN or a corrupted proxy, and checking on read is
/// what makes a local file replaced with attacker bytes self-healing rather than permanent (spec 11 row 7).
/// </para>
/// <para>
/// The decompression that feeds the verify is BOUNDED and the ORDER is load bearing, because verifying a
/// hash taken over uncompressed bytes means decompressing bytes that are not yet trusted. The refusals each
/// get a test here, through the decorator, because that is the caller that holds bytes off a wire.
/// </para>
/// </summary>
public class CachingPackStoreTests
{
    [Fact]
    public async Task GetAsync_misses_through_to_the_remote_writes_through_to_the_cache_and_hits_it_next_time()
    {
        CatalogPack pack = CatalogPack.Build();
        var local = new MemoryPackStore();
        var remote = new MemoryPackStore();
        remote.Plant(pack.TagChunk.Hash, pack.TagChunk.StoredFile.ToArray());
        var store = new CachingPackStore(local, remote);

        ReadOnlyMemory<byte>? first = await store.GetAsync(pack.TagChunk.Hash);

        Assert.NotNull(first);
        Assert.Equal(pack.TagChunk.StoredFile, first.Value.ToArray());
        Assert.Equal(1, remote.Gets);
        Assert.True(await local.ExistsAsync(pack.TagChunk.Hash));

        ReadOnlyMemory<byte>? second = await store.GetAsync(pack.TagChunk.Hash);

        Assert.NotNull(second);
        Assert.Equal(pack.TagChunk.StoredFile, second.Value.ToArray());
        Assert.Equal(1, remote.Gets);
    }

    [Fact]
    public async Task GetAsync_answers_null_when_neither_source_holds_it()
    {
        var store = new CachingPackStore(new MemoryPackStore(), new MemoryPackStore());

        Assert.Null(await store.GetAsync(new string('a', 64)));
    }

    [Fact]
    public async Task A_name_that_is_not_a_content_address_reaches_neither_source()
    {
        var local = new MemoryPackStore();
        var remote = new MemoryPackStore();
        var store = new CachingPackStore(local, remote);

        Assert.Null(await store.GetAsync("../../etc/passwd"));
        Assert.False(await store.ExistsAsync("../../etc/passwd"));
        Assert.Equal(0, local.Gets);
        Assert.Equal(0, remote.Gets);
    }

    [Fact]
    public async Task A_remote_object_that_does_not_hash_to_its_name_is_reported_and_never_cached()
    {
        CatalogPack pack = CatalogPack.Build();
        byte[] tampered = pack.TagChunk.StoredFile.ToArray();
        tampered[^1] ^= 0xFF;
        var local = new MemoryPackStore();
        var remote = new MemoryPackStore();
        remote.Plant(pack.TagChunk.Hash, tampered);
        var refusals = new List<(string Hash, string Reason)>();
        var store = new CachingPackStore(local, remote, (hash, reason) => refusals.Add((hash, reason)));

        Assert.Null(await store.GetAsync(pack.TagChunk.Hash));
        Assert.False(await local.ExistsAsync(pack.TagChunk.Hash));
        Assert.Equal((pack.TagChunk.Hash, ContentPackReader.ReasonHashMismatch), Assert.Single(refusals));
    }

    // Spec 11 row 7, client cache poisoning: the entry is discarded and refetched, automatically. This is the
    // reason the cache verifies on READ and not only on write, and the reason the local bytes are planted here
    // rather than PutAsync'd: PutAsync verifies too, so a store's own API cannot produce the state this test
    // is about.
    [Fact]
    public async Task A_cached_object_replaced_with_other_bytes_is_discarded_refetched_and_heals()
    {
        CatalogPack pack = CatalogPack.Build();
        byte[] poisoned = pack.TagChunk.StoredFile.ToArray();
        poisoned[^1] ^= 0xFF;
        var local = new MemoryPackStore();
        local.Plant(pack.TagChunk.Hash, poisoned);
        var remote = new MemoryPackStore();
        remote.Plant(pack.TagChunk.Hash, pack.TagChunk.StoredFile.ToArray());
        var refusals = new List<(string Hash, string Reason)>();
        var store = new CachingPackStore(local, remote, (hash, reason) => refusals.Add((hash, reason)));

        ReadOnlyMemory<byte>? bytes = await store.GetAsync(pack.TagChunk.Hash);

        Assert.NotNull(bytes);
        Assert.Equal(pack.TagChunk.StoredFile, bytes.Value.ToArray());
        Assert.Equal(1, remote.Gets);
        Assert.Equal((pack.TagChunk.Hash, ContentPackReader.ReasonHashMismatch), Assert.Single(refusals));

        ReadOnlyMemory<byte>? healed = await local.GetAsync(pack.TagChunk.Hash);
        Assert.NotNull(healed);
        Assert.Equal(pack.TagChunk.StoredFile, healed.Value.ToArray());
    }

    // The same healing over the store a client actually caches in, which is the case the eviction is FOR:
    // FileSystemPackStore.PutAsync is a no-op when the name already exists, because the name is the content,
    // so a poisoned entry that was not deleted first would never be replaced and the client would be stuck.
    [Fact]
    public async Task A_poisoned_file_in_the_file_system_cache_is_deleted_and_replaced()
    {
        using var root = new TemporaryRoot();
        CatalogPack pack = CatalogPack.Build();
        var local = new FileSystemPackStore(root.Path);
        await local.PutAsync(pack.TagChunk.Hash, pack.TagChunk.StoredFile);
        byte[] poisoned = pack.TagChunk.StoredFile.ToArray();
        poisoned[^1] ^= 0xFF;
        await System.IO.File.WriteAllBytesAsync(local.PathFor(pack.TagChunk.Hash), poisoned);
        var remote = new MemoryPackStore();
        remote.Plant(pack.TagChunk.Hash, pack.TagChunk.StoredFile.ToArray());
        var store = new CachingPackStore(local, remote);

        ReadOnlyMemory<byte>? bytes = await store.GetAsync(pack.TagChunk.Hash);

        Assert.NotNull(bytes);
        Assert.Equal(pack.TagChunk.StoredFile, bytes.Value.ToArray());
        Assert.Equal(
            pack.TagChunk.StoredFile.ToArray(),
            await System.IO.File.ReadAllBytesAsync(local.PathFor(pack.TagChunk.Hash)));
    }

    // The hostile case, written explicitly: a 40 KB body whose header declares uncompressedBytes = 0xFFFFFFFF
    // is refused BEFORE any allocation. The declared length is the sender's number, so it is bounded against
    // the format's own maximum before it is believed.
    [Fact]
    public async Task A_body_declaring_a_four_gigabyte_uncompressed_length_is_refused_before_any_allocation()
    {
        CatalogPack pack = CatalogPack.Build();
        byte[] hostile = HostileChunk(pack.TagChunk.StoredFile, uncompressedBytes: uint.MaxValue, bodyBytes: 40 * 1024);
        var local = new MemoryPackStore();
        var remote = new MemoryPackStore();
        string address = new('b', 64);
        remote.Plant(address, hostile);
        var refusals = new List<(string Hash, string Reason)>();
        var store = new CachingPackStore(local, remote, (hash, reason) => refusals.Add((hash, reason)));

        Assert.Null(await store.GetAsync(address));
        Assert.False(await local.ExistsAsync(address));
        Assert.Equal((address, ContentChunkCodec.ReasonTooLarge), Assert.Single(refusals));
    }

    [Fact]
    public async Task A_stored_length_that_disagrees_with_the_body_it_arrived_with_is_refused()
    {
        CatalogPack pack = CatalogPack.Build();
        byte[] shortened = pack.TagChunk.StoredFile.ToArray()[..^1];
        var local = new MemoryPackStore();
        var remote = new MemoryPackStore();
        remote.Plant(pack.TagChunk.Hash, shortened);
        var refusals = new List<(string Hash, string Reason)>();
        var store = new CachingPackStore(local, remote, (hash, reason) => refusals.Add((hash, reason)));

        Assert.Null(await store.GetAsync(pack.TagChunk.Hash));
        Assert.Equal((pack.TagChunk.Hash, ContentChunkCodec.ReasonStoredLength), Assert.Single(refusals));
    }

    [Fact]
    public async Task A_manifest_that_does_not_digest_to_its_name_is_refused_with_the_manifest_token()
    {
        CatalogPack pack = CatalogPack.Build();
        var local = new MemoryPackStore();
        var remote = new MemoryPackStore();
        remote.Plant(new string('c', 64), pack.ServerManifestFile.ToArray());
        var refusals = new List<(string Hash, string Reason)>();
        var store = new CachingPackStore(local, remote, (hash, reason) => refusals.Add((hash, reason)));

        Assert.Null(await store.GetAsync(new string('c', 64)));
        Assert.Equal(ContentPackReader.ReasonManifestHashMismatch, Assert.Single(refusals).Reason);
    }

    [Fact]
    public async Task ExistsAsync_asks_the_cache_first_and_the_remote_only_on_a_miss()
    {
        CatalogPack pack = CatalogPack.Build();
        var local = new MemoryPackStore();
        local.Plant(pack.TagChunk.Hash, pack.TagChunk.StoredFile.ToArray());
        var remote = new MemoryPackStore();
        remote.Plant(new string('d', 64), pack.TagChunk.StoredFile.ToArray());
        var store = new CachingPackStore(local, remote);

        Assert.True(await store.ExistsAsync(pack.TagChunk.Hash));
        Assert.Equal(0, remote.Exists);

        Assert.True(await store.ExistsAsync(new string('d', 64)));
        Assert.Equal(1, remote.Exists);
    }

    [Fact]
    public async Task PutAsync_goes_to_the_cache_only()
    {
        CatalogPack pack = CatalogPack.Build();
        var local = new MemoryPackStore();
        var remote = new MemoryPackStore();
        var store = new CachingPackStore(local, remote);

        await store.PutAsync(pack.TagChunk.Hash, pack.TagChunk.StoredFile);

        Assert.True(await local.ExistsAsync(pack.TagChunk.Hash));
        Assert.Equal(0, remote.Puts);
    }

    [Fact]
    public async Task PutAsync_verifies_the_way_every_store_does()
    {
        CatalogPack pack = CatalogPack.Build();
        byte[] tampered = pack.TagChunk.StoredFile.ToArray();
        tampered[^1] ^= 0xFF;
        var store = new CachingPackStore(new MemoryPackStore(), new MemoryPackStore());

        await Assert.ThrowsAsync<ContentPackException>(
            () => store.PutAsync(pack.TagChunk.Hash, tampered));
    }

    // Pruning is the CACHE's and never the remote's: a decorator that could delete from the origin would let
    // one client's eviction policy empty a CDN.
    [Fact]
    public async Task Pruning_is_delegated_to_the_cache_and_never_reaches_the_remote()
    {
        CatalogPack pack = CatalogPack.Build();
        var local = new MemoryPackStore();
        local.Plant(pack.TagChunk.Hash, pack.TagChunk.StoredFile.ToArray());
        var remote = new MemoryPackStore();
        remote.Plant(pack.ItemChunkZero.Hash, pack.ItemChunkZero.StoredFile.ToArray());
        var store = new CachingPackStore(local, remote);

        var held = new List<string>();
        await foreach (string hash in store.EnumerateAsync())
        {
            held.Add(hash);
        }

        Assert.Equal(pack.TagChunk.Hash, Assert.Single(held));
        Assert.True(await store.DeleteAsync(pack.TagChunk.Hash));
        Assert.False(await store.DeleteAsync(pack.ItemChunkZero.Hash));
        Assert.Equal(0, remote.Deletes);
        Assert.True(await remote.ExistsAsync(pack.ItemChunkZero.Hash));
    }

    [Fact]
    public async Task A_cache_that_cannot_prune_answers_empty_rather_than_reaching_past_itself()
    {
        var store = new CachingPackStore(new ReadOnlyPackStore(), new MemoryPackStore());

        var held = new List<string>();
        await foreach (string hash in store.EnumerateAsync())
        {
            held.Add(hash);
        }

        Assert.Empty(held);
        Assert.False(await store.DeleteAsync(new string('a', 64)));
    }

    // The listing is the publish SWEEP's member and the sweep runs on the publisher's own store, never
    // through a client's cache. An empty listing is the seam's own "the listing failed" answer, which is the
    // sweep's skip condition, so the decorator answering from the cache alone can never authorize a delete.
    [Fact]
    public async Task ListAsync_answers_from_the_cache_alone()
    {
        CatalogPack pack = CatalogPack.Build();
        var local = new MemoryPackStore();
        local.Plant(pack.TagChunk.Hash, pack.TagChunk.StoredFile.ToArray());
        var remote = new MemoryPackStore();
        remote.Listing(CatalogPack.VersionNumber, [pack.ServerManifestHash]);
        var store = new CachingPackStore(local, remote);

        var listed = new List<string>();
        await foreach (string hash in store.ListAsync(CatalogPack.VersionNumber))
        {
            listed.Add(hash);
        }

        Assert.Empty(listed);
        Assert.Equal(0, remote.Lists);
    }

    [Fact]
    public void A_decorator_over_a_missing_half_refuses_to_be_built()
    {
        Assert.Throws<ArgumentNullException>(() => new CachingPackStore(null!, new MemoryPackStore()));
        Assert.Throws<ArgumentNullException>(() => new CachingPackStore(new MemoryPackStore(), null!));
    }

    /// <summary>
    /// A chunk file whose header claims a body the file does not carry: the same 36 byte header, with
    /// <c>uncompressedBytes</c> and <c>storedBytes</c> rewritten and a body of the given size.
    /// </summary>
    static byte[] HostileChunk(ReadOnlyMemory<byte> file, uint uncompressedBytes, int bodyBytes)
    {
        byte[] hostile = new byte[ContentPackFormat.ChunkHeaderBytes + bodyBytes];
        file.Span[..ContentPackFormat.ChunkHeaderBytes].CopyTo(hostile);
        BinaryPrimitives.WriteUInt32LittleEndian(hostile.AsSpan(28), uncompressedBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(hostile.AsSpan(32), (uint)bodyBytes);
        return hostile;
    }

    /// <summary>
    /// A store that holds bytes in memory and counts what it was asked. <see cref="Plant"/> is what
    /// <see cref="IPackStore.PutAsync"/> deliberately is not: an unverified write, which is the only way to
    /// build the poisoned-cache state the decorator exists to heal.
    /// </summary>
    internal sealed class MemoryPackStore : IPackStore, IPackStorePruning
    {
        readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        readonly Dictionary<int, IReadOnlyList<string>> _listings = [];

        public int Gets { get; private set; }

        public int Exists { get; private set; }

        public int Puts { get; private set; }

        public int Lists { get; private set; }

        public int Deletes { get; private set; }

        /// <summary>Files bytes under a name WITHOUT verifying they digest to it.</summary>
        public void Plant(string hash, byte[] bytes) => _objects[hash] = bytes;

        /// <summary>What <see cref="ListAsync"/> answers for one version.</summary>
        public void Listing(int versionNumber, IReadOnlyList<string> hashes) => _listings[versionNumber] = hashes;

        public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
        {
            Exists++;
            return Task.FromResult(_objects.ContainsKey(hash));
        }

        public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
        {
            Gets++;

            // Written as two statements on purpose. A conditional whose other arm is `null` binds through the
            // implicit byte[] to ReadOnlyMemory<byte> conversion and yields an EMPTY memory rather than a
            // nullable null, so a miss would come back as bytes that are present and zero long.
            if (!_objects.TryGetValue(hash, out byte[]? bytes))
            {
                return Task.FromResult<ReadOnlyMemory<byte>?>(null);
            }

            return Task.FromResult<ReadOnlyMemory<byte>?>(bytes);
        }

        public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Puts++;
            if (!ContentPackReader.TryVerify(bytes.Span, hash, out string? reason))
            {
                throw new ContentPackException("The bytes do not digest to the name.", hash, reason);
            }

            _objects[hash] = bytes.ToArray();
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ListAsync(
            int versionNumber,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Lists++;
            await Task.Yield();
            foreach (string hash in _listings.TryGetValue(versionNumber, out IReadOnlyList<string>? hashes)
                ? hashes
                : [])
            {
                yield return hash;
            }
        }

        public async IAsyncEnumerable<string> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (string hash in _objects.Keys.ToArray())
            {
                yield return hash;
            }
        }

        public Task<bool> DeleteAsync(string hash, CancellationToken cancellationToken = default)
        {
            Deletes++;
            return Task.FromResult(_objects.Remove(hash));
        }
    }

    /// <summary>A store with no pruning half at all, which is the read-only provider's shape.</summary>
    sealed class ReadOnlyPackStore : IPackStore
    {
        public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
            => Task.FromResult<ReadOnlyMemory<byte>?>(null);

        public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
