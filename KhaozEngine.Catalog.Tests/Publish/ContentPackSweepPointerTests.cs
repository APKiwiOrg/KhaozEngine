using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>A sweep must prove that its mutable pointers still describe the durable version records.</summary>
public sealed class ContentPackSweepPointerTests
{
    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task APointerNamingAnotherCatalogAtTheSameVersionSkipsBeforeEnumeration(
        bool replaceServer,
        bool replaceClient)
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        var tracked = new TrackedPointerPackStore(pack);
        InMemoryContentAuthoringStore first = PublishFixtures.Store(registry, tracked);
        ContentPublishResult a = await PublishAsync(first, value: 11);
        var live = new List<string>();
        await foreach (string hash in pack.ListAsync(1))
        {
            live.Add(hash);
        }

        // The second catalog writes into the same root without pruning the first catalog's objects.
        InMemoryContentAuthoringStore second = PublishFixtures.Store(registry, new PrunelessPackStore(pack));
        ContentPublishResult b = await PublishAsync(second, value: 99);
        Assert.Equal(1, a.VersionNumber);
        Assert.Equal(1, b.VersionNumber);
        Assert.NotEqual(a.ServerManifestHash, b.ServerManifestHash);
        Assert.NotEqual(a.ClientManifestHash, b.ClientManifestHash);
        await pack.PutVersionPointerAsync(
            1,
            replaceServer ? b.ServerManifestHash : a.ServerManifestHash,
            replaceClient ? b.ClientManifestHash : a.ClientManifestHash);
        tracked.Reset();

        ContentPackSweepResult sweep = await PublishFixtures.Commit(first, tracked, registry).SweepAsync();

        Assert.False(sweep.Ran);
        Assert.Equal(ContentPackSweep.SkippedListingFailed, sweep.SkipReason);
        Assert.Equal(0, sweep.Deleted);
        Assert.Equal(0, tracked.ListCalls);
        Assert.Equal(0, tracked.EnumerationCalls);
        Assert.Equal(0, tracked.DeleteCalls);
        Assert.NotEmpty(live);
        foreach (string hash in live)
        {
            Assert.True(await pack.ExistsAsync(hash), hash);
        }
    }

    [Fact]
    public async Task APointerReplacedAfterValidationCannotChangeTheDurableKeepSet()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        var tracked = new TrackedPointerPackStore(pack);
        InMemoryContentAuthoringStore first = PublishFixtures.Store(registry, tracked);
        ContentPublishResult a = await PublishAsync(first, value: 11);
        var live = new HashSet<string>(StringComparer.Ordinal);
        await foreach (string hash in pack.ListAsync(1))
        {
            live.Add(hash);
        }

        ContentPublishResult b = await PublishAsync(
            PublishFixtures.Store(registry, new PrunelessPackStore(pack)), value: 99);
        var replacementOnly = new HashSet<string>(StringComparer.Ordinal);
        await foreach (string hash in pack.ListAsync(1))
        {
            if (!live.Contains(hash))
            {
                replacementOnly.Add(hash);
            }
        }

        await pack.PutVersionPointerAsync(1, a.ServerManifestHash, a.ClientManifestHash);
        tracked.Reset();
        tracked.AfterPointerRead = () => pack.PutVersionPointerAsync(1, b.ServerManifestHash, b.ClientManifestHash);

        ContentPackSweepResult sweep = await PublishFixtures.Commit(first, tracked, registry).SweepAsync();

        Assert.NotEmpty(live);
        foreach (string hash in live)
        {
            Assert.True(await pack.ExistsAsync(hash), hash);
        }

        Assert.True(sweep.Ran, sweep.SkipReason);
        Assert.Equal(live.Count, sweep.Kept);
        Assert.Equal(replacementOnly.Count, sweep.Deleted);
        Assert.Equal(0, tracked.ListCalls);
        PackVersionPointer replaced = Assert.IsType<PackVersionPointer>(await pack.GetVersionPointerAsync(1));
        Assert.Equal(b.ServerManifestHash, replaced.ServerManifestHash);
        Assert.Equal(b.ClientManifestHash, replaced.ClientManifestHash);
        Assert.NotEmpty(replacementOnly);
        foreach (string hash in replacementOnly)
        {
            Assert.False(await pack.ExistsAsync(hash), hash);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMissingOrPartialPointerSkipsBeforeEnumeration(bool partial)
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        var tracked = new TrackedPointerPackStore(pack);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, tracked);
        ContentPublishResult published = await PublishAsync(store, value: 11);
        if (partial)
        {
            await File.WriteAllTextAsync(pack.VersionPointerPathFor(1), published.ServerManifestHash + "\n");
        }
        else
        {
            File.Delete(pack.VersionPointerPathFor(1));
        }

        tracked.Reset();
        ContentPackSweepResult sweep = await PublishFixtures.Commit(store, tracked, registry).SweepAsync();

        Assert.False(sweep.Ran);
        Assert.Equal(ContentPackSweep.SkippedListingFailed, sweep.SkipReason);
        Assert.Equal(0, sweep.Deleted);
        Assert.Equal(0, tracked.ListCalls);
        Assert.Equal(0, tracked.EnumerationCalls);
        Assert.Equal(0, tracked.DeleteCalls);
        Assert.True(await pack.ExistsAsync(published.ServerManifestHash));
        Assert.True(await pack.ExistsAsync(published.ClientManifestHash));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMissingDurableManifestSkipsWithoutPruning(bool server)
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        var tracked = new TrackedPointerPackStore(pack);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, tracked);
        ContentPublishResult published = await PublishAsync(store, value: 11);
        ContentPublishBaseline baseline = await store.ReadPublishBaselineAsync();
        File.Delete(pack.PathFor(server ? published.ServerManifestHash : published.ClientManifestHash));
        tracked.Reset();

        ContentPackSweepResult sweep = await PublishFixtures.Commit(store, tracked, registry).SweepAsync();

        Assert.False(sweep.Ran);
        Assert.Equal(ContentPackSweep.SkippedListingFailed, sweep.SkipReason);
        Assert.Equal(0, sweep.Deleted);
        Assert.Equal(0, tracked.ListCalls);
        Assert.Equal(0, tracked.EnumerationCalls);
        Assert.Equal(0, tracked.DeleteCalls);
        Assert.NotEmpty(baseline.Chunks);
        foreach (ContentChunkRecord chunk in baseline.Chunks)
        {
            Assert.True(await pack.ExistsAsync(chunk.Hash), chunk.Hash);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AValidManifestServedUnderAnotherHashHasNoClosureAndSkipsPruning(bool server)
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        var tracked = new TrackedPointerPackStore(pack);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, tracked);
        ContentPublishResult a = await PublishAsync(store, value: 11);
        ContentPublishBaseline baseline = await store.ReadPublishBaselineAsync();
        ContentPublishResult b = await PublishAsync(
            PublishFixtures.Store(registry, new PrunelessPackStore(pack)), value: 99);
        string expected = server ? a.ServerManifestHash : a.ClientManifestHash;
        string replacement = server ? b.ServerManifestHash : b.ClientManifestHash;
        var bytes = await pack.GetAsync(replacement);
        Assert.NotNull(bytes);
        Assert.True(ContentPackReader.TryVerify(bytes.Value.Span, replacement, out _));
        Assert.False(ContentPackReader.TryVerify(bytes.Value.Span, expected, out _));

        // GetAsync returns raw fetched bytes, including a valid manifest filed under the wrong address.
        await File.WriteAllBytesAsync(pack.PathFor(expected), bytes.Value.ToArray());
        await pack.PutVersionPointerAsync(1, a.ServerManifestHash, a.ClientManifestHash);
        tracked.Reset();

        Assert.Empty(await ContentPackClosure.ReadAsync(tracked, a.ServerManifestHash, a.ClientManifestHash));
        ContentPackSweepResult sweep = await PublishFixtures.Commit(store, tracked, registry).SweepAsync();

        Assert.False(sweep.Ran);
        Assert.Equal(ContentPackSweep.SkippedListingFailed, sweep.SkipReason);
        Assert.Equal(0, sweep.Deleted);
        Assert.Equal(0, tracked.ListCalls);
        Assert.Equal(0, tracked.EnumerationCalls);
        Assert.Equal(0, tracked.DeleteCalls);
        foreach (ContentChunkRecord chunk in baseline.Chunks)
        {
            Assert.True(await pack.ExistsAsync(chunk.Hash), chunk.Hash);
        }
    }

    [Fact]
    public async Task AStaleOlderVersionPointerSkipsTheNewPublishSweep()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        var tracked = new TrackedPointerPackStore(pack);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, tracked);
        await PublishAsync(store, value: 11);
        var live = new List<string>();
        await foreach (string hash in pack.ListAsync(1))
        {
            live.Add(hash);
        }

        await PublishAsync(PublishFixtures.Store(registry, new PrunelessPackStore(pack)), value: 99);
        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(22)));
        tracked.Reset();
        ContentPublishCommit commit = PublishFixtures.Commit(store, tracked, registry);

        ContentPublishResult published = await commit.PublishAsync(PublishFixtures.Request(1));

        Assert.Equal(2, published.VersionNumber);
        ContentPackSweepResult sweep = Assert.IsType<ContentPackSweepResult>(commit.LastSweep);
        Assert.False(sweep.Ran);
        Assert.Equal(ContentPackSweep.SkippedListingFailed, sweep.SkipReason);
        Assert.Equal(0, sweep.Deleted);
        Assert.Equal(0, tracked.ListCalls);
        Assert.Equal(0, tracked.EnumerationCalls);
        Assert.Equal(0, tracked.DeleteCalls);
        Assert.True(await pack.ExistsAsync(published.ServerManifestHash));
        Assert.True(await pack.ExistsAsync(published.ClientManifestHash));
        await foreach (string hash in pack.ListAsync(2))
        {
            live.Add(hash);
        }

        foreach (string hash in live)
        {
            Assert.True(await pack.ExistsAsync(hash), hash);
        }
    }

    [Fact]
    public async Task AStoreWithoutReadablePointerEvidenceSkipsBeforeEnumeration()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);
        ContentPublishResult published = await PublishAsync(store, value: 11);
        var tracked = new TrackedPackStore(pack);

        ContentPackSweepResult sweep = await ContentPackSweep.RunValidatedAsync(tracked, await store.ListVersionsAsync());

        Assert.False(sweep.Ran);
        Assert.Equal(ContentPackSweep.SkippedListingFailed, sweep.SkipReason);
        Assert.Equal(0, sweep.Deleted);
        Assert.Equal(0, tracked.ListCalls);
        Assert.Equal(0, tracked.EnumerationCalls);
        Assert.Equal(0, tracked.DeleteCalls);
        Assert.True(await pack.ExistsAsync(published.ServerManifestHash));
        Assert.True(await pack.ExistsAsync(published.ClientManifestHash));
    }

    [Fact]
    public async Task AMatchingReadOnlyPointerSourceKeepsTheLiveClosureAndPrunesAnOrphan()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);
        ContentPublishResult published = await PublishAsync(store, value: 11);
        RemapRule[] rules =
        [
            new(1, 1, Thing, RemapRuleKind.Retired, 99, 0, [RemapRule.RetirePolicyPlaceholder]),
        ];
        string orphan = ContentRuleChunkCodec.Hash(rules);
        await pack.PutAsync(orphan, ContentRuleChunkCodec.Encode(rules));
        var tracked = new TrackedReadPointerPackStore(pack);

        ContentPackSweepResult sweep = await ContentPackSweep.RunValidatedAsync(tracked, await store.ListVersionsAsync());

        Assert.True(sweep.Ran, sweep.SkipReason);
        Assert.Equal(1, sweep.Deleted);
        Assert.False(await pack.ExistsAsync(orphan));
        Assert.True(await pack.ExistsAsync(published.ServerManifestHash));
        Assert.True(await pack.ExistsAsync(published.ClientManifestHash));
        await foreach (string hash in pack.ListAsync(1))
        {
            Assert.True(await pack.ExistsAsync(hash), hash);
        }
    }

    [Fact]
    public async Task ANumericEmptyCollectionCallSweepsOrphansWithoutPointerEvidence()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        RemapRule[] rules =
        [
            new(1, 1, Thing, RemapRuleKind.Retired, 99, 0, [RemapRule.RetirePolicyPlaceholder]),
        ];
        string orphan = ContentRuleChunkCodec.Hash(rules);
        await pack.PutAsync(orphan, ContentRuleChunkCodec.Encode(rules));

        ContentPackSweepResult sweep = await ContentPackSweep.RunAsync(pack, []);

        Assert.True(sweep.Ran, sweep.SkipReason);
        Assert.Equal(0, sweep.Kept);
        Assert.Equal(1, sweep.Deleted);
        Assert.Null(sweep.SkipReason);
        Assert.False(await pack.ExistsAsync(orphan));
    }

    [Fact]
    public async Task ANumericNullCallRetainsItsArgumentValidation()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);

        ArgumentNullException refusal = await Assert.ThrowsAsync<ArgumentNullException>(
            () => ContentPackSweep.RunAsync(pack, null!));

        Assert.Equal("versions", refusal.ParamName);
    }

    static async Task<ContentPublishResult> PublishAsync(InMemoryContentAuthoringStore store, int value)
    {
        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(value)));
        return await store.PublishAsync(PublishFixtures.Request(0));
    }

    /// <summary>Records enumeration and deletion calls while forwarding every operation to the real pack.</summary>
    class TrackedPackStore(FileSystemPackStore inner) : IPackStore, IPackStorePruning
    {
        protected FileSystemPackStore Inner { get; } = inner;

        public int ListCalls { get; private set; }
        public int EnumerationCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public void Reset()
        {
            ListCalls = 0;
            EnumerationCalls = 0;
            DeleteCalls = 0;
        }

        public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
            => Inner.ExistsAsync(hash, cancellationToken);

        public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
            => Inner.GetAsync(hash, cancellationToken);

        public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
            => Inner.PutAsync(hash, bytes, cancellationToken);

        public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Inner.ListAsync(versionNumber, cancellationToken);
        }

        public IAsyncEnumerable<string> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            EnumerationCalls++;
            return Inner.EnumerateAsync(cancellationToken);
        }

        public Task<bool> DeleteAsync(string hash, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Inner.DeleteAsync(hash, cancellationToken);
        }
    }

    sealed class TrackedPointerPackStore(FileSystemPackStore inner)
        : TrackedPackStore(inner), IPackVersionPointerStore
    {
        public Func<Task>? AfterPointerRead { get; set; }

        public Task PutVersionPointerAsync(
            int versionNumber,
            string serverManifestHash,
            string clientManifestHash,
            CancellationToken cancellationToken = default)
            => Inner.PutVersionPointerAsync(versionNumber, serverManifestHash, clientManifestHash, cancellationToken);

        public async Task<PackVersionPointer?> GetVersionPointerAsync(
            int versionNumber,
            CancellationToken cancellationToken = default)
        {
            PackVersionPointer? pointer = await Inner.GetVersionPointerAsync(versionNumber, cancellationToken);
            if (AfterPointerRead is not null)
            {
                await AfterPointerRead();
            }

            return pointer;
        }
    }

    sealed class TrackedReadPointerPackStore(FileSystemPackStore inner)
        : TrackedPackStore(inner), IContentVersionPointerSource
    {
        public Task<PackVersionPointer?> GetVersionPointerAsync(
            int versionNumber,
            CancellationToken cancellationToken = default)
            => Inner.GetVersionPointerAsync(versionNumber, cancellationToken);
    }
}
