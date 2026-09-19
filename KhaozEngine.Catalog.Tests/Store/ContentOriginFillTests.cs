using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Store;

/// <summary>
/// The origin fill a game server runs before it opens a socket: the active version's CLIENT closure, pushed
/// from the server's own pack store into the origin its clients fetch from.
/// <para>
/// The claim under test is not that the fill is fast. It is that the origin ends up holding the client
/// closure and NOTHING else: no server only chunk, no server manifest, and no version pointer, because an
/// origin is public and a pointer carries the server manifest hash. The fill is a named wrapper over
/// <see cref="ContentFetchLoop"/> pointed the other way round, so everything the loop already proves about
/// verification and atomicity holds here and is not retested.
/// </para>
/// </summary>
public class ContentOriginFillTests
{
    // The whole point of the wrapper in one fact: an empty origin ends up byte for byte holding the client
    // manifest and every hash it names, and a real client fetch loop pointed at that origin completes.
    [Fact]
    public async Task A_fill_into_an_empty_origin_holds_the_client_closure_and_a_client_then_fetches_it()
    {
        using var sourceRoot = new TemporaryRoot();
        using var originRoot = new TemporaryRoot();
        using var cacheRoot = new TemporaryRoot();
        var source = new FileSystemPackStore(sourceRoot.Path);
        var origin = new FileSystemPackStore(originRoot.Path);
        PublishedVersion published = await PublishAsync(source);

        ContentOriginFillResult fill = await ContentOriginFill.RunAsync(
            source, origin, published.ClientVersion, published.Registry);

        Assert.True(fill.Filled, fill.RefusalReason ?? "no reason");
        Assert.Null(fill.RefusalReason);
        Assert.Null(fill.RefusalHash);

        IReadOnlyList<string> closure = published.ClientClosure();
        Assert.Equal(closure.Order(), (await HashesAsync(origin)).Order());
        Assert.Equal(closure.Count, fill.ObjectsRequired);
        Assert.Equal(closure.Count, fill.ObjectsWritten);

        foreach (string hash in closure)
        {
            ReadOnlyMemory<byte>? filed = await origin.GetAsync(hash);
            ReadOnlyMemory<byte>? original = await source.GetAsync(hash);
            Assert.NotNull(filed);
            Assert.NotNull(original);
            Assert.Equal(original.Value.ToArray(), filed.Value.ToArray());
        }

        var client = new ContentFetchLoop(
            new FileSystemPackStore(cacheRoot.Path),
            origin,
            published.Registry,
            ClientOptions());
        ContentFetchResult fetched = await client.FetchAsync(published.ClientVersion);

        Assert.True(fetched.Success, fetched.Reason ?? "no reason");
    }

    // The security fact. A server only chunk is bytes no client may ever hold, and an origin is public, so a
    // fill that copied the whole version would publish the loot tables to the world.
    [Fact]
    public async Task A_server_only_chunk_never_lands_in_the_origin()
    {
        using var sourceRoot = new TemporaryRoot();
        using var originRoot = new TemporaryRoot();
        var source = new FileSystemPackStore(sourceRoot.Path);
        var origin = new FileSystemPackStore(originRoot.Path);
        PublishedVersion published = await PublishAsync(source);

        IReadOnlyList<string> serverOnly = published.ServerOnlyChunkHashes();
        Assert.NotEmpty(serverOnly);

        Assert.True((await ContentOriginFill.RunAsync(
            source, origin, published.ClientVersion, published.Registry)).Filled);

        IReadOnlyList<string> held = await HashesAsync(origin);
        foreach (string hash in serverOnly)
        {
            Assert.False(
                held.Contains(hash, StringComparer.Ordinal),
                "A ServerOnly chunk reached the public origin: " + hash);
        }

        Assert.False(
            held.Contains(published.Published.ServerManifestHash, StringComparer.Ordinal),
            "The SERVER manifest reached the public origin, which names every server only chunk there is.");
        Assert.False(await origin.ExistsAsync(published.Published.ServerManifestHash));
    }

    // The fill is callable on every boot, because the missing set is computed against the origin itself. A
    // restart that activates the version already filled writes no object at all.
    [Fact]
    public async Task A_second_fill_writes_nothing()
    {
        using var sourceRoot = new TemporaryRoot();
        using var originRoot = new TemporaryRoot();
        var source = new FileSystemPackStore(sourceRoot.Path);
        var origin = new FileSystemPackStore(originRoot.Path);
        PublishedVersion published = await PublishAsync(source);

        ContentOriginFillResult first = await ContentOriginFill.RunAsync(
            source, origin, published.ClientVersion, published.Registry);
        ContentOriginFillResult second = await ContentOriginFill.RunAsync(
            source, origin, published.ClientVersion, published.Registry);

        Assert.True(first.Filled);
        Assert.True(second.Filled);
        Assert.True(first.ObjectsWritten > 0);
        Assert.Equal(0, second.ObjectsWritten);
        Assert.Equal(first.ObjectsRequired, second.ObjectsRequired);
        Assert.Equal(published.ClientClosure().Order(), (await HashesAsync(origin)).Order());
    }

    // The gate bypass. A version that raises MinimumClientBuild refuses every client below it, and the
    // SERVER filling the origin for that version is not a client: refusing it there would strand the very
    // version the players are being asked to update for.
    [Fact]
    public async Task A_version_whose_minimum_client_build_is_raised_still_fills()
    {
        using var sourceRoot = new TemporaryRoot();
        using var originRoot = new TemporaryRoot();
        using var cacheRoot = new TemporaryRoot();
        var source = new FileSystemPackStore(sourceRoot.Path);
        var origin = new FileSystemPackStore(originRoot.Path);
        PublishedVersion published = await PublishAsync(source, minimumClientBuild: 5000);

        Assert.Equal(5000u, published.ClientManifest.MinimumClientBuild);

        ContentOriginFillResult fill = await ContentOriginFill.RunAsync(
            source, origin, published.ClientVersion, published.Registry);

        Assert.True(fill.Filled, fill.RefusalReason ?? "no reason");
        Assert.Equal(published.ClientClosure().Order(), (await HashesAsync(origin)).Order());

        // The gate is real, which is what makes the bypass worth a fact: the same version through a CLIENT
        // loop carrying an ordinary build ordinal is refused.
        var client = new ContentFetchLoop(
            new FileSystemPackStore(cacheRoot.Path),
            origin,
            published.Registry,
            ClientOptions(clientBuild: 1));
        ContentFetchResult refused = await client.FetchAsync(published.ClientVersion);

        Assert.False(refused.Success);
        Assert.Equal(ContentFetchOutcome.ClientBuildTooOld, refused.Outcome);
    }

    // A server manifest names every server only chunk of the version, so handing one to a fill is the worst
    // single mistake available here. The wrapper cannot tell the two apart by hash, so it reads the manifest
    // as a CLIENT one and the side byte refuses it, before anything is written.
    [Fact]
    public async Task A_fill_handed_the_server_manifest_hash_is_refused_before_any_write()
    {
        using var sourceRoot = new TemporaryRoot();
        using var originRoot = new TemporaryRoot();
        var source = new FileSystemPackStore(sourceRoot.Path);
        var origin = new FileSystemPackStore(originRoot.Path);
        PublishedVersion published = await PublishAsync(source);

        ContentOriginFillResult fill = await ContentOriginFill.RunAsync(
            source,
            origin,
            published.Published.ServerIdentity,
            published.Registry);

        Assert.False(fill.Filled);
        Assert.Equal(ContentManifestCodec.ReasonWrongSide, fill.RefusalReason);
        Assert.Equal(published.Published.ServerManifestHash, fill.RefusalHash);
        Assert.Equal(0, fill.ObjectsWritten);
        Assert.Empty(await HashesAsync(origin));
        Assert.False(await origin.ExistsAsync(published.Published.ServerManifestHash));
    }

    // A fill that cannot complete leaves the origin without the manifest, so nothing there points at a hole.
    // The chunks that did arrive are content addressed and harmless, and the next fill completes the set.
    [Fact]
    public async Task A_source_missing_a_chunk_is_refused_and_leaves_no_manifest_to_follow()
    {
        using var sourceRoot = new TemporaryRoot();
        using var originRoot = new TemporaryRoot();
        var source = new FileSystemPackStore(sourceRoot.Path);
        var origin = new FileSystemPackStore(originRoot.Path);
        PublishedVersion published = await PublishAsync(source);
        string lost = published.ClientManifest.RemapRuleChunkHash;

        Assert.True(await source.DeleteAsync(lost));

        ContentOriginFillResult fill = await ContentOriginFill.RunAsync(
            source, origin, published.ClientVersion, published.Registry);

        Assert.False(fill.Filled);
        Assert.Equal(ContentPackReader.ReasonFetchFailed, fill.RefusalReason);
        Assert.Equal(lost, fill.RefusalHash);
        Assert.False(
            await origin.ExistsAsync(published.Published.ClientManifestHash),
            "The origin holds a manifest naming a chunk the fill never copied.");
        Assert.DoesNotContain(lost, await HashesAsync(origin));
    }

    // A fault WRITING the origin is the one failure a read cannot be asked to stand in for: a store answers
    // absent for a read it could not do, and has no such answer for a write. It is still a RESULT here,
    // because a fill runs on a boot path where the useful answer is a reason token and an exit code.
    [Fact]
    public async Task A_fill_whose_origin_refuses_a_write_is_a_refusal_result_and_never_a_throw()
    {
        using var sourceRoot = new TemporaryRoot();
        using var originRoot = new TemporaryRoot();
        var source = new FileSystemPackStore(sourceRoot.Path);
        var origin = new FileSystemPackStore(originRoot.Path);
        PublishedVersion published = await PublishAsync(source);
        var refusing = new RefusingWriteStore(origin, _ => true);

        ContentOriginFillResult fill = await ContentOriginFill.RunAsync(
            source, refusing, published.ClientVersion, published.Registry, concurrency: 1);

        Assert.False(fill.Filled);
        Assert.Equal(ContentOriginFill.RefusedOriginWrite, fill.RefusalReason);
        Assert.Equal(0, fill.ObjectsWritten);
        Assert.NotNull(fill.RefusalDetail);
        Assert.Contains(RefusingWriteStore.Message, fill.RefusalDetail, StringComparison.Ordinal);

        // The fill stops at the FIRST write that faulted rather than working through the rest of the
        // closure: a container that refused one object refuses the next one too, and a boot that keeps
        // pushing at a throttled origin makes the throttle worse.
        Assert.Equal(Assert.Single(refusing.Attempted), fill.RefusalHash);
        Assert.Empty(await HashesAsync(origin));
        Assert.False(await origin.ExistsAsync(published.Published.ClientManifestHash));
    }

    // The manifest write is the LAST one and it is made outside the fetch loop, so it is a second place a
    // write fault can arrive from. It answers the same way, and the origin is left with the chunks and no
    // manifest, which is the state the next fill completes.
    [Fact]
    public async Task A_fill_whose_origin_refuses_the_manifest_write_is_a_refusal_result_too()
    {
        using var sourceRoot = new TemporaryRoot();
        using var originRoot = new TemporaryRoot();
        var source = new FileSystemPackStore(sourceRoot.Path);
        var origin = new FileSystemPackStore(originRoot.Path);
        PublishedVersion published = await PublishAsync(source);
        string manifestHash = published.Published.ClientManifestHash;
        var refusing = new RefusingWriteStore(
            origin,
            hash => string.Equals(hash, manifestHash, StringComparison.Ordinal));

        ContentOriginFillResult fill = await ContentOriginFill.RunAsync(
            source, refusing, published.ClientVersion, published.Registry);

        Assert.False(fill.Filled);
        Assert.Equal(ContentOriginFill.RefusedOriginWrite, fill.RefusalReason);
        Assert.Equal(manifestHash, fill.RefusalHash);
        Assert.True(fill.ObjectsWritten > 0);
        Assert.Equal(published.ClientClosure().Count, fill.ObjectsRequired);
        Assert.False(await origin.ExistsAsync(manifestHash));
        Assert.DoesNotContain(manifestHash, await HashesAsync(origin));
    }

    // The versions/<n> pointer format carries the SERVER manifest hash, an origin is public, and a client
    // learns its version from the connect door rather than from a file. So the fill writes no pointer, and
    // the directory one would land in is not even created.
    [Fact]
    public async Task No_version_pointer_is_written_to_the_origin()
    {
        using var sourceRoot = new TemporaryRoot();
        using var originRoot = new TemporaryRoot();
        var source = new FileSystemPackStore(sourceRoot.Path);
        var origin = new FileSystemPackStore(originRoot.Path);
        PublishedVersion published = await PublishAsync(source);

        Assert.True((await ContentOriginFill.RunAsync(
            source, origin, published.ClientVersion, published.Registry)).Filled);

        string versions = Path.Combine(originRoot.Path, FileSystemPackStore.VersionDirectoryName);

        Assert.True(
            !Directory.Exists(versions) || !Directory.EnumerateFileSystemEntries(versions).Any(),
            "The origin holds a version pointer, which carries the SERVER manifest hash.");
        Assert.Null(await origin.GetVersionPointerAsync(published.ClientVersion.Number));

        // The source, which is the server's own store, DOES hold one. The absence above is the fill's doing
        // rather than a publish that wrote no pointer at all.
        Assert.NotNull(await source.GetVersionPointerAsync(published.ClientVersion.Number));
    }

    static ContentFetchOptions ClientOptions(int clientBuild = 10_000) => new()
    {
        ClientBuild = clientBuild,
        Attempts = 1,
        BackoffBase = TimeSpan.Zero,
    };

    static async Task<IReadOnlyList<string>> HashesAsync(FileSystemPackStore store)
    {
        var held = new List<string>();
        await foreach (string hash in store.EnumerateAsync())
        {
            held.Add(hash);
        }

        return held;
    }

    /// <summary>
    /// Publishes ONE version through the real publish pipeline into a file system store, which is the
    /// server's own pack store. It carries a client type and a ServerOnly type, because the whole point of
    /// the fill is which of the two reaches the origin.
    /// </summary>
    static async Task<PublishedVersion> PublishAsync(FileSystemPackStore pack, int? minimumClientBuild = null)
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        var tag = new ContentTypeId(EngineContentTypes.TagTypeId);
        var table = new ContentTypeId(EngineContentTypes.LootTableTypeId);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Import(tag, 1, new ContentKey("metal"), []),
            ContentEdit.Import(tag, 2, new ContentKey("two_handed"), []),
            ContentEdit.Import(
                table,
                1,
                new ContentKey("goblin"),
                [
                    new ContentFieldEdit(
                        LootTableContentType.RollCountField,
                        ContentFieldValue.OfNumber(ContentFieldKind.Int, 1)),
                ]));

        ContentPublishResult published = await store.PublishAsync(
            PublishFixtures.Request(0, minimumClientBuild: minimumClientBuild));

        ContentManifest client = await ManifestAsync(
            pack, published.ClientManifestHash, ContentManifestSide.Client, registry);
        ContentManifest server = await ManifestAsync(
            pack, published.ServerManifestHash, ContentManifestSide.Server, registry);

        return new PublishedVersion(registry, published, client, server);
    }

    static async Task<ContentManifest> ManifestAsync(
        IPackStore store,
        string hash,
        ContentManifestSide side,
        ContentTypeRegistry registry)
    {
        ContentManifestRead read = await ContentPackReader.ReadManifestAsync(store, hash, side, registry);
        Assert.True(read.Success, read.Reason ?? "no reason");
        Assert.NotNull(read.Manifest);
        return read.Manifest;
    }

    /// <summary>
    /// An origin whose WRITES fault, which is what an expired signature or a throttled container is. Reads go
    /// to the real store behind it, because a store answers absent for a read it could not do and the fill
    /// has to be able to see what the origin already holds.
    /// </summary>
    /// <param name="inner">The store every read, and every write that is not refused, goes to.</param>
    /// <param name="refuses">Which hashes the write faults for.</param>
    sealed class RefusingWriteStore(IPackStore inner, Func<string, bool> refuses) : IPackStore
    {
        /// <summary>What the fault carries, so the detail can be pinned without naming an exception type.</summary>
        public const string Message = "the origin refused the write";

        readonly ConcurrentQueue<string> _attempted = new();

        /// <summary>Every hash a write was attempted for, in call order.</summary>
        public IReadOnlyCollection<string> Attempted => _attempted;

        /// <inheritdoc />
        public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
            => inner.ExistsAsync(hash, cancellationToken);

        /// <inheritdoc />
        public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
            => inner.GetAsync(hash, cancellationToken);

        /// <inheritdoc />
        public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            _attempted.Enqueue(hash);
            return refuses(hash)
                ? throw new IOException(Message)
                : inner.PutAsync(hash, bytes, cancellationToken);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
            => inner.ListAsync(versionNumber, cancellationToken);
    }

    /// <summary>One published version as the fill sees it: the registry, the result and both manifests.</summary>
    sealed record PublishedVersion(
        ContentTypeRegistry Registry,
        ContentPublishResult Published,
        ContentManifest ClientManifest,
        ContentManifest ServerManifest)
    {
        /// <summary>The number and the CLIENT manifest hash, which is the only identity a fill accepts.</summary>
        public ContentVersionIdentity ClientVersion => Published.ClientIdentity;

        /// <summary>The client manifest's own address and every hash it names, which is what an origin holds.</summary>
        public IReadOnlyList<string> ClientClosure()
        {
            var closure = new List<string> { Published.ClientManifestHash };
            closure.AddRange(ContentFetchLoop.RequiredHashes(ClientManifest));
            return closure;
        }

        /// <summary>Every chunk the SERVER manifest names that the client manifest does not, which no client may hold.</summary>
        public IReadOnlyList<string> ServerOnlyChunkHashes()
        {
            IReadOnlyList<string> client = ContentFetchLoop.RequiredHashes(ClientManifest);
            return ContentFetchLoop.RequiredHashes(ServerManifest)
                .Where(hash => !client.Contains(hash, StringComparer.Ordinal))
                .ToArray();
        }
    }
}
