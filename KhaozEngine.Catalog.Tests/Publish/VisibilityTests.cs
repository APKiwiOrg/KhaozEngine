using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// Spec 6.7 and contracts 11.3: a <c>Client</c> type MAY carry a per-field <c>ServerOnly</c> override, and
/// this is the case the two-chunk path exists for. The client chunk is encoded with those fields omitted,
/// which gives it its own canonical bytes and its own hash, so the two sides of one id range are two
/// addresses because they are two different runs of bytes.
/// <para>
/// <c>KEC0014</c> therefore fires on exactly one thing, the CLIENT-side encoded bytes of a chunk still
/// carrying a field the schema marks <c>ServerOnly</c>. That is an ENCODER defect and never an authoring
/// one, which is why the test for it injects a stub encoder rather than authoring a row.
/// </para>
/// </summary>
public sealed class VisibilityTests
{
    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    [Fact]
    public async Task TheClientChunkOmitsTheServerOnlyFieldAndTheServerChunkKeepsIt()
    {
        (InMemoryContentAuthoringStore store, ContentTypeRegistry registry, ContentPublishPlan plan) =
            await PublishSecretAsync();

        ContentChunkRecord client = PublishFixtures.Chunk(plan, PublishFixtures.ThingTypeId, 0, ContentVisibility.Client);
        ContentChunkRecord server = PublishFixtures.Chunk(plan, PublishFixtures.ThingTypeId, 0, ContentVisibility.ServerOnly);

        ContentRow clientRow = PublishFixtures.DecodeRow(registry, client, 1);
        ContentRow serverRow = PublishFixtures.DecodeRow(registry, server, 1);

        Assert.True(registry.TryGet(Thing, out ContentTypeRegistration? registration));
        int secret = SecretIndex(registration);
        Assert.True(clientRow.Fields[secret].IsAbsent);
        Assert.Equal(101, serverRow.Fields[secret].Number);

        // The Client fields survive on both sides, so the omission is the one field and not the whole row.
        Assert.Equal(1, clientRow.Fields[0].Number);
        Assert.Equal(1, serverRow.Fields[0].Number);
        Assert.Equal(new ContentKey("one"), clientRow.Key);
        _ = store;
    }

    [Fact]
    public async Task BothSidesExistUnderOneAddressAndTheirHashesDiffer()
    {
        (InMemoryContentAuthoringStore store, ContentTypeRegistry registry, ContentPublishPlan plan) =
            await PublishSecretAsync();

        ContentChunkRecord client = PublishFixtures.Chunk(plan, PublishFixtures.ThingTypeId, 0, ContentVisibility.Client);
        ContentChunkRecord server = PublishFixtures.Chunk(plan, PublishFixtures.ThingTypeId, 0, ContentVisibility.ServerOnly);

        Assert.NotEqual(client.Hash, server.Hash);
        Assert.Equal(client.Type, server.Type);
        Assert.Equal(client.ChunkIndex, server.ChunkIndex);
        Assert.Equal(2, plan.Chunks.Count(chunk => chunk.Type == Thing && chunk.ChunkIndex == 0));
        _ = store;
        _ = registry;
    }

    [Fact]
    public async Task TheServerManifestTakesTheServerSideAndTheClientManifestTakesTheClientSide()
    {
        (InMemoryContentAuthoringStore store, ContentTypeRegistry registry, ContentPublishPlan plan) =
            await PublishSecretAsync();

        ContentChunkRecord client = PublishFixtures.Chunk(plan, PublishFixtures.ThingTypeId, 0, ContentVisibility.Client);
        ContentChunkRecord server = PublishFixtures.Chunk(plan, PublishFixtures.ThingTypeId, 0, ContentVisibility.ServerOnly);

        Assert.Equal(server.Hash, ManifestHash(plan.ServerManifest, PublishFixtures.ThingTypeId, 0));
        Assert.Equal(client.Hash, ManifestHash(plan.ClientManifest, PublishFixtures.ThingTypeId, 0));
        _ = store;
        _ = registry;
    }

    [Fact]
    public async Task ATypeWithNoServerOnlyFieldWritesOneChunkThatBothManifestsName()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);

        ContentPublishPlan plan = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            ContentEdit.Import(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(1))));

        ContentChunkRecord only = Assert.Single(plan.Chunks);
        Assert.Equal(ContentVisibility.Client, only.Side);
        Assert.Equal(only.Hash, ManifestHash(plan.ServerManifest, PublishFixtures.ThingTypeId, 0));
        Assert.Equal(only.Hash, ManifestHash(plan.ClientManifest, PublishFixtures.ThingTypeId, 0));
    }

    [Fact]
    public async Task TheClientManifestOmitsEveryServerOnlyType()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);

        var tag = new ContentTypeId(EngineContentTypes.TagTypeId);
        var table = new ContentTypeId(EngineContentTypes.LootTableTypeId);

        ContentPublishPlan plan = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            ContentEdit.Import(tag, 1, new ContentKey("metal"), []),
            ContentEdit.Import(
                table,
                1,
                new ContentKey("goblin"),
                [
                    new ContentFieldEdit(LootTableContentType.RollCountField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 1)),
                ])));

        Assert.Contains(plan.ServerManifest!.Types, entry => entry.TypeId == EngineContentTypes.LootTableTypeId);
        Assert.DoesNotContain(plan.ClientManifest!.Types, entry => entry.TypeId == EngineContentTypes.LootTableTypeId);
        Assert.Contains(plan.ClientManifest.Types, entry => entry.TypeId == EngineContentTypes.TagTypeId);

        // A ServerOnly TYPE produces one chunk and that chunk is the server side's.
        ContentChunkRecord table0 = PublishFixtures.Chunk(plan, EngineContentTypes.LootTableTypeId, 0, ContentVisibility.ServerOnly);
        Assert.False(table0.IsReused);
    }

    [Fact]
    public async Task AStubEncoderLeavingTheFieldInTheClientBytesIsRefusedByKec0014()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.SecretThing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry, rowEncoder: new LeakyRowEncoder());

        ContentPublishPlan plan = await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.Empty,
            ContentEdit.Import(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(1, 101)));

        Assert.False(plan.IsValid, PublishFixtures.Findings(plan));
        ContentFinding finding = Assert.Single(
            plan.Validation.Findings,
            f => string.Equals(f.Code, "KEC0014", StringComparison.Ordinal));
        Assert.Equal(Thing, finding.Type);
        Assert.Equal(1, finding.Id);
        Assert.Contains(PublishFixtures.SecretField, finding.Message, StringComparison.Ordinal);
        Assert.Null(plan.ServerManifest);
    }

    [Fact]
    public async Task AServerOnlyFieldAuthoredOnAClientRowIsNotAFinding()
    {
        // Before the client encode existed, KEC0014 refused the authored value, which made a Client type
        // with a required ServerOnly field unpublishable both ways. Nothing refuses it now and nothing needs
        // to, because the client chunk omits the field at encode time.
        (InMemoryContentAuthoringStore store, ContentTypeRegistry registry, ContentPublishPlan plan) =
            await PublishSecretAsync();

        Assert.True(plan.IsValid, PublishFixtures.Findings(plan));
        Assert.False(PublishFixtures.Has(plan, "KEC0014"));
        _ = store;
        _ = registry;
    }

    static int SecretIndex(ContentTypeRegistration registration)
    {
        for (int i = 0; i < registration.Schema.Fields.Count; i++)
        {
            if (string.Equals(registration.Schema.Fields[i].Name, PublishFixtures.SecretField, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException("The fixture schema carries no secret field.");
    }

    static string ManifestHash(ContentManifest? manifest, ushort typeId, uint chunkIndex)
    {
        Assert.NotNull(manifest);
        ManifestTypeEntry type = Assert.Single(manifest.Types, entry => entry.TypeId == typeId);
        return Assert.Single(type.Chunks, chunk => chunk.ChunkIndex == chunkIndex).Hash;
    }

    static async Task<(InMemoryContentAuthoringStore Store, ContentTypeRegistry Registry, ContentPublishPlan Plan)>
        PublishSecretAsync()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.SecretThing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);

        ContentPublishPlan plan = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            ContentEdit.Import(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(1, 101))));

        return (store, registry, plan);
    }
}
