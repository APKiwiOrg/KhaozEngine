using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Validation;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Authoring;

/// <summary>
/// The in-memory store's own behaviour: the metadata a provider carries in <c>catalog_metadata</c>, the one
/// open draft, the boundary check that refuses an edit naming a field the schema does not declare, and the
/// field-level audit.
/// <para>
/// It is a TEST AND TOOLING store, so what is asserted here is the shape a provider must match rather than a
/// production behaviour. The published half, versions and rows, arrives with the publish pipeline, which is
/// why the reads over it answer empty rather than being approximated.
/// </para>
/// </summary>
public class InMemoryContentAuthoringStoreTests
{
    const string Actor = "store-tests";
    const string Operator = "oid:tests";

    static readonly ContentTypeId Game = new(ContentValidationFixtures.GameTypeId);

    static InMemoryContentAuthoringStore NewStore()
        => new(ContentValidationFixtures.GameRegistry(
            ContentValidationFixtures.Schema(
                new ContentFieldEntry("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
                new ContentFieldEntry("weight", ContentFieldKind.Int, null, ContentVisibility.Client, false))));

    static ContentFieldEdit Int(string name, long value)
        => new(name, ContentFieldValue.OfNumber(ContentFieldKind.Int, value));

    [Fact]
    public async Task AnEmptyStoreReportsTheCurrentSchemaVersionAnEpochAndNoActiveVersion()
    {
        InMemoryContentAuthoringStore store = NewStore();
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(InMemoryContentAuthoringStore.SchemaVersion, await store.GetSchemaVersionAsync());
        Assert.Equal(InMemoryContentAuthoringStore.NoActiveVersion, await store.GetActiveVersionAsync());
        Assert.Null(await store.GetPinnedVersionAsync());
        Assert.NotEmpty(await store.GetStoreEpochAsync());
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Null(await store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task TwoStoresMintDifferentEpochsBecauseTheEpochIsTheIdentityOfOneDatabase()
    {
        // Two databases can hold a version 12 that share no history, and the epoch is what tells them apart.
        string first = await NewStore().GetStoreEpochAsync();
        string second = await NewStore().GetStoreEpochAsync();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task ApplyEditsOpensADraftAgainstTheActiveVersionAndKeepsTheEditOrder()
    {
        InMemoryContentAuthoringStore store = NewStore();

        ContentDraft draft = await store.ApplyEditsAsync(
            [
                ContentEdit.Add(Game, new ContentKey("iron_sword"), [Int("value", 120)]),
                ContentEdit.Add(Game, new ContentKey("oak_shield"), [Int("value", 7)]),
            ],
            Actor,
            Operator,
            "autumn price pass");

        Assert.Equal(InMemoryContentAuthoringStore.NoActiveVersion, draft.BaseVersion);
        Assert.Equal(2, draft.EditCount);
        Assert.Equal("autumn price pass", draft.Note);
        Assert.Equal(
            ["iron_sword", "oak_shield"],
            new[] { draft.Changes.Edits[0].Key.ToString(), draft.Changes.Edits[1].Key.ToString() });
        Assert.Same(draft, await store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task ASecondEditOfOneTargetUnderADifferentOperationLeavesTheDraftUntouched()
    {
        InMemoryContentAuthoringStore store = NewStore();
        var key = new ContentKey("stone_sword");
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Game, 13, key, [Int("value", 45)])], Actor, Operator, "price");

        ContentAuthoringException error = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ApplyEditsAsync(
                [ContentEdit.Retire(Game, 13, key, ContentRetirePolicy.Placeholder, 0)],
                Actor,
                Operator,
                "retire"));

        Assert.Equal(ContentAuthoringException.EditTargetCollisionReason, error.Reason);
        ContentDraft? draft = await store.GetOpenDraftAsync();
        Assert.NotNull(draft);
        ContentEdit standing = Assert.Single(draft.Changes.Edits);
        Assert.Equal(ContentEditOperation.Update, standing.Operation);
        Assert.Equal(45, Assert.Single(standing.Fields).Value.Number);
    }

    [Fact]
    public async Task AnEditNamingAFieldTheSchemaDoesNotDeclareIsRefusedAtTheBoundary()
    {
        // At the BOUNDARY rather than at publish, so the console hears about a typo on the request that
        // carried it rather than at the end of a publish that then aborts.
        InMemoryContentAuthoringStore store = NewStore();

        ContentAuthoringException error = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ApplyEditsAsync(
                [ContentEdit.Add(Game, new ContentKey("iron_sword"), [Int("valeu", 120)])],
                Actor,
                Operator,
                string.Empty));

        Assert.Equal(ContentAuthoringException.UnknownFieldReason, error.Reason);
        Assert.Contains("valeu", error.Message, StringComparison.Ordinal);
        Assert.Null(await store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task OneRefusedEditInABatchLandsNoneOfThem()
    {
        InMemoryContentAuthoringStore store = NewStore();

        await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ApplyEditsAsync(
                [
                    ContentEdit.Add(Game, new ContentKey("iron_sword"), [Int("value", 120)]),
                    ContentEdit.Add(Game, new ContentKey("oak_shield"), [Int("nope", 7)]),
                ],
                Actor,
                Operator,
                string.Empty));

        Assert.Null(await store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task AnEditNamingAnUnregisteredTypeIsRefused()
    {
        InMemoryContentAuthoringStore store = NewStore();

        ContentAuthoringException error = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ApplyEditsAsync(
                [ContentEdit.Add(new ContentTypeId(2), new ContentKey("iron_sword"), [Int("value", 1)])],
                Actor,
                Operator,
                string.Empty));

        Assert.Equal(ContentAuthoringException.UnknownTypeReason, error.Reason);
    }

    [Fact]
    public async Task EachChangedFieldWritesItsOwnAuditEntry()
    {
        // The unit of an audit row is one FIELD, so an edit that changes two fields writes two entries.
        InMemoryContentAuthoringStore store = NewStore();

        await store.ApplyEditsAsync(
            [ContentEdit.Update(Game, 13, new ContentKey("stone_sword"), [Int("value", 45), Int("weight", 3)])],
            Actor,
            Operator,
            "autumn price pass");

        IReadOnlyList<ContentAuditEntry> audit = await store.ListAuditAsync(Game, 13, 0, 50);
        Assert.Equal(2, audit.Count);
        Assert.All(audit, entry =>
        {
            Assert.Equal(ContentAuditActions.DraftEdit, entry.Action);
            Assert.Equal(Actor, entry.Actor);
            Assert.Equal(Operator, entry.Operator);
            Assert.Equal("autumn price pass", entry.Note);
            Assert.Equal(0, entry.VersionNumber);
        });

        // Newest first.
        Assert.Equal("weight", audit[0].FieldName);
        Assert.Equal("3", audit[0].AfterValue);
        Assert.Equal("value", audit[1].FieldName);
        Assert.Equal("45", audit[1].AfterValue);
    }

    [Fact]
    public async Task ADiscardClearsTheDraftAndLeavesATraceCarryingTheEditCount()
    {
        InMemoryContentAuthoringStore store = NewStore();
        await store.ApplyEditsAsync(
            [ContentEdit.Add(Game, new ContentKey("iron_sword"), [Int("value", 120)])],
            Actor,
            Operator,
            string.Empty);

        await store.DiscardDraftAsync(Actor, Operator);

        Assert.Null(await store.GetOpenDraftAsync());
        IReadOnlyList<ContentAuditEntry> audit = await store.ListAuditAsync(default, 0, 0, 50);
        Assert.Equal(ContentAuditActions.DraftDiscard, audit[0].Action);
        Assert.Equal("1", audit[0].BeforeValue);
    }

    [Fact]
    public async Task AFamilyCreationLeavesAnAuditEntryNamingTheFamilyKey()
    {
        InMemoryContentAuthoringStore store = NewStore();

        ContentFamily family = await store.CreateFamilyAsync(Game, "epic", 16, Actor, Operator);

        IReadOnlyList<ContentAuditEntry> audit = await store.ListAuditAsync(Game, 0, 0, 50);
        ContentAuditEntry entry = Assert.Single(audit);
        Assert.Equal(ContentAuditActions.FamilyCreate, entry.Action);
        Assert.Equal("epic", entry.Key.ToString());
        Assert.Equal(family.Blocks[0].BaseId.ToString(CultureInfo.InvariantCulture), entry.AfterValue);
    }

    [Fact]
    public async Task PinningAVersionTheStoreDoesNotHoldIsRefused()
    {
        InMemoryContentAuthoringStore store = NewStore();

        await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.SetPinnedVersionAsync(7, Actor, Operator));

        Assert.Null(await store.GetPinnedVersionAsync());
    }

    [Fact]
    public async Task ClearingAPinIsAlwaysAllowedBecauseNoPinIsTheOrdinaryState()
    {
        InMemoryContentAuthoringStore store = NewStore();

        await store.SetPinnedVersionAsync(null, Actor, Operator);

        Assert.Null(await store.GetPinnedVersionAsync());
        Assert.Equal(ContentAuditActions.Pin, (await store.ListAuditAsync(default, 0, 0, 50))[0].Action);
    }

    [Fact]
    public async Task AStoreThatHasPublishedNothingReadsAnEmptyRowPage()
    {
        InMemoryContentAuthoringStore store = NewStore();

        ContentRowPage page = await store.ListRowsAsync(Game, 0, null, includeRetired: true, 0, 50);

        Assert.Equal(InMemoryContentAuthoringStore.NoActiveVersion, page.VersionNumber);
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Rows);
        Assert.Empty(await store.GetRowHistoryAsync(Game, 1));
    }

    [Fact]
    public async Task APageReadIsClampedToTheImplementationsCap()
    {
        InMemoryContentAuthoringStore store = NewStore();
        for (int i = 0; i < InMemoryContentAuthoringStore.MaxPageSize + 10; i++)
        {
            await store.SetPinnedVersionAsync(null, Actor, Operator);
        }

        IReadOnlyList<ContentAuditEntry> audit = await store.ListAuditAsync(default, 0, 0, int.MaxValue);

        Assert.Equal(InMemoryContentAuthoringStore.MaxPageSize, audit.Count);
    }
}
