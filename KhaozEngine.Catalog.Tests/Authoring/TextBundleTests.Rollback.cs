using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Authoring;

/// <summary>
/// The companion rollback through the reference store: complete value changes against the selected version,
/// every currently declared language retained, retired rows rolled back without unretiring, and the
/// irreversible retirement blocker kept.
/// </summary>
public sealed partial class TextBundleTests
{
    [Fact]
    public async Task A_text_rollback_restores_values_and_keeps_every_currently_declared_language()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = TextStore(files.Pack());
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        await store.PublishAsync(Request(0));
        await ApplyAsync(
            text,
            null,
            ContentTextEdit.Set(Target(NameField, "en"), "Blade"),
            ContentTextEdit.Set(Target(NameField, "fr"), "Lame"));
        await store.PublishAsync(Request(1));
        ContentVersionTextSnapshot before = await text.ReadTextSnapshotAsync(2);

        ContentDraft draft = await text.RollbackTextToAsync(1, Actor, Operator, "roll back");

        Assert.Equal(0, draft.EditCount);
        Assert.Equal(
            new[] { (ContentTextEditOperation.Set, "en", "Sword"), (ContentTextEditOperation.Remove, "fr", (string?)null) },
            draft.TextState!.Edits.Select(edit => (edit.Operation, edit.Target.Language, edit.Value)));
        Assert.Empty(draft.TextState.Introductions);
        Assert.Contains(
            await store.ListAuditAsync(default, 0, 0, 500),
            entry => entry.Action == ContentAuditActions.Rollback && entry.AfterValue == "1");

        await store.PublishAsync(Request(2));
        ContentVersionTextSnapshot restored = await text.ReadTextSnapshotAsync(3);
        Assert.Equal("Sword", restored.Revisions.Single().Value);
        Assert.Equal(new[] { "en", "fr" }, restored.Languages.Select(language => language.WireTag));
        Assert.Equal(ContentTextChunkCodec.Hash("fr", []), restored.Languages.Single(l => l.Language == "fr").Hash);
        ContentVersionTextSnapshot unchanged = await text.ReadTextSnapshotAsync(2);
        Assert.Equal(before.Languages, unchanged.Languages);
        Assert.Equal(
            before.Revisions.Select(revision => (revision.Language, revision.Value, revision.ValidFromVersion)),
            unchanged.Revisions.Select(revision => (revision.Language, revision.Value, revision.ValidFromVersion)));
        Assert.Single((await text.ReadTextSnapshotAsync(1)).Languages);
    }

    [Fact]
    public async Task A_retired_row_rolls_back_its_text_and_stays_retired()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = TextStore(files.Pack());
        IContentTextAuthoringStore text = store;
        await ApplyAsync(
            text,
            new[] { Add("shield"), Add("sword") },
            ContentTextEdit.Set(Target(NameField, "en", "shield"), "Shield"),
            ContentTextEdit.Set(Target(NameField, "en", "sword"), "Sword"));
        await store.PublishAsync(Request(0));
        int shield = (await store.ListRowsAsync(Item, 1, "shield", true, 0, 1)).Rows.Single().Id;
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Retire(Item, shield, new ContentKey("shield"), ContentRetirePolicy.Placeholder, 0) },
            Actor,
            Operator,
            "retire");
        await store.PublishAsync(Request(1));
        await ApplyAsync(
            text,
            null,
            ContentTextEdit.Set(Target(NameField, "en", "shield"), "Old Shield"),
            ContentTextEdit.Set(Target(NameField, "en", "sword"), "Blade"));
        await store.PublishAsync(Request(2));

        ContentDraft draft = await text.RollbackTextToAsync(2, Actor, Operator, "roll back");

        Assert.Equal(0, draft.EditCount);
        Assert.Equal(new[] { "Shield", "Sword" }, draft.TextState!.Edits.Select(edit => edit.Value));
        await store.PublishAsync(Request(3));
        Assert.True((await store.ListRowsAsync(Item, 4, "shield", true, 0, 1)).Rows.Single().IsRetired);
        Assert.Equal(
            new[] { "Shield", "Sword" },
            (await text.ReadTextSnapshotAsync(4)).Revisions.OrderBy(revision => revision.DefinitionId).Select(r => r.Value));
    }

    [Fact]
    public async Task A_row_retired_since_the_target_still_blocks_a_text_rollback()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = TextStore(files.Pack());
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        await store.PublishAsync(Request(0));
        int sword = (await store.ListRowsAsync(Item, 1, null, true, 0, 1)).Rows.Single().Id;
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Retire(Item, sword, Sword, ContentRetirePolicy.Placeholder, 0) }, Actor, Operator, "retire");
        await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "en"), "Gone"));
        await store.PublishAsync(Request(1));
        int audits = await AuditCountAsync(store);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => text.RollbackTextToAsync(1, Actor, Operator, "roll back"));

        Assert.Equal(ContentAuthoringException.RetireIrreversibleReason, refused.Reason);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(audits, await AuditCountAsync(store));
        Assert.True((await store.ListRowsAsync(Item, 2, null, true, 0, 1)).Rows.Single().IsRetired);
        var legacy = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(1, Actor, Operator, "roll back"));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, legacy.Reason);
        var missing = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => text.RollbackTextToAsync(9, Actor, Operator, "roll back"));
        Assert.Equal(ContentAuthoringException.UnknownVersionReason, missing.Reason);
    }
}
