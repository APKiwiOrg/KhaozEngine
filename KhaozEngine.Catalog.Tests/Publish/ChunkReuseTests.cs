using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// Step 6 of spec 6.6: which chunks a publish touches, and what every chunk it does NOT touch costs.
/// <para>
/// This is the mechanism behind the owner's download-size-after-a-one-item-edit budget, and it is the reason
/// a chunk's identity is an id RANGE rather than a row range: adding a definition rewrites one chunk and
/// leaves every other chunk hash untouched, where a row range would renumber every chunk after it.
/// </para>
/// </summary>
public sealed class ChunkReuseTests
{
    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    [Fact]
    public async Task EditingOneRowRewritesExactlyOneChunkAndReusesTheOther()
    {
        (InMemoryContentAuthoringStore store, ContentTypeRegistry registry, ContentPublishPlan seeded) =
            await SeedTwoChunksAsync();

        ContentPublishPlan edited = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.After(seeded),
            ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(11))));

        Assert.Equal(1, edited.ChunksWritten);
        Assert.Equal(1, edited.ChunksReused);
        Assert.NotEqual(HashOf(seeded, 0), HashOf(edited, 0));
        Assert.Equal(HashOf(seeded, 1), HashOf(edited, 1));
        Assert.True(PublishFixtures.Chunk(edited, PublishFixtures.ThingTypeId, 1, ContentVisibility.Client).IsReused);
    }

    [Fact]
    public async Task EditingRowsInTwoChunksRewritesBoth()
    {
        (InMemoryContentAuthoringStore store, ContentTypeRegistry registry, ContentPublishPlan seeded) =
            await SeedTwoChunksAsync();

        ContentPublishPlan edited = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.After(seeded),
            ContentEdit.Update(Thing, 2, new ContentKey("two"), PublishFixtures.Fields(22)),
            ContentEdit.Update(
                Thing,
                PublishFixtures.SecondChunkId,
                new ContentKey("three_hundred"),
                PublishFixtures.Fields(333))));

        Assert.Equal(2, edited.ChunksWritten);
        Assert.Equal(0, edited.ChunksReused);
        Assert.NotEqual(HashOf(seeded, 0), HashOf(edited, 0));
        Assert.NotEqual(HashOf(seeded, 1), HashOf(edited, 1));
    }

    [Fact]
    public async Task AnUpdateTouchesItsChunkOnceBecauseTheAffectedSetCollapses()
    {
        (InMemoryContentAuthoringStore store, ContentTypeRegistry registry, ContentPublishPlan seeded) =
            await SeedTwoChunksAsync();

        ContentPublishPlan edited = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.After(seeded),
            ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(11)),
            ContentEdit.Update(Thing, 2, new ContentKey("two"), PublishFixtures.Fields(22))));

        // An update CLOSES a row and INSERTS its successor, both in chunk 0, and the set holds one entry.
        Assert.Equal(1, edited.ChunksWritten);
        Assert.Equal(2, edited.Closes.Count);
        Assert.Equal(2, edited.Inserts.Count);
    }

    [Fact]
    public async Task ARetireIsAnOrdinaryChunkRewriteAndTheRowKeepsItsSlot()
    {
        (InMemoryContentAuthoringStore store, ContentTypeRegistry registry, ContentPublishPlan seeded) =
            await SeedTwoChunksAsync();

        ContentPublishPlan retired = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.After(seeded),
            ContentEdit.Retire(Thing, 2, new ContentKey("two"), ContentRetirePolicy.Placeholder, 0)));

        Assert.Equal(1, retired.ChunksWritten);
        Assert.Equal(HashOf(seeded, 1), HashOf(retired, 1));

        ContentChunkRecord chunk = PublishFixtures.Chunk(retired, PublishFixtures.ThingTypeId, 0, ContentVisibility.Client);
        Assert.Equal(2, chunk.RowCount);
        ContentRow row = PublishFixtures.DecodeRow(registry, chunk, 2);
        Assert.True(row.IsRetired);

        RemapRule rule = Assert.Single(retired.AppendedRules);
        Assert.Equal(RemapRuleKind.Retired, rule.Kind);
        Assert.Equal(2, rule.FromId);
    }

    [Fact]
    public async Task AddingATypeRewritesNoExistingChunkAndMovesTheManifestHash()
    {
        (InMemoryContentAuthoringStore store, ContentTypeRegistry registry, ContentPublishPlan seeded) =
            await SeedTwoChunksAsync();

        // The new type registers into the SAME registry, which is what a game release adding a type does.
        PublishFixtures.Register(registry, PublishFixtures.Other);
        ContentPublishPlan grown = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.After(seeded),
            ContentEdit.Add(new ContentTypeId(PublishFixtures.OtherTypeId), new ContentKey("fresh"), PublishFixtures.Fields(9))));

        Assert.Equal(HashOf(seeded, 0), HashOf(grown, 0));
        Assert.Equal(HashOf(seeded, 1), HashOf(grown, 1));
        Assert.Equal(2, grown.ChunksReused);
        Assert.Equal(1, grown.ChunksWritten);
        Assert.NotEqual(seeded.ServerManifestHash, grown.ServerManifestHash);
        Assert.NotEqual(seeded.ClientManifestHash, grown.ClientManifestHash);
    }

    [Fact]
    public async Task AnUnaffectedChunkCarriesForwardEverySideThePreviousVersionWrote()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.SecretThing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);

        ContentPublishPlan seeded = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.Empty,
            ContentEdit.Import(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(1, 101)),
            ContentEdit.Import(
                Thing,
                PublishFixtures.SecondChunkId,
                new ContentKey("three_hundred"),
                PublishFixtures.Fields(300, 300))));

        // Four chunk rows, two chunks at two sides each, because the type is Client with a ServerOnly field.
        Assert.Equal(4, seeded.Chunks.Count);

        ContentPublishPlan edited = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.After(seeded),
            ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(11, 111))));

        Assert.Equal(4, edited.Chunks.Count);
        Assert.Equal(2, edited.ChunksWritten);
        Assert.Equal(2, edited.ChunksReused);
        foreach (ContentVisibility side in new[] { ContentVisibility.Client, ContentVisibility.ServerOnly })
        {
            Assert.Equal(
                PublishFixtures.Chunk(seeded, PublishFixtures.ThingTypeId, 1, side).Hash,
                PublishFixtures.Chunk(edited, PublishFixtures.ThingTypeId, 1, side).Hash);
            Assert.True(PublishFixtures.Chunk(edited, PublishFixtures.ThingTypeId, 1, side).IsReused);
        }
    }

    [Fact]
    public async Task ARewrittenChunkCarriesEveryLiveRowOfItsRangeSortedAscending()
    {
        (InMemoryContentAuthoringStore store, ContentTypeRegistry registry, ContentPublishPlan seeded) =
            await SeedTwoChunksAsync();

        // The seed carried id 300, so the high water sits there and the next ordinary add takes 301, which
        // is chunk 1 and not chunk 0.
        ContentPublishPlan added = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.After(seeded),
            ContentEdit.Add(Thing, new ContentKey("three_oh_one"), PublishFixtures.Fields(3))));

        Assert.Equal(PublishFixtures.SecondChunkId + 1, Assert.Single(added.Allocation.Entries).DefinitionId);
        Assert.Equal(HashOf(seeded, 0), HashOf(added, 0));

        ContentChunkRecord chunk = PublishFixtures.Chunk(added, PublishFixtures.ThingTypeId, 1, ContentVisibility.Client);
        Assert.Equal(2, chunk.RowCount);
        Assert.True(
            ContentChunkCodec.TryDecode(chunk.StoredFile.Span, registry, out ContentChunk? decoded, out string? reason),
            reason ?? "no reason");
        Assert.Equal([PublishFixtures.SecondChunkId, PublishFixtures.SecondChunkId + 1], decoded.Ids.ToArray());
    }

    [Fact]
    public async Task TheChunkHashIsOverTheUncompressedBytesSoACompressorChangeIsANoOp()
    {
        (InMemoryContentAuthoringStore store, ContentTypeRegistry registry, ContentPublishPlan seeded) =
            await SeedTwoChunksAsync();

        ContentChunkRecord chunk = PublishFixtures.Chunk(seeded, PublishFixtures.ThingTypeId, 0, ContentVisibility.Client);
        Assert.True(registry.TryGet(Thing, out ContentTypeRegistration? registration));
        Assert.True(
            ContentChunkCodec.TryDecode(chunk.StoredFile.Span, registry, out ContentChunk? decoded, out string? reason),
            reason ?? "no reason");
        Assert.Equal(chunk.UncompressedBytes, decoded.Body.Length);
        Assert.Equal(registration.Type, decoded.Type);
    }

    static string HashOf(ContentPublishPlan plan, int chunkIndex)
        => PublishFixtures.Chunk(plan, PublishFixtures.ThingTypeId, chunkIndex, ContentVisibility.Client).Hash;

    /// <summary>
    /// Two rows in chunk 0 and one in chunk 1, seeded through the carried-id path so the ids are the test's
    /// and not the allocator's. At 256 slots per chunk that is ids 1 and 2 against id 300.
    /// </summary>
    static async Task<(InMemoryContentAuthoringStore Store, ContentTypeRegistry Registry, ContentPublishPlan Seeded)>
        SeedTwoChunksAsync()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);

        ContentPublishPlan seeded = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            ContentEdit.Import(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(1)),
            ContentEdit.Import(Thing, 2, new ContentKey("two"), PublishFixtures.Fields(2)),
            ContentEdit.Import(
                Thing,
                PublishFixtures.SecondChunkId,
                new ContentKey("three_hundred"),
                PublishFixtures.Fields(300))));

        Assert.Equal(2, seeded.Chunks.Count);
        return (store, registry, seeded);
    }
}
