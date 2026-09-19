using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// Rebuilding a PUBLISHED version's whole pack out of the authoring store, which is the recovery a server
/// whose pack root does not outlive its process needs: the store keeps rows, rules and hashes and never the
/// bytes, so a boot against an empty pack store refuses a version the store still names as active.
/// <para>
/// The property every test here is a face of is one sentence. A rebuild reproduces the bytes the publisher
/// wrote, and it proves it did by digesting both manifests and comparing them against the two the version
/// record holds, BEFORE it writes anything a reader could follow. A manifest digest covers every chunk hash
/// inline, so one comparison pins the whole closure.
/// </para>
/// </summary>
public sealed class PackRebuildTests
{
    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    [Fact]
    public async Task ARebuildIntoAnEmptyStoreReproducesEveryChunkAndBothManifestsByteForByte()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, packA);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)),
            ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(2)),
            ContentEdit.Import(Thing, PublishFixtures.SecondChunkId, new ContentKey("far"), PublishFixtures.Fields(3)));
        await store.PublishAsync(PublishFixtures.Request(0));

        ContentVersionRecord record = await RecordAsync(store, 1);
        var packB = new FileSystemPackStore(rootB.Path);

        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(store, registry, 1, packB);

        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail ?? rebuilt.RefusalReason);
        Assert.Null(rebuilt.RefusalReason);
        Assert.Null(rebuilt.RefusalDetail);
        Assert.Equal(1, rebuilt.VersionNumber);
        Assert.Equal(2, rebuilt.ChunksBuilt);
        Assert.True(rebuilt.ObjectsWritten > 0);
        Assert.True(rebuilt.BytesWritten > 0);

        // Every object version 1's closure names in the published store is fetchable out of the rebuilt one.
        IReadOnlyList<string> closure = await ClosureAsync(packA, 1);
        Assert.NotEmpty(closure);
        foreach (string hash in closure)
        {
            Assert.True(await packB.ExistsAsync(hash), hash);
        }

        // The two manifests are the same FILE, not merely the same digest.
        await AssertSameFileAsync(packA, packB, record.ServerManifestHash);
        await AssertSameFileAsync(packA, packB, record.ClientManifestHash);

        // A chunk's stored bytes are free to differ, because the hash is over the UNCOMPRESSED canonical
        // bytes and the header carries no version number, so the canonical form is what is compared.
        ContentPublishBaseline baseline = await store.ReadPublishBaselineAsync();
        Assert.NotEmpty(baseline.Chunks);
        foreach (ContentChunkRecord chunk in baseline.Chunks)
        {
            await AssertSameCanonicalAsync(registry, packA, packB, chunk.Hash);
        }

        PackVersionPointer? pointer = await packB.GetVersionPointerAsync(1);
        Assert.NotNull(pointer);
        Assert.Equal(record.ServerManifestHash, pointer.ServerManifestHash);
        Assert.Equal(record.ClientManifestHash, pointer.ClientManifestHash);
    }

    [Fact]
    public async Task ARebuildOfAVersionThatReusedChunksFromItsPredecessorReproducesThoseChunksAtTheirOriginalHashes()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, packA);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)),
            ContentEdit.Import(Thing, PublishFixtures.SecondChunkId, new ContentKey("far"), PublishFixtures.Fields(2)));
        await store.PublishAsync(PublishFixtures.Request(0));

        ContentPublishBaseline first = await store.ReadPublishBaselineAsync();
        string untouched = ChunkHash(first, 1);

        // Version 2 edits a row in chunk 0 only, so chunk 1 is carried forward at the hash version 1 encoded.
        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(99)));
        await store.PublishAsync(PublishFixtures.Request(1));

        ContentPublishBaseline second = await store.ReadPublishBaselineAsync();
        Assert.Equal(untouched, ChunkHash(second, 1));

        var packB = new FileSystemPackStore(rootB.Path);
        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(store, registry, 2, packB);

        // The rebuild has no baseline to carry anything forward from, so it ENCODES the reused chunk again
        // and lands on the same address. That is the claim chunk identity being an id range buys.
        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail ?? rebuilt.RefusalReason);
        Assert.Equal(2, rebuilt.ChunksBuilt);
        Assert.True(await packB.ExistsAsync(untouched), untouched);
        await AssertSameCanonicalAsync(registry, packA, packB, untouched);
    }

    [Fact]
    public async Task ASecondRebuildWritesNoObjectsBecauseTheStoreAlreadyHoldsEveryHash()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, packA);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));
        await store.PublishAsync(PublishFixtures.Request(0));

        var packB = new FileSystemPackStore(rootB.Path);
        ContentPackRebuildResult first = await ContentPackRebuild.RunAsync(store, registry, 1, packB);
        ContentPackRebuildResult second = await ContentPackRebuild.RunAsync(store, registry, 1, packB);

        Assert.True(first.ObjectsWritten > 0);
        Assert.True(second.Rebuilt, second.RefusalDetail ?? second.RefusalReason);
        Assert.Equal(0, second.ObjectsWritten);
        Assert.Equal(0L, second.BytesWritten);
        Assert.Equal(first.ChunksBuilt, second.ChunksBuilt);
    }

    [Fact]
    public async Task ARebuiltVersionWhoseRowsNoLongerDigestToTheRecordedManifestIsRefusedAndTheTargetGetsNoPointer()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry published = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(published, packA);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));
        await store.PublishAsync(PublishFixtures.Request(0));
        ContentVersionRecord record = await RecordAsync(store, 1);

        var packB = new FileSystemPackStore(rootB.Path);
        ContentPackRebuildResult refused = await ContentPackRebuild.RunAsync(store, Drifted(), 1, packB);

        Assert.False(refused.Rebuilt);
        Assert.Equal(ContentPackRebuild.RefusedServerManifest, refused.RefusalReason);
        Assert.NotNull(refused.RefusalDetail);
        Assert.Contains(record.ServerManifestHash, refused.RefusalDetail, StringComparison.Ordinal);
        Assert.Equal(2, ContentAddressCount(refused.RefusalDetail));
        Assert.Equal(0, refused.ObjectsWritten);
        Assert.Equal(0L, refused.BytesWritten);

        // NOTHING a reader could follow: no pointer, no manifest, and no object at all.
        Assert.Null(await packB.GetVersionPointerAsync(1));
        Assert.False(await packB.ExistsAsync(record.ServerManifestHash));
        Assert.False(await packB.ExistsAsync(record.ClientManifestHash));
        Assert.Empty(await EverythingAsync(packB));
    }

    [Fact]
    public async Task ARebuildOfAVersionTheStoreDoesNotHoldThrowsUnknownVersion()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, packA);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));
        await store.PublishAsync(PublishFixtures.Request(0));

        var packB = new FileSystemPackStore(rootB.Path);
        ContentAuthoringException unknown = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => ContentPackRebuild.RunAsync(store, registry, 7, packB));

        Assert.Equal(ContentAuthoringException.UnknownVersionReason, unknown.Reason);
        Assert.Empty(await EverythingAsync(packB));
    }

    [Fact]
    public async Task AServerOnlyChunkIsInTheRebuiltServerPackAndIsNamedByNeitherTheClientManifestNorItsClosure()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, packA);

        var tag = new ContentTypeId(EngineContentTypes.TagTypeId);
        var table = new ContentTypeId(EngineContentTypes.LootTableTypeId);
        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Import(tag, 1, new ContentKey("metal"), []),
            ContentEdit.Import(
                table,
                1,
                new ContentKey("goblin"),
                [
                    new ContentFieldEdit(
                        LootTableContentType.RollCountField,
                        ContentFieldValue.OfNumber(ContentFieldKind.Int, 1)),
                ]));
        await store.PublishAsync(PublishFixtures.Request(0));

        ContentVersionRecord record = await RecordAsync(store, 1);
        ContentPublishBaseline baseline = await store.ReadPublishBaselineAsync();
        ContentChunkRecord secret = Assert.Single(
            baseline.Chunks,
            chunk => chunk.Type == table && chunk.Side == ContentVisibility.ServerOnly);

        var packB = new FileSystemPackStore(rootB.Path);
        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(store, registry, 1, packB);

        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail ?? rebuilt.RefusalReason);
        Assert.True(await packB.ExistsAsync(secret.Hash), secret.Hash);
        await AssertSameCanonicalAsync(registry, packA, packB, secret.Hash);

        ContentManifest client = await ManifestAsync(packB, record.ClientManifestHash, ContentManifestSide.Client);
        Assert.DoesNotContain(client.Types, entry => entry.TypeId == EngineContentTypes.LootTableTypeId);
        foreach (ManifestTypeEntry entry in client.Types)
        {
            Assert.DoesNotContain(
                entry.Chunks,
                chunk => string.Equals(chunk.Hash, secret.Hash, StringComparison.Ordinal));
        }

        ContentManifest server = await ManifestAsync(packB, record.ServerManifestHash, ContentManifestSide.Server);
        ManifestTypeEntry tables = Assert.Single(
            server.Types,
            entry => entry.TypeId == EngineContentTypes.LootTableTypeId);
        Assert.Contains(tables.Chunks, chunk => string.Equals(chunk.Hash, secret.Hash, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARebuildRefusesWhenTheRegistrysFieldSchemaHasChangedSincePublish()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry published = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(published, packA);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)),
            ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(2)));
        await store.PublishAsync(PublishFixtures.Request(0));

        // The same rows through a registry that now marks one field ServerOnly. The chunk set, the chunk
        // bytes and therefore both digests all move, and the rebuild refuses rather than filing a pack that
        // no version record describes.
        var packB = new FileSystemPackStore(rootB.Path);
        ContentPackRebuildResult refused = await ContentPackRebuild.RunAsync(store, Drifted(), 1, packB);

        Assert.False(refused.Rebuilt);
        Assert.Equal(ContentPackRebuild.RefusedServerManifest, refused.RefusalReason);
        Assert.Empty(await EverythingAsync(packB));
    }

    [Fact]
    public async Task ARebuildOfAnOlderVersionThanTheActiveOneReproducesThatVersionsManifests()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, packA);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)),
            ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(2)),
            ContentEdit.Add(Thing, new ContentKey("three"), PublishFixtures.Fields(3)));
        await store.PublishAsync(PublishFixtures.Request(0));

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Retire(Thing, 2, new ContentKey("two"), ContentRetirePolicy.Placeholder, 0));
        await store.PublishAsync(PublishFixtures.Request(1));
        IReadOnlyList<RemapRule> throughTwo = [.. store.Rules];

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Retire(Thing, 3, new ContentKey("three"), ContentRetirePolicy.Placeholder, 0));
        await store.PublishAsync(PublishFixtures.Request(2));
        IReadOnlyList<RemapRule> throughThree = [.. store.Rules];

        Assert.Single(throughTwo);
        Assert.Equal(2, throughThree.Count);
        Assert.Equal(3, await store.GetActiveVersionAsync());

        var packB = new FileSystemPackStore(rootB.Path);
        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(store, registry, 2, packB);

        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail ?? rebuilt.RefusalReason);
        Assert.Equal(2, rebuilt.VersionNumber);

        // The rule chunk is version 2's, so the rule version 3 appended is not in it.
        Assert.True(await packB.ExistsAsync(ContentRuleChunkCodec.Hash(throughTwo)));
        Assert.False(await packB.ExistsAsync(ContentRuleChunkCodec.Hash(throughThree)));

        ContentVersionRecord second = await RecordAsync(store, 2);
        ContentVersionRecord third = await RecordAsync(store, 3);
        Assert.True(await packB.ExistsAsync(second.ServerManifestHash));
        Assert.True(await packB.ExistsAsync(second.ClientManifestHash));
        Assert.False(await packB.ExistsAsync(third.ServerManifestHash));
        await AssertSameFileAsync(packA, packB, second.ServerManifestHash);
        await AssertSameFileAsync(packA, packB, second.ClientManifestHash);
    }

    /// <summary>
    /// A rebuild calls no publishing or editing member, which is what the audit, the active version and the
    /// draft below are asserted against. It is not strictly write free: the publish baseline read clears a
    /// STALE freeze marker left by a publish that died, which is the recovery that read always performs and
    /// which no store in a healthy state is in a position to need.
    /// </summary>
    [Fact]
    public async Task ARebuildNeverWritesToTheAuthoringStore()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, packA);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));
        await store.PublishAsync(PublishFixtures.Request(0));

        // A draft is open and unfrozen while the rebuild runs, which is the state an operator recovering a
        // pack root is most likely to be in.
        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(2)));

        int auditBefore = (await store.ListAuditAsync(default, 0, 0, 500)).Count;
        int activeBefore = await store.GetActiveVersionAsync();
        ContentDraft? draftBefore = await store.GetOpenDraftAsync();
        Assert.NotNull(draftBefore);

        var packB = new FileSystemPackStore(rootB.Path);
        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(store, registry, 1, packB);

        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail ?? rebuilt.RefusalReason);
        Assert.Equal(auditBefore, (await store.ListAuditAsync(default, 0, 0, 500)).Count);
        Assert.Equal(activeBefore, await store.GetActiveVersionAsync());

        ContentDraft? draftAfter = await store.GetOpenDraftAsync();
        Assert.NotNull(draftAfter);
        Assert.Equal(draftBefore.EditCount, draftAfter.EditCount);
        Assert.False(draftAfter.IsFrozen);
        Assert.Single(await store.ListVersionsAsync());
    }

    /// <summary>
    /// The fixture type through a registry whose schema has DRIFTED since the publish: the optional field is
    /// marked <c>ServerOnly</c> now, which gives the type two sides where it had one and moves the bytes of
    /// both.
    /// </summary>
    static ContentTypeRegistry Drifted()
    {
        var schema = new ContentFieldSchema(
        [
            new ContentFieldEntry(PublishFixtures.ValueField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new ContentFieldEntry(PublishFixtures.LegacyField, ContentFieldKind.Bool, null, ContentVisibility.ServerOnly, false),
        ]);

        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            PublishFixtures.ThingTypeId,
            PublishFixtures.ThingTypeKey,
            new PublishCodec(new ContentTypeId(PublishFixtures.ThingTypeId), schema),
            null,
            schema,
            ContentVisibility.Client,
            PublishFixtures.ChunkSlots);
        return registry;
    }

    static async Task<ContentVersionRecord> RecordAsync(IContentAuthoringStore store, int versionNumber)
    {
        ContentVersionRecord? record = await store.GetVersionAsync(versionNumber);
        Assert.NotNull(record);
        return record;
    }

    static string ChunkHash(ContentPublishBaseline baseline, int chunkIndex)
        => Assert.Single(baseline.Chunks, chunk => chunk.ChunkIndex == chunkIndex).Hash;

    static async Task<IReadOnlyList<string>> ClosureAsync(FileSystemPackStore store, int versionNumber)
    {
        var hashes = new List<string>();
        await foreach (string hash in store.ListAsync(versionNumber))
        {
            hashes.Add(hash);
        }

        return hashes;
    }

    static async Task<IReadOnlyList<string>> EverythingAsync(FileSystemPackStore store)
    {
        var hashes = new List<string>();
        await foreach (string hash in store.EnumerateAsync())
        {
            hashes.Add(hash);
        }

        return hashes;
    }

    static async Task<ContentManifest> ManifestAsync(
        FileSystemPackStore store,
        string hash,
        ContentManifestSide side)
    {
        ReadOnlyMemory<byte>? file = await store.GetAsync(hash);
        Assert.NotNull(file);
        Assert.True(
            ContentManifestCodec.TryDecode(file.Value.Span, side, out ContentManifest? manifest, out string? reason),
            reason ?? "no reason");
        return manifest;
    }

    static async Task AssertSameFileAsync(FileSystemPackStore left, FileSystemPackStore right, string hash)
    {
        ReadOnlyMemory<byte>? a = await left.GetAsync(hash);
        ReadOnlyMemory<byte>? b = await right.GetAsync(hash);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(a.Value.Span.SequenceEqual(b.Value.Span), hash);
    }

    static async Task AssertSameCanonicalAsync(
        ContentTypeRegistry registry,
        FileSystemPackStore left,
        FileSystemPackStore right,
        string hash)
    {
        ContentChunk a = await ChunkAsync(registry, left, hash);
        ContentChunk b = await ChunkAsync(registry, right, hash);

        Assert.Equal(a.Type, b.Type);
        Assert.Equal(a.ChunkIndex, b.ChunkIndex);
        Assert.Equal(a.Visibility, b.Visibility);
        Assert.Equal(a.SlotBase, b.SlotBase);
        Assert.Equal(a.SlotCount, b.SlotCount);
        Assert.Equal(a.RowCount, b.RowCount);
        Assert.True(a.Body.SequenceEqual(b.Body), hash);
    }

    static async Task<ContentChunk> ChunkAsync(ContentTypeRegistry registry, FileSystemPackStore store, string hash)
    {
        ReadOnlyMemory<byte>? file = await store.GetAsync(hash);
        Assert.NotNull(file);
        Assert.True(
            ContentChunkCodec.TryDecode(file.Value.Span, registry, out ContentChunk? chunk, out string? reason),
            reason ?? "no reason");
        return chunk;
    }

    /// <summary>How many content addresses a refusal detail names, which is two when it names both digests.</summary>
    static int ContentAddressCount(string detail)
    {
        int count = 0;
        foreach (string token in detail.Split(
            [' ', '\n', '\r', '\t', '.', ',', '\''],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (FileSystemPackStore.IsContentAddress(token))
            {
                count++;
            }
        }

        return count;
    }
}
