using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// The rollback of spec 6.13, which is a PUBLISH and not a special case: it builds a draft of ordinary
/// updates restoring an earlier version's field values, and an operator reviews the diff and publishes it.
/// <para>
/// Two properties carry the whole section. The version number keeps CLIMBING, so a rollback is never a
/// return to an old number, which is what lets a durable page's version stamp be an ordering comparison. And
/// a row introduced after the target keeps its id and its values, which is the difference between a rollback
/// and a restore.
/// </para>
/// <para>
/// <b>A retire is a flat refusal.</b> There is no un-retire branch and there never was a reachable one: every
/// retire appends exactly one kind 2 rule, so a branch conditioned on "no rule names that id" could not run.
/// </para>
/// </summary>
public class RollbackTests
{
    /// <summary>The code a blocked rollback carries, its own rather than a second meaning for another.</summary>
    const string BlockedCode = "KEC0039";

    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    static ContentEdit SetValue(int id, string key, int value)
        => ContentEdit.Update(
            Thing,
            id,
            new ContentKey(key),
            [new ContentFieldEdit(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, value))]);

    [Fact]
    public async Task ARollbackBuildsADraftOfUpdatesAndPublishesNothingItself()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(10)));
        await store.PublishAsync(PublishFixtures.Request(0));

        await PublishFixtures.ApplyAsync(store, SetValue(1, "one", 99));
        await store.PublishAsync(PublishFixtures.Request(1));

        ContentDraft draft = await store.RollbackToAsync(1, PublishFixtures.Actor, "oid:tests", "undo the pass");

        // The rollback wrote a DRAFT and moved nothing. The operator reviews the diff and publishes it.
        Assert.Equal(2, await store.GetActiveVersionAsync());
        ContentEdit edit = Assert.Single(draft.Changes.Edits);
        Assert.Equal(ContentEditOperation.Update, edit.Operation);
        Assert.Equal(1, edit.DefinitionId);
        Assert.Equal(10, Assert.Single(edit.Fields).Value.Number);

        ContentPublishResult published = await store.PublishAsync(PublishFixtures.Request(2));

        // The number KEEPS CLIMBING. A rollback is never a return to an old number.
        Assert.Equal(3, published.VersionNumber);
        ContentRowPage page = await store.ListRowsAsync(Thing, 0, null, true, 0, 50);
        Assert.Equal(10, Assert.Single(page.Rows).Fields[0].Number);
    }

    [Fact]
    public async Task ARowIntroducedAfterTheTargetKeepsItsIdAndItsValues()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(10)));
        await store.PublishAsync(PublishFixtures.Request(0));

        await PublishFixtures.ApplyAsync(
            store,
            SetValue(1, "one", 99),
            ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(20)));
        await store.PublishAsync(PublishFixtures.Request(1));

        ContentDraft draft = await store.RollbackToAsync(1, PublishFixtures.Actor, "oid:tests", "undo the pass");

        // Nothing at all is emitted for row 2, which did not exist at the target.
        ContentEdit edit = Assert.Single(draft.Changes.Edits);
        Assert.Equal(1, edit.DefinitionId);

        await store.PublishAsync(PublishFixtures.Request(2));

        ContentRowPage page = await store.ListRowsAsync(Thing, 0, null, true, 0, 50);
        Assert.Equal(2, page.Total);
        Assert.Equal(10, page.Rows.Single(row => row.Id == 1).Fields[0].Number);
        Assert.Equal(20, page.Rows.Single(row => row.Id == 2).Fields[0].Number);
    }

    [Fact]
    public async Task ARollbackOverAVersionThatChangedNothingEmitsNoEdit()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(10)));
        await store.PublishAsync(PublishFixtures.Request(0));

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(20)));
        await store.PublishAsync(PublishFixtures.Request(1));

        ContentDraft draft = await store.RollbackToAsync(1, PublishFixtures.Actor, "oid:tests", "nothing to undo");

        Assert.Equal(0, draft.EditCount);
    }

    [Fact]
    public async Task ARowRetiredSinceTheTargetRefusesTheRollbackAndNamesTheRuleThatDidIt()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("sword"), PublishFixtures.Fields(10)));
        await store.PublishAsync(PublishFixtures.Request(0));

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Retire(Thing, 1, new ContentKey("sword"), ContentRetirePolicy.Placeholder, 0));
        await store.PublishAsync(PublishFixtures.Request(1));

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(1, PublishFixtures.Actor, "oid:tests", "undo the retire"));

        Assert.Equal(ContentAuthoringException.RetireIrreversibleReason, refused.Reason);
        ContentFinding finding = Assert.Single(refused.Findings);
        Assert.Equal(BlockedCode, finding.Code);
        Assert.Equal(Thing, finding.Type);
        Assert.Equal(1, finding.Id);

        // The message names the rule that retired it and the way out, so the operator is not left guessing.
        Assert.Contains("rule 1", finding.Message, StringComparison.Ordinal);
        Assert.Contains("version 2", finding.Message, StringComparison.Ordinal);
        Assert.Contains(ContentRollback.Remedy, finding.Message, StringComparison.Ordinal);

        // Nothing was written. A refused rollback opens no draft.
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(2, await store.GetActiveVersionAsync());
    }

    [Fact]
    public async Task TheUnretireBranchIsNotReachableBecauseEveryRetireAppendsAKind2Rule()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("sword"), PublishFixtures.Fields(10)),
            ContentEdit.Add(Thing, new ContentKey("shield"), PublishFixtures.Fields(11)));
        await store.PublishAsync(PublishFixtures.Request(0));

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Retire(Thing, 1, new ContentKey("sword"), ContentRetirePolicy.Replacement, 2));
        await store.PublishAsync(PublishFixtures.Request(1));

        // Every retired row is named by a rule, which is why a branch conditioned on "no rule names that id"
        // could not run and why the refusal is flat.
        RemapRule rule = Assert.Single(store.Rules);
        Assert.Equal(RemapRuleKind.Retired, rule.Kind);
        Assert.Equal(1, rule.FromId);

        await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(1, PublishFixtures.Actor, "oid:tests", "undo the retire"));
    }

    [Fact]
    public async Task TheWayOutOfABlockedRollbackIsAnOrdinaryAddUnderANewKeyCarryingTheOldValues()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("sword"), PublishFixtures.Fields(10)));
        await store.PublishAsync(PublishFixtures.Request(0));

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Retire(Thing, 1, new ContentKey("sword"), ContentRetirePolicy.Placeholder, 0));
        await store.PublishAsync(PublishFixtures.Request(1));

        await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(1, PublishFixtures.Actor, "oid:tests", "undo the retire"));

        // A key is immutable once published, so the old values come back under a NEW key on a NEW id. The
        // retired row keeps its id and its bytes forever, which is what makes a stored stack still decode.
        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("sword_restored"), PublishFixtures.Fields(10)));
        ContentPublishResult published = await store.PublishAsync(PublishFixtures.Request(2));

        Assert.Equal(3, published.VersionNumber);
        ContentRowPage page = await store.ListRowsAsync(Thing, 0, null, true, 0, 50);
        Assert.Equal(2, page.Total);
        Assert.True(page.Rows.Single(row => row.Id == 1).IsRetired);

        ContentRow restored = page.Rows.Single(row => row.Id == 2);
        Assert.Equal("sword_restored", restored.Key.ToString());
        Assert.Equal(10, restored.Fields[0].Number);
        Assert.False(restored.IsRetired);
    }

    [Fact]
    public async Task ARollbackToAVersionTheStoreDoesNotHoldIsRefused()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(10)));
        await store.PublishAsync(PublishFixtures.Request(0));

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(47, PublishFixtures.Actor, "oid:tests", "undo"));

        Assert.Equal(ContentAuthoringException.UnknownVersionReason, refused.Reason);
    }

    [Fact]
    public async Task ARollbackWritesOneAuditRowNamingBothVersions()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(10)));
        await store.PublishAsync(PublishFixtures.Request(0));

        await PublishFixtures.ApplyAsync(store, SetValue(1, "one", 99));
        await store.PublishAsync(PublishFixtures.Request(1));

        await store.RollbackToAsync(1, PublishFixtures.Actor, "oid:tests", "undo the pass");

        ContentAuditEntry entry = (await store.ListAuditAsync(default, 0, 0, 100))
            .First(one => string.Equals(one.Action, ContentAuditActions.Rollback, StringComparison.Ordinal));

        Assert.Equal("2", entry.BeforeValue);
        Assert.Equal("1", entry.AfterValue);
        Assert.Equal("undo the pass", entry.Note);
    }
}
