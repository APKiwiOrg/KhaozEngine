using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// Spec 6.8 against spec 9.6's step 6 row: the manifest's type list is the REGISTRY's at publish time, so a
/// registered type that authored no rows is named with zero chunks rather than left out.
/// <para>
/// Boot refuses a version whose manifest does not name a type this build registers, and it can only mean
/// "this pack predates the registration" when an empty type is still named. Building the list out of the
/// CHUNKS made the two indistinguishable and made a real pack unbootable over <c>base_socket</c> alone
/// (https://github.com/APKiwiOrg/KhaozEngine/issues/937).
/// </para>
/// <para>
/// The client manifest's own rule is untouched and is a different one: it omits a <c>ServerOnly</c> TYPE
/// outright (contracts 11.3), which is why it names fewer types than the server manifest rather than the same
/// list with empty entries.
/// </para>
/// </summary>
public sealed class ManifestTypeListTests
{
    [Fact]
    public async Task ARegisteredTypeThatAuthoredNoRowsIsNamedWithZeroChunksOnBothSides()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);

        ContentPublishPlan plan = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            ContentEdit.Import(
                new ContentTypeId(PublishFixtures.ThingTypeId), 1, new ContentKey("one"), PublishFixtures.Fields(1))));

        // The publish touched one type, so the other one has no chunk row anywhere in the plan.
        Assert.DoesNotContain(plan.Chunks, chunk => chunk.Type.Value == PublishFixtures.OtherTypeId);

        foreach (ContentManifest manifest in Both(plan))
        {
            Assert.Equal(
                [PublishFixtures.ThingTypeId, PublishFixtures.OtherTypeId],
                manifest.Types.Select(entry => entry.TypeId));

            ManifestTypeEntry empty = manifest.Types[1];
            Assert.Equal(PublishFixtures.OtherTypeKey, empty.TypeKey);
            Assert.Equal(PublishFixtures.ChunkSlots, empty.ChunkSlots);
            Assert.Equal(ContentVisibility.Client, empty.Visibility);
            Assert.Empty(empty.Chunks);

            Assert.Single(manifest.Types[0].Chunks);
        }
    }

    [Fact]
    public async Task TheServerManifestNamesEveryEngineTypeAndTheClientOneOmitsTheServerOnlyTypes()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);

        ContentPublishPlan plan = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            ContentEdit.Import(new ContentTypeId(EngineContentTypes.TagTypeId), 1, new ContentKey("metal"), [])));

        // Every registered type, including the five the publish authored nothing for. base_socket is the one
        // the issue was reported against, because no game authors rows for all seven.
        Assert.Equal(
            [
                EngineContentTypes.TagTypeId,
                EngineContentTypes.ItemTypeId,
                EngineContentTypes.StatTypeId,
                EngineContentTypes.LootTableTypeId,
                EngineContentTypes.LootEntryTypeId,
                EngineContentTypes.BaseSocketTypeId,
                EngineContentTypes.ItemCategoryTypeId,
            ],
            plan.ServerManifest!.Types.Select(entry => entry.TypeId));

        // The two ServerOnly types and nothing else are missing from the client side.
        Assert.Equal(
            [
                EngineContentTypes.TagTypeId,
                EngineContentTypes.ItemTypeId,
                EngineContentTypes.StatTypeId,
                EngineContentTypes.BaseSocketTypeId,
                EngineContentTypes.ItemCategoryTypeId,
            ],
            plan.ClientManifest!.Types.Select(entry => entry.TypeId));

        foreach (ContentManifest manifest in Both(plan))
        {
            foreach (ManifestTypeEntry entry in manifest.Types)
            {
                int expected = entry.TypeId == EngineContentTypes.TagTypeId ? 1 : 0;
                Assert.Equal(expected, entry.Chunks.Count);
            }
        }

        Assert.Equal(
            EngineContentTypes.BaseSocketTypeKey,
            Assert.Single(
                plan.ServerManifest.Types,
                entry => entry.TypeId == EngineContentTypes.BaseSocketTypeId).TypeKey);
    }

    [Fact]
    public async Task AManifestNamingAnEmptyTypeRoundTripsThroughTheCodecUnderTheSameHash()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);

        ContentPublishPlan plan = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            ContentEdit.Import(new ContentTypeId(EngineContentTypes.TagTypeId), 1, new ContentKey("metal"), [])));

        RoundTrip(registry, plan.ServerManifest!, plan.ServerManifestHash);
        RoundTrip(registry, plan.ClientManifest!, plan.ClientManifestHash);
    }

    /// <summary>The file a type with no chunks writes decodes back to the same type list and the same hash.</summary>
    static void RoundTrip(ContentTypeRegistry registry, ContentManifest manifest, string hash)
    {
        byte[] file = ContentManifestCodec.Encode(manifest);
        Assert.True(
            ContentManifestCodec.TryDecode(file, manifest.Side, registry, out ContentManifest? read, out string? reason),
            reason ?? "no reason");

        Assert.Equal(
            manifest.Types.Select(entry => entry.TypeId),
            read.Types.Select(entry => entry.TypeId));
        Assert.Equal(
            manifest.Types.Select(entry => entry.Chunks.Count),
            read.Types.Select(entry => entry.Chunks.Count));
        Assert.Equal(hash, ContentManifestText.Hash(read));
    }

    /// <summary>Both sides of one plan, for an assertion that holds on each.</summary>
    static IEnumerable<ContentManifest> Both(ContentPublishPlan plan)
    {
        Assert.NotNull(plan.ServerManifest);
        Assert.NotNull(plan.ClientManifest);
        yield return plan.ServerManifest;
        yield return plan.ClientManifest;
    }
}
