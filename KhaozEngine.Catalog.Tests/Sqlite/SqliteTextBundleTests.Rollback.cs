using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The SQLite companion rollback and the upgrade baseline over a version that declares a language: complete
/// value changes against the selected version in one transaction, every currently declared language kept,
/// retired rows rolled back without unretiring, the irreversible retirement blocker kept, and an upgrade run
/// planning over the format 2 baseline export.
/// </summary>
public sealed partial class SqliteTextBundleTests
{
    [Fact]
    public async Task A_text_rollback_restores_values_keeps_every_declared_language_and_survives_a_reopen()
    {
        using var database = new TemporaryCatalogDatabase();
        ContentVersionTextSnapshot before;
        using (SqliteContentAuthoringStore store = await OpenAsync(database))
        {
            await ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
            await store.PublishAsync(Request(0));
            await ApplyAsync(
                store,
                null,
                ContentTextEdit.Set(Target(NameField, "en"), "Blade"),
                ContentTextEdit.Set(Target(NameField, "fr"), "Lame"));
            await store.PublishAsync(Request(1));
            before = await store.ReadTextSnapshotAsync(2);

            ContentDraft draft = await store.RollbackTextToAsync(1, Actor, Operator, "roll back");

            Assert.Equal(0, draft.EditCount);
            Assert.Equal(
                new[] { (ContentTextEditOperation.Set, "en", "Sword"), (ContentTextEditOperation.Remove, "fr", (string?)null) },
                draft.TextState!.Edits.Select(edit => (edit.Operation, edit.Target.Language, edit.Value)));
            Assert.Empty(draft.TextState.Introductions);
            ContentAuditEntry latest = (await store.ListAuditAsync(default, 0, 0, 500))[0];
            Assert.Equal((ContentAuditActions.Rollback, "2", "1"), (latest.Action, latest.BeforeValue, latest.AfterValue));
        }

        using SqliteContentAuthoringStore reopened = await OpenAsync(database);
        Assert.Equal(2, (await reopened.GetOpenDraftAsync())!.TextEditCount);
        await reopened.PublishAsync(Request(2));
        ContentVersionTextSnapshot restored = await reopened.ReadTextSnapshotAsync(3);
        Assert.Equal("Sword", restored.Revisions.Single().Value);
        Assert.Equal(new[] { "en", "fr" }, restored.Languages.Select(language => language.WireTag));
        Assert.Equal(Hash("fr"), restored.Languages.Single(language => language.Language == "fr").Hash);
        ContentVersionTextSnapshot unchanged = await reopened.ReadTextSnapshotAsync(2);
        Assert.Equal(before.Languages, unchanged.Languages);
        Assert.Equal(
            before.Revisions.Select(revision => (revision.Language, revision.Value, revision.ValidFromVersion)),
            unchanged.Revisions.Select(revision => (revision.Language, revision.Value, revision.ValidFromVersion)));
        Assert.Single((await reopened.ReadTextSnapshotAsync(1)).Languages);
    }

    [Fact]
    public async Task A_row_added_after_the_target_keeps_its_text_through_a_text_rollback()
    {
        using var database = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore store = await OpenAsync(database);
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        await store.PublishAsync(Request(0));
        await ApplyAsync(
            text,
            new[] { Add("shield", 2) },
            ContentTextEdit.Set(Target(NameField, "en", "shield"), "Shield"),
            ContentTextEdit.Set(Target(NameField, "en"), "Blade"));
        await store.PublishAsync(Request(1));

        ContentDraft draft = await text.RollbackTextToAsync(1, Actor, Operator, "roll back");

        Assert.Equal(0, draft.EditCount);
        Assert.Equal(
            new[] { (ContentTextEditOperation.Set, "sword", (string?)"Sword") },
            draft.TextState!.Edits.Select(edit => (edit.Operation, edit.Target.Key.ToString(), edit.Value)));
        Assert.DoesNotContain(draft.TextState.Edits, edit => edit.Target.Key.Equals(new ContentKey("shield")));
    }

    [Fact]
    public async Task A_retired_row_rolls_back_its_text_and_stays_retired()
    {
        using var database = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore store = await OpenAsync(database);
        await ApplyAsync(
            store,
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
            store,
            null,
            ContentTextEdit.Set(Target(NameField, "en", "shield"), "Old Shield"),
            ContentTextEdit.Set(Target(NameField, "en", "sword"), "Blade"));
        await store.PublishAsync(Request(2));

        ContentDraft draft = await store.RollbackTextToAsync(2, Actor, Operator, "roll back");

        Assert.Equal(0, draft.EditCount);
        Assert.Equal(new[] { "Shield", "Sword" }, draft.TextState!.Edits.Select(edit => edit.Value));
        await store.PublishAsync(Request(3));
        Assert.True((await store.ListRowsAsync(Item, 4, "shield", true, 0, 1)).Rows.Single().IsRetired);
        Assert.Equal(
            new[] { "Shield", "Sword" },
            (await store.ReadTextSnapshotAsync(4)).Revisions.OrderBy(revision => revision.DefinitionId).Select(r => r.Value));
    }

    [Fact]
    public async Task A_row_retired_since_the_target_still_blocks_a_text_rollback_with_nothing_written()
    {
        using var database = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore store = await OpenAsync(database);
        await ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        await store.PublishAsync(Request(0));
        int sword = (await store.ListRowsAsync(Item, 1, null, true, 0, 1)).Rows.Single().Id;
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Retire(Item, sword, Sword, ContentRetirePolicy.Placeholder, 0) }, Actor, Operator, "retire");
        await ApplyAsync(store, null, ContentTextEdit.Set(Target(NameField, "en"), "Gone"));
        await store.PublishAsync(Request(1));
        int audits = await AuditCountAsync(store);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackTextToAsync(1, Actor, Operator, "roll back"));

        Assert.Equal(ContentAuthoringException.RetireIrreversibleReason, refused.Reason);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(audits, await AuditCountAsync(store));
        Assert.True((await store.ListRowsAsync(Item, 2, null, true, 0, 1)).Rows.Single().IsRetired);
        var missing = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackTextToAsync(9, Actor, Operator, "roll back"));
        Assert.Equal(ContentAuthoringException.UnknownVersionReason, missing.Reason);
        Assert.Equal(audits, await AuditCountAsync(store));
    }

    [Fact]
    public async Task An_upgrade_over_a_version_declaring_a_language_plans_on_the_format_two_baseline_and_applies()
    {
        using var database = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore store = await OpenAsync(database);
        await ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        await store.PublishAsync(Request(0));
        int seenFormat = 0;
        ContentBundleTextState? seen = null;
        var french = new ContentUpgradeDefinition("name-in-french", 1, "names the sword in french", context =>
        {
            seenFormat = context.Baseline.FormatVersion;
            seen = context.BaselineText;
            return ContentUpgradePlan.Changes(
                new ContentAuthoringChanges([], [ContentTextEdit.Set(Target(NameField, "fr"), "Epee")]),
                ["names the sword in french"]);
        });

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store,
            TextRegistry(),
            new ContentUpgradeSet(french),
            new ContentUpgradeOptions(ContentUpgradeMode.Apply, Actor, Operator, 0, 0));

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(2, Assert.Single(report.Steps).PublishedVersion);
        Assert.Equal(ContentBundle.TextFormatVersion, seenFormat);
        Assert.Equal(("en", "Sword"), seen!.Values.Select(value => (value.Target.Language, value.Value)).Single());
        ContentVersionTextSnapshot text = await store.ReadTextSnapshotAsync(2);
        Assert.Equal(new[] { "en", "fr" }, text.Languages.Select(language => language.WireTag));
        Assert.Equal("Epee", text.Revisions.Single(revision => revision.Language == "fr").Value);
        Assert.Equal(2, Assert.Single(await store.ListUpgradesAsync()).VersionNumber);
        Assert.Null(await store.GetOpenDraftAsync());
    }
}
