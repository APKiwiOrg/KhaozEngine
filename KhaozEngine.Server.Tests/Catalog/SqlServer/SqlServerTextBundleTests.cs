using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Xunit;
using static KhaozEngine.Tests.Catalog.SqlServer.SqlServerTextFixtures;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The SQL Server companion import, text rollback and upgrade baseline beyond the shared conformance facts:
/// an open draft holding any work refuses a companion import with nothing staged, a failure after staging
/// leaves the database empty of rows, text and draft text and a retry imports whole, a row added after the
/// rollback target keeps its text, and an upgrade over a version declaring a language plans on the format 2
/// baseline export.
/// <para>
/// ENV GATED on <c>KE_CATALOG_SQLSERVER</c> and in the serialized collection, because every fact starts by
/// dropping the catalog schema of the one test database.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public sealed class SqlServerTextBundleTests
{
    /// <summary>Every row an import may stage or a text publish may write, summed.</summary>
    const string StagedRows = """
        SELECT (SELECT COUNT(*) FROM dbo.catalog_draft_edit) + (SELECT COUNT(*) FROM dbo.catalog_draft_text_edit)
             + (SELECT COUNT(*) FROM dbo.catalog_draft_text_language) + (SELECT COUNT(*) FROM dbo.catalog_text)
             + (SELECT COUNT(*) FROM dbo.catalog_text_chunk) + (SELECT COUNT(*) FROM dbo.catalog_row)
             + (SELECT COUNT(*) FROM dbo.catalog_version);
        """;

    [CatalogSqlServerFact]
    public async Task An_open_draft_holding_any_work_refuses_a_companion_import_with_nothing_staged()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        ContentDraft pending = await ApplyAsync(
            store, new[] { Add("axe") }, ContentTextEdit.Set(Target(NameField, "fr", "axe"), "Hache"));
        int audits = await AuditCountAsync(store);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ImportTextBundleAsync(FamilySeed(), Actor, Operator, "seed"));

        Assert.Equal(ContentAuthoringException.DraftOpenReason, refused.Reason);
        ContentDraft held = (await store.GetOpenDraftAsync())!;
        Assert.Equal(pending.EditCount, held.EditCount);
        Assert.Equal("axe", held.Changes.Edits.Single().Key.ToString());
        Assert.True(pending.TextState!.IsSameAs(held.TextState));
        Assert.Equal(1, held.LanguageIntroductionCount);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Empty(await store.ListFamiliesAsync(Item));
        Assert.Equal(0, (await store.ReadHighWaterAsync(Item)).ReservedThrough);
        Assert.Equal(audits, await AuditCountAsync(store));
    }

    [CatalogSqlServerFact]
    public async Task A_failure_after_staging_on_the_text_route_leaves_the_database_empty_and_a_retry_imports_whole()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        ContentBundle seed = FamilySeed();
        database.Execute(
            """
            CREATE TRIGGER catalog_text_chunk_fault ON dbo.catalog_text_chunk AFTER INSERT AS
            BEGIN
                THROW 51000, 'text chunk fault', 1;
            END;
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => store.ImportTextBundleAsync(seed, Actor, Operator, "seed"));

        Assert.Empty(await store.ListVersionsAsync());
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Empty(await store.ListFamiliesAsync(Item));
        Assert.Equal(0, (await store.ReadHighWaterAsync(Item)).ReservedThrough);
        Assert.Equal(0, database.Scalar(StagedRows));

        database.Execute("DROP TRIGGER catalog_text_chunk_fault;");
        Assert.Equal(1, (await store.ImportTextBundleAsync(seed, Actor, Operator, "retry")).VersionNumber);
        ContentVersionTextSnapshot text = await store.ReadTextSnapshotAsync(1);
        Assert.Equal(new[] { "en", "fr" }, text.Languages.Select(language => language.WireTag));
        Assert.Equal("Blade", text.Revisions.Single(revision => revision.DefinitionId == 16).Value);
        Assert.Equal(16, Assert.Single(await store.ListFamiliesAsync(Item)).Blocks.Single().BaseId);
        Assert.Equal(0, database.Scalar(
            "SELECT (SELECT COUNT(*) FROM dbo.catalog_draft_text_edit) + (SELECT COUNT(*) FROM dbo.catalog_draft_text_language);"));
    }

    [CatalogSqlServerFact]
    public async Task A_row_added_after_the_target_keeps_its_text_through_a_text_rollback()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
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
        ContentAuditEntry latest = (await store.ListAuditAsync(default, 0, 0, 500))[0];
        Assert.Equal((ContentAuditActions.Rollback, "2", "1"), (latest.Action, latest.BeforeValue, latest.AfterValue));
    }

    [CatalogSqlServerFact]
    public async Task An_upgrade_over_a_version_declaring_a_language_plans_on_the_format_two_baseline_and_applies()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
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

    /// <summary>A seed carrying a family whose block holds one named row, beside a plain row and an empty language.</summary>
    static ContentBundle FamilySeed()
        => new(
            ContentBundle.TextFormatVersion,
            "seed",
            0,
            Types(),
            [Row("sword", 3), new ContentBundleRow(Item, 16, new ContentKey("blade"), false, "swords", [Value(16)])],
            [new ContentFamily(7, Item, "swords", 16, false, 1, [new ContentFamilyBlock(7, 0, 16, 16, 17, 1)])],
            Array.Empty<RemapRule>(),
            new ContentBundleTextState(
                [new ContentTextLanguageDeclaration("en", "en"), new ContentTextLanguageDeclaration("fr", "fr")],
                [
                    new ContentBundleTextValue(Target(NameField, "en"), "Sword"),
                    new ContentBundleTextValue(Target(NameField, "en", "blade"), "Blade"),
                ]));

    static IReadOnlyList<ContentBundleType> Types()
        => TextRegistry().ByTypeId
            .Select(registration => new ContentBundleType(
                registration.Type,
                registration.TypeKey,
                registration.DefaultVisibility,
                registration.ChunkSlots,
                registration.MaxDefinitionId,
                registration.Schema))
            .ToArray();

    static ContentBundleRow Row(string key, int id)
        => new(Item, id, new ContentKey(key), false, null, new[] { Value(id) });
}
