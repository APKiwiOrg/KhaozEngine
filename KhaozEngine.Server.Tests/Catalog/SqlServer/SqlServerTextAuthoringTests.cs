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
/// The SQL Server text authoring companion against a store a test reopens: full 8192-byte values, intent
/// order and pending introductions survive a reopen, recorded languages keep their historical wire spelling
/// and hashes through publication and recovery, a mixed batch rolls back whole on an audit or a clock fault,
/// and old DTOs and stale or forged plans are refused at the backend before any write.
/// <para>
/// ENV GATED on <c>KE_CATALOG_SQLSERVER</c> and in the serialized collection, because every fact starts by
/// dropping the catalog schema of the one test database.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public sealed class SqlServerTextAuthoringTests
{
    [CatalogSqlServerFact]
    public async Task A_reopened_draft_keeps_full_values_order_and_introductions_and_still_proves_ownership()
    {
        using var database = new SqlServerCatalogDatabase();
        string full = Utf8Value(ContentTextEdit.MaxValueBytes);

        // A clock with sub-millisecond ticks: the proof compares the stamp the store reads back, not the clock.
        DateTimeOffset now = new DateTimeOffset(2026, 10, 3, 1, 2, 3, TimeSpan.Zero).AddTicks(4321);
        SqlServerContentAuthoringStore store = await OpenAsync(database, () => now);
        await ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "EN-us"), "first"));
        await ApplyAsync(store, null, ContentTextEdit.Set(Target(DescriptionField, "fr"), "Lame"));
        await ApplyAsync(store, null, ContentTextEdit.Remove(Target(DescriptionField, "fr")));
        ContentDraft proof = await ApplyAsync(store, null, ContentTextEdit.Set(Target(NameField, "EN-US"), full));

        SqlServerContentAuthoringStore reopened = await OpenAsync(database, () => now);
        ContentDraft held = (await reopened.GetOpenDraftAsync())!;
        Assert.Equal(
            new[] { (NameField, "en-us", ContentTextEditOperation.Set), (DescriptionField, "fr", ContentTextEditOperation.Remove) },
            held.TextState!.Edits.Select(edit => (edit.Target.FieldName, edit.Target.Language, edit.Operation)));
        Assert.Equal(full, held.TextState.Edits[0].Value);
        Assert.Equal(new[] { "en-us", "fr" }, held.TextState.Introductions.Select(language => language.WireTag));
        Assert.True(held.TextState.IsSameAs(proof.TextState));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_draft_text_edit WHERE string_value IS NULL;"));

        Assert.True(await ((IContentTextAuthoringStore)reopened).TryDiscardChangesAsync(proof, Actor, Operator));
        Assert.Null(await reopened.GetOpenDraftAsync());
        Assert.Equal(0, database.Scalar(
            "SELECT (SELECT COUNT(*) FROM dbo.catalog_draft_text_edit) + (SELECT COUNT(*) FROM dbo.catalog_draft_text_language);"));
    }

    [CatalogSqlServerFact]
    public async Task A_full_value_publishes_whole_while_every_audit_of_it_is_abbreviated_on_a_whole_character()
    {
        using var database = new SqlServerCatalogDatabase();
        string full = Utf8Value(ContentTextEdit.MaxValueBytes);
        string revised = "x" + full[1..];
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        await ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), full));
        await store.PublishAsync(Request(0));
        await ApplyAsync(store, null, ContentTextEdit.Set(Target(NameField, "en"), revised));
        await store.PublishAsync(Request(1));

        SqlServerContentAuthoringStore reopened = await OpenAsync(database);
        IContentTextAuthoringStore text = reopened;
        Assert.Equal(full, Assert.Single((await text.ReadTextSnapshotAsync(1)).Revisions).Value);
        Assert.Equal(revised, Assert.Single((await text.ReadTextSnapshotAsync(2)).Revisions).Value);

        IReadOnlyList<ContentAuditEntry> trail = (await reopened.ListAuditAsync(default, 0, 0, 500))
            .Where(entry => entry.LanguageTag is not null).ToArray();
        Assert.Equal(4, trail.Count);
        foreach (string value in trail.SelectMany(entry => new[] { entry.BeforeValue, entry.AfterValue }).OfType<string>())
        {
            Assert.True(value.Length <= ContentAuditEntry.MaxValueLength);
            Assert.EndsWith(ContentTextAuditRendering.CutMarker, value, StringComparison.Ordinal);
            Assert.False(char.IsHighSurrogate(value[^(ContentTextAuditRendering.CutMarker.Length + 1)]));
        }

        ContentAuditEntry changed = trail.Single(entry => entry.Action == ContentAuditActions.Publish && entry.VersionNumber == 2);
        Assert.Equal(ContentTextAuditRendering.Render(full), changed.BeforeValue);
        Assert.Equal(ContentTextAuditRendering.Render(revised), changed.AfterValue);
        Assert.Equal("en", changed.LanguageTag);
    }

    [CatalogSqlServerFact]
    public async Task Published_text_reopens_with_its_values_empty_languages_and_recorded_hashes()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        await ApplyAsync(
            store,
            new[] { Add(), Add("shield", 2) },
            ContentTextEdit.Set(Target(NameField, "en"), "Sword"),
            ContentTextEdit.Set(Target(NameField, "en", "shield"), "Shield"),
            ContentTextEdit.Set(Target(NameField, "fr"), "Epee"),
            ContentTextEdit.Set(Target(DescriptionField, "de"), "Klinge"));
        await ApplyAsync(store, null, ContentTextEdit.Remove(Target(DescriptionField, "de")));
        await store.PublishAsync(Request(0));
        ContentVersionRecord record = (await store.GetVersionAsync(1))!;

        SqlServerContentAuthoringStore reopened = await OpenAsync(database);
        ContentVersionTextSnapshot text = await ((IContentTextAuthoringStore)reopened).ReadTextSnapshotAsync(1);
        Assert.Equal(
            new[]
            {
                new ContentTextLanguage("de", "de", Hash("de")),
                new ContentTextLanguage("en", "en", Hash("en", (ShieldName, "Shield"), (SwordName, "Sword"))),
                new ContentTextLanguage("fr", "fr", Hash("fr", (SwordName, "Epee"))),
            },
            text.Languages);
        Assert.Equal(new[] { "Epee", "Shield", "Sword" }, text.Revisions.Select(revision => revision.Value).Order(StringComparer.Ordinal));
        IReadOnlyList<ManifestLanguageEntry> manifest = await LanguagesAsync(database.Pack(), record);
        Assert.Equal(text.Languages.Select(language => (language.WireTag, language.Hash)), manifest.Select(entry => (entry.Tag, entry.TextHash)));
        Assert.Equal(1, database.Scalar("SELECT text_snapshot_complete FROM dbo.catalog_version WHERE version_number = 1;"));
    }

    [CatalogSqlServerFact]
    public async Task A_historical_wire_spelling_survives_reopen_publication_and_exact_recovery()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore seeded = await OpenAsync(database);
        await ApplyAsync(seeded, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en-us"), "Sword"));
        await seeded.PublishAsync(Request(0));

        // Version 1 recorded under the historical spelling a manifest of an earlier producer carried.
        KeyValuePair<string, string>[] entries = Pairs((SwordName, "Sword"));
        string historical = ContentTextChunkCodec.Hash("en-US", entries);
        await database.Pack().PutAsync(historical, ContentTextChunkCodec.Encode("en-US", entries));
        database.Execute(
            $"UPDATE dbo.catalog_text_chunk SET wire_tag = N'en-US', chunk_hash = N'{historical}' WHERE version_number = 1;");

        SqlServerContentAuthoringStore reopened = await OpenAsync(database);
        IContentTextAuthoringStore text = reopened;
        Assert.Equal(new ContentTextLanguage("en-us", "en-US", historical), Assert.Single((await text.ReadTextSnapshotAsync(1)).Languages));

        await ApplyAsync(text, new[] { Add("shield", 2) }, ContentTextEdit.Set(Target(NameField, "EN-us", "shield"), "Shield"));
        await reopened.PublishAsync(Request(1));
        ContentVersionRecord second = (await reopened.GetVersionAsync(2))!;
        string expected = Hash("en-US", (ShieldName, "Shield"), (SwordName, "Sword"));
        Assert.Equal(new ContentTextLanguage("en-us", "en-US", expected), Assert.Single((await text.ReadTextSnapshotAsync(2)).Languages));
        ManifestLanguageEntry named = Assert.Single(await LanguagesAsync(database.Pack(), second));
        Assert.Equal(("en-US", expected), (named.Tag, named.TextHash));

        // Recovery of version 2 into an empty pack regenerates the chunk in its recorded spelling and matches
        // both manifest hashes before it writes.
        var recovered = new FileSystemPackStore(System.IO.Path.Combine(database.Root, "recovered"));
        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(reopened, TextRegistry(), 2, recovered);
        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail);
        Assert.Equal("Shield", await ValueAsync(recovered, expected, ShieldName));
    }

    [CatalogSqlServerFact]
    public async Task A_text_audit_fault_rolls_the_mixed_batch_back_out_of_every_table()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        database.Execute(
            """
            CREATE TRIGGER catalog_audit_text_fault ON dbo.catalog_audit AFTER INSERT AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted WHERE language_tag IS NOT NULL)
                    THROW 51000, 'text audit fault', 1;
            END;
            """);

        await Assert.ThrowsAnyAsync<Exception>(
            () => ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "fr"), "Epee")));

        Assert.Equal(0, EveryDraftAndAuditRow(database));
        database.Execute("DROP TRIGGER catalog_audit_text_fault;");
        await ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "fr"), "Epee"));
        Assert.Equal(2, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_audit;"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_audit WHERE language_tag = N'fr';"));
    }

    [CatalogSqlServerFact]
    public async Task A_clock_failure_part_way_through_a_mixed_batch_leaves_nothing_behind()
    {
        using var database = new SqlServerCatalogDatabase();
        int reads = 0;
        int failAt = int.MaxValue;
        SqlServerContentAuthoringStore store = await OpenAsync(database, () =>
            ++reads >= failAt ? throw new InvalidOperationException("The clock failed, the injected fault.") : DateTimeOffset.UnixEpoch);

        // The draft open, the row edit and its audit read the clock before the text intent's first read, so the
        // fault lands after rows were written inside the transaction.
        reads = 0;
        failAt = 4;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(
            store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "fr"), "Epee"), ContentTextEdit.Set(Target(NameField, "de"), "Klinge")));
        Assert.True(reads >= 4);
        Assert.Equal(0, EveryDraftAndAuditRow(database));

        failAt = int.MaxValue;
        ContentDraft draft = await ApplyAsync(
            store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "fr"), "Epee"), ContentTextEdit.Set(Target(NameField, "de"), "Klinge"));
        Assert.Equal((1, 2, 2), (draft.EditCount, draft.TextEditCount, draft.LanguageIntroductionCount));
    }

    [CatalogSqlServerFact]
    public async Task A_frozen_draft_takes_no_write_of_any_kind_and_old_dtos_are_refused_at_confirmation()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        IContentTextAuthoringStore text = store;
        ContentDraft complete = await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        ContentTextPublishSnapshot snapshot = await text.FreezeChangesAsync(0);
        int audits = await AuditCountAsync(store);

        foreach (Func<Task> write in new Func<Task>[]
        {
            () => ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "en"), "late")),
            () => store.ApplyEditsAsync(new[] { Add("shield") }, Actor, Operator, "late"),
            () => store.DiscardDraftAsync(Actor, Operator),
        })
        {
            var refused = await Assert.ThrowsAsync<ContentAuthoringException>(write);
            Assert.Equal(ContentAuthoringException.PublishInProgressReason, refused.Reason);
        }

        Assert.False(await text.TryDiscardChangesAsync(snapshot.Draft, Actor, Operator));
        Assert.Equal(audits, await AuditCountAsync(store));
        await store.ClearDraftFreezeAsync();

        // The old constructor erases the text half, so the reconstruction proves nothing.
        var old = new ContentDraft(complete.BaseVersion, complete.OpenedBy, complete.OpenedAtUtc, complete.Note, complete.Changes);
        Assert.False(await text.TryDiscardChangesAsync(old, Actor, Operator));
        var legacy = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.DiscardDraftAsync(Actor, Operator));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, legacy.Reason);
        var freeze = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.FreezeDraftAsync(0));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, freeze.Reason);
        Assert.Equal("Sword", Assert.Single((await store.GetOpenDraftAsync())!.TextState!.Edits).Value);
        Assert.Equal(audits, await AuditCountAsync(store));
    }

    [CatalogSqlServerFact]
    public async Task A_supplied_plan_is_confirmed_against_draft_and_chunks_before_any_write()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en-us"), "Sword"));
        ContentTextChunkRecord real = Chunk("en-us", ("sword", "Sword"));
        ContentTextChunkRecord other = Chunk("en-us", ("sword", "Blade"));
        ContentTextPublishPlan plan = await PlanAsync(store, real);
        ContentTextPublishPlan lying = await PlanAsync(store, other);
        ContentTextPublishPlan forged = await PlanAsync(store, new ContentTextChunkRecord(real.WireTag, real.Hash, other.StoredFile, false));
        int audits = await AuditCountAsync(store);

        // The row half handed to the row-only commit is refused.
        var rowHalf = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.CommitPublishAsync(plan.RowPlan, Request(0), null));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, rowHalf.Reason);

        // A chunk that is not the plan's own values, and one with the right hash over other bytes, are refused.
        foreach (ContentTextPublishPlan wrong in new[] { lying, forged })
        {
            var mismatch = await Assert.ThrowsAsync<ContentAuthoringException>(() => text.CommitTextPublishAsync(wrong, Request(0), null));
            Assert.Equal(ContentAuthoringException.TextChunkMismatchReason, mismatch.Reason);
        }

        // A rival intent landing after the freeze makes the plan name a draft the store no longer holds.
        await store.ClearDraftFreezeAsync();
        await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "fr"), "Epee"));
        await text.FreezeChangesAsync(0);
        var stale = await Assert.ThrowsAsync<ContentAuthoringException>(() => text.CommitTextPublishAsync(plan, Request(0), null));
        Assert.Equal(ContentAuthoringException.TextStateMismatchReason, stale.Reason);

        Assert.Empty(await store.ListVersionsAsync());
        Assert.Equal(audits + 1, await AuditCountAsync(store));
        Assert.Equal(0, database.Scalar("SELECT (SELECT COUNT(*) FROM dbo.catalog_text) + (SELECT COUNT(*) FROM dbo.catalog_text_chunk);"));
        Assert.Equal(2, (await store.GetOpenDraftAsync())!.TextEditCount);
    }

    [CatalogSqlServerFact]
    public async Task A_confirmed_plan_commits_its_text_and_consumes_exactly_the_intents_it_froze()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en-us"), "Sword"));
        ContentTextChunkRecord chunk = Chunk("en-us", ("sword", "Sword"));

        await text.CommitTextPublishAsync(await PlanAsync(store, chunk), Request(0), null);

        ContentVersionTextSnapshot published = await text.ReadTextSnapshotAsync(1);
        Assert.Equal("Sword", Assert.Single(published.Revisions).Value);
        Assert.Equal(chunk.Hash, Assert.Single(published.Languages).Hash);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(1, database.Scalar("SELECT text_snapshot_complete FROM dbo.catalog_version WHERE version_number = 1;"));
    }

    [CatalogSqlServerFact]
    public async Task A_marker_name_over_the_field_name_column_is_a_typed_refusal_not_a_constraint_error()
    {
        using var database = new SqlServerCatalogDatabase();
        string longest = new('n', 64);
        string over = new('n', 65);
        SqlServerContentAuthoringStore store = await OpenAsync(database, registry: MarkerRegistry(longest, over));
        IContentTextAuthoringStore text = store;

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Target(over, "en"), "Sword")));
        Assert.Equal(ContentAuthoringException.TextBoundsReason, refused.Reason);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(0, await AuditCountAsync(store));

        ContentDraft held = await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Target(longest, "en"), "Sword"));
        Assert.Equal(longest, Assert.Single(held.TextState!.Edits).Target.FieldName);
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_draft_text_edit;"));
    }

    [CatalogSqlServerFact]
    public async Task Values_differing_only_in_trailing_blanks_stay_two_values_through_publish_and_close()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        await store.PublishAsync(Request(0));
        await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "en"), "Sword "));
        await store.PublishAsync(Request(1));

        Assert.Equal("Sword", Assert.Single((await text.ReadTextSnapshotAsync(1)).Revisions).Value);
        Assert.Equal("Sword ", Assert.Single((await text.ReadTextSnapshotAsync(2)).Revisions).Value);
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_text WHERE replaced_in_version = 2;"));
        Assert.Null(await store.GetOpenDraftAsync());
    }

    /// <summary>Every row of the draft, intent, introduction and audit tables, which a refused batch leaves at zero.</summary>
    static int EveryDraftAndAuditRow(SqlServerCatalogDatabase database)
        => database.Scalar(
            """
            SELECT (SELECT COUNT(*) FROM dbo.catalog_draft) + (SELECT COUNT(*) FROM dbo.catalog_draft_edit)
                 + (SELECT COUNT(*) FROM dbo.catalog_draft_text_edit) + (SELECT COUNT(*) FROM dbo.catalog_draft_text_language)
                 + (SELECT COUNT(*) FROM dbo.catalog_audit);
            """);
}
