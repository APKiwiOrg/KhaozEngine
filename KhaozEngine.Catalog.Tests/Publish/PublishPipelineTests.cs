using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// The ordered steps of spec 6.1, the two id SOURCES of spec 6.3, and contracts 4.3's
/// registration-order independence.
/// <para>
/// Steps 1 to 8 write nothing durable, so every test here drives the plan rather than the store's published
/// state: the store carries the draft, the baseline carries the base version, and the plan carries
/// everything the commit of task 16 will need.
/// </para>
/// </summary>
public sealed class PublishPipelineTests
{
    [Fact]
    public async Task TheStepHookStopsAfterTheManifestBecauseTheCommitIsNotHere()
    {
        var steps = new List<ContentPublishStep>();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry, steps.Add);

        ContentPublishPlan plan = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.Empty,
            ContentEdit.Add(new ContentTypeId(PublishFixtures.ThingTypeId), new ContentKey("one"), PublishFixtures.Fields(1))));

        Assert.Equal(
            [
                ContentPublishStep.BeforeIdAllocation,
                ContentPublishStep.AfterIdAllocation,
                ContentPublishStep.BeforeChunkWrite,
                ContentPublishStep.AfterChunkWrite,
                ContentPublishStep.BeforeManifestWrite,
                ContentPublishStep.AfterManifestWrite,
            ],
            steps);
        Assert.Equal(1, plan.VersionNumber);
    }

    [Fact]
    public async Task AnAddWithNoIdIsAllocatedOneInEditOrdinalOrder()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);

        ContentPublishPlan plan = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            ContentEdit.Add(type, new ContentKey("first"), PublishFixtures.Fields(1)),
            ContentEdit.Add(type, new ContentKey("second"), PublishFixtures.Fields(2))));

        Assert.Equal([1, 2], plan.Allocation.Entries.Select(entry => entry.DefinitionId));
        Assert.All(plan.Allocation.Entries, entry => Assert.Equal(ContentIdSource.Plain, entry.Source));
        Assert.Empty(plan.Allocation.Seeds);
    }

    [Fact]
    public async Task AnImportKeepsTheIdItCarriesAndSeedsTheHighWater()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);

        ContentPublishPlan imported = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            ContentEdit.Import(type, 1, new ContentKey("one"), PublishFixtures.Fields(1)),
            ContentEdit.Import(type, 35, new ContentKey("thirty_five"), PublishFixtures.Fields(35))));

        Assert.Equal([1, 35], imported.Allocation.Entries.Select(entry => entry.DefinitionId));
        Assert.All(imported.Allocation.Entries, entry => Assert.Equal(ContentIdSource.Carried, entry.Source));
        ContentIdSeed seed = Assert.Single(imported.Allocation.Seeds);
        Assert.Equal(type, seed.Type);
        Assert.Equal(35, seed.SeededThrough);

        // The point of the seeding: the first ordinary add after an import does not land on an imported row.
        ContentPublishPlan next = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.After(imported),
            ContentEdit.Add(type, new ContentKey("thirty_six"), PublishFixtures.Fields(36))));

        Assert.Equal(36, Assert.Single(next.Allocation.Entries).DefinitionId);
        Assert.Empty(next.Allocation.Seeds);
    }

    [Fact]
    public async Task TwoEmptyDatabasesAllocateTheSameIdsForTheSameIdFreeBundle()
    {
        static async Task<int[]> PublishAsync()
        {
            ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
            InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
            var thing = new ContentTypeId(PublishFixtures.ThingTypeId);
            var other = new ContentTypeId(PublishFixtures.OtherTypeId);

            ContentPublishPlan plan = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
                store,
                PublishFixtures.Publisher(store, registry),
                ContentPublishBaseline.Empty,
                ContentEdit.Add(thing, new ContentKey("alpha"), PublishFixtures.Fields(1)),
                ContentEdit.Add(other, new ContentKey("beta"), PublishFixtures.Fields(2)),
                ContentEdit.Add(thing, new ContentKey("gamma"), PublishFixtures.Fields(3))));

            return plan.Allocation.Entries.Select(entry => entry.DefinitionId).ToArray();
        }

        Assert.Equal(await PublishAsync(), await PublishAsync());
    }

    [Fact]
    public async Task AForkAllocatesPlainlyAndItsCopyInheritsTheSourceRowsFamily()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);
        ContentFamily family = await store.CreateFamilyAsync(type, "swords", 16, PublishFixtures.Actor, "oid:tests");

        ContentPublishPlan seeded = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            ContentEdit.Add(type, new ContentKey("sword"), PublishFixtures.Fields(1), family.FamilyId)));

        ContentIdAllocationEntry member = Assert.Single(seeded.Allocation.Entries);
        Assert.Equal(ContentIdSource.Family, member.Source);
        Assert.Equal(family.Blocks[0].BaseId, member.DefinitionId);

        ContentPublishPlan forked = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.After(seeded),
            ContentEdit.Fork(
                type,
                member.DefinitionId,
                new ContentKey("sword"),
                new ContentKey("sword_legacy"),
                PublishFixtures.LegacyField,
                PublishFixtures.Fields(2))));

        ContentIdAllocationEntry copy = Assert.Single(forked.Allocation.Entries);
        Assert.Equal(ContentIdSource.Plain, copy.Source);
        Assert.Equal(family.FamilyId, copy.FamilyId);
        Assert.NotEqual(member.DefinitionId, copy.DefinitionId);

        // The copy is written FIRST, so the kind 3 rule appended last names a to_id already live at V.
        RemapRule rule = Assert.Single(forked.AppendedRules);
        Assert.Equal(RemapRuleKind.MovedToLegacy, rule.Kind);
        Assert.Equal(member.DefinitionId, rule.FromId);
        Assert.Equal(copy.DefinitionId, rule.ToId);
    }

    [Fact]
    public async Task AStaleExpectedBaseVersionIsRefusedWithBothNumbersNamed()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);
        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(new ContentTypeId(PublishFixtures.ThingTypeId), new ContentKey("one"), PublishFixtures.Fields(1)));

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => publisher.PrepareAsync(PublishFixtures.Request(7), ContentPublishBaseline.Empty));

        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, refused.Reason);
        Assert.Contains("7", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APublishWithNoOpenDraftIsRefused()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => publisher.PrepareAsync(PublishFixtures.Request(0), ContentPublishBaseline.Empty));

        Assert.Equal(ContentAuthoringException.NoOpenDraftReason, refused.Reason);
    }

    [Fact]
    public async Task AnInvalidCandidateStopsBeforeTheChunksAndReportsEveryFinding()
    {
        var steps = new List<ContentPublishStep>();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);

        // Two rows under one key, which is KEC0002, reached through two imports so the ids are ours.
        ContentPublishPlan plan = await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry, steps.Add),
            ContentPublishBaseline.Empty,
            ContentEdit.Import(type, 1, new ContentKey("same"), PublishFixtures.Fields(1)),
            ContentEdit.Import(type, 2, new ContentKey("same"), PublishFixtures.Fields(2)));

        Assert.False(plan.IsValid, PublishFixtures.Findings(plan));
        Assert.True(PublishFixtures.Has(plan, "KEC0002"), PublishFixtures.Findings(plan));
        Assert.Empty(plan.Chunks);
        Assert.Null(plan.ServerManifest);
        Assert.Null(plan.ClientManifest);
        Assert.DoesNotContain(ContentPublishStep.BeforeChunkWrite, steps);
    }

    [Fact]
    public async Task ThePreviousSnapshotIsNonNullOnEveryPublishAfterTheFirst()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);

        ContentPublishPlan first = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            ContentEdit.Add(type, new ContentKey("one"), PublishFixtures.Fields(1))));

        // KEC0000 says previous was null, which is true of a first publish and false of every later one.
        Assert.True(PublishFixtures.Has(first, ContentValidator.InformationalCode));

        ContentPublishPlan second = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.After(first),
            ContentEdit.Add(type, new ContentKey("two"), PublishFixtures.Fields(2))));

        Assert.False(PublishFixtures.Has(second, ContentValidator.InformationalCode));
        Assert.Equal(2, second.VersionNumber);
    }

    [Fact]
    public async Task TheMinimumBuildsCarryForwardWhenTheRequestOmitsThem()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);

        await PublishFixtures.ApplyAsync(store, ContentEdit.Add(type, new ContentKey("one"), PublishFixtures.Fields(1)));
        ContentPublishPlan first = PublishFixtures.AssertValid(
            await publisher.PrepareAsync(PublishFixtures.Request(0, 40, 41), ContentPublishBaseline.Empty));
        await store.DiscardDraftAsync(PublishFixtures.Actor, "oid:tests");

        Assert.Equal(40, first.MinimumServerBuild);
        Assert.Equal(41, first.MinimumClientBuild);

        ContentPublishPlan second = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.After(first),
            ContentEdit.Add(type, new ContentKey("two"), PublishFixtures.Fields(2))));

        Assert.Equal(40, second.MinimumServerBuild);
        Assert.Equal(41, second.MinimumClientBuild);
    }

    [Fact]
    public async Task RaisingAMinimumBuildMovesTheManifestHashAndNoChunkHash()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);

        ContentPublishPlan first = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.Empty,
            ContentEdit.Add(type, new ContentKey("one"), PublishFixtures.Fields(1))));

        await PublishFixtures.ApplyAsync(store, ContentEdit.Update(type, 1, new ContentKey("one"), PublishFixtures.Fields(1)));
        ContentPublishPlan second = PublishFixtures.AssertValid(
            await publisher.PrepareAsync(PublishFixtures.Request(1, 99, null), ContentPublishBaseline.After(first)));

        Assert.Equal(99, second.MinimumServerBuild);
        Assert.Equal(
            PublishFixtures.Chunk(first, PublishFixtures.ThingTypeId, 0, ContentVisibility.Client).Hash,
            PublishFixtures.Chunk(second, PublishFixtures.ThingTypeId, 0, ContentVisibility.Client).Hash);
        Assert.NotEqual(first.ServerManifestHash, second.ServerManifestHash);
    }

    [Fact]
    public async Task TheTwoManifestHashesOfOneVersionAreNeverEqual()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);

        ContentPublishPlan plan = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            ContentEdit.Add(new ContentTypeId(PublishFixtures.ThingTypeId), new ContentKey("one"), PublishFixtures.Fields(1))));

        Assert.NotEqual(plan.ServerManifestHash, plan.ClientManifestHash);
        Assert.Equal(64, plan.ServerManifestHash.Length);
        Assert.Equal(64, plan.ClientManifestHash.Length);
    }

    /// <summary>
    /// Contracts 4.3's direct test: two processes registering the same types in DIFFERENT orders produce
    /// byte-identical packs and byte-identical manifests. Ten shuffles from one seeded source, because a
    /// single shuffle that happens to match the reference order proves nothing.
    /// <para>
    /// This is the test Ruinborne's wire index would have failed, since the same item has a different byte
    /// index depending on whether the catalog loaded from SQL or from code defaults.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheManifestHashesAreIndependentOfRegistrationOrder()
    {
        PublishTypeSpec[] reference =
        [
            new(1024, "alpha"),
            new(1025, "beta"),
            new(1026, "gamma", ContentVisibility.ServerOnly),
            new(1027, "delta", HasSecret: true),
            new(1028, "epsilon"),
        ];

        ContentPublishPlan expected = await PublishOrderAsync(reference);
        var random = new SeededRandomSource(0x5EEDC0DE);
        for (int shuffle = 0; shuffle < 10; shuffle++)
        {
            PublishTypeSpec[] shuffled = Shuffle(reference, random);
            ContentPublishPlan actual = await PublishOrderAsync(shuffled);

            string order = string.Join(", ", shuffled.Select(spec => spec.TypeKey));
            Assert.True(
                string.Equals(expected.ServerManifestHash, actual.ServerManifestHash, StringComparison.Ordinal),
                FormattableString.Invariant($"Server manifest hash moved for registration order {order}."));
            Assert.True(
                string.Equals(expected.ClientManifestHash, actual.ClientManifestHash, StringComparison.Ordinal),
                FormattableString.Invariant($"Client manifest hash moved for registration order {order}."));
            Assert.Equal(
                expected.Chunks.Select(chunk => chunk.Hash).Order(StringComparer.Ordinal),
                actual.Chunks.Select(chunk => chunk.Hash).Order(StringComparer.Ordinal));
        }
    }

    static async Task<ContentPublishPlan> PublishOrderAsync(PublishTypeSpec[] specs)
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(specs);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);

        // The EDITS are in one fixed order whatever the registration order is, so the ids are the same too.
        var edits = new List<ContentEdit>();
        foreach (PublishTypeSpec spec in specs.OrderBy(spec => spec.TypeId))
        {
            edits.Add(ContentEdit.Import(
                new ContentTypeId(spec.TypeId),
                1,
                new ContentKey(spec.TypeKey + "_one"),
                PublishFixtures.Fields(spec.TypeId, spec.HasSecret ? 7 : null)));
        }

        return PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            PublishFixtures.Publisher(store, registry),
            ContentPublishBaseline.Empty,
            edits.ToArray()));
    }

    static PublishTypeSpec[] Shuffle(PublishTypeSpec[] source, IRandomSource random)
    {
        var copy = source.ToArray();
        for (int i = copy.Length - 1; i > 0; i--)
        {
            int j = random.NextInt(0, i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy;
    }
}
