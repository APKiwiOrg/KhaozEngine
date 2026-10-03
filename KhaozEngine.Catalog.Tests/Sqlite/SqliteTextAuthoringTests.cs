using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The SQLite text authoring companion against FILE databases a test closes and reopens: full values, intent
/// order and pending introductions survive a reopen, recorded languages keep their historical wire spelling
/// and hashes through publication and recovery, a mixed batch rolls back whole, old DTOs and stale plans are
/// refused at the backend, and the expected-draft discard serializes a rival without any timing.
/// </summary>
public sealed class SqliteTextAuthoringTests
{
    static async Task<SqliteContentAuthoringStore> OpenAsync(
        TemporaryCatalogDatabase database, Func<DateTimeOffset>? clock = null)
    {
        var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack(), clock);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        return store;
    }

    [Fact]
    public async Task A_reopened_draft_keeps_full_values_order_and_introductions_and_still_proves_ownership()
    {
        using var database = new TemporaryCatalogDatabase();
        string full = Utf8Value(ContentTextEdit.MaxValueBytes);

        // A clock with sub-millisecond ticks: the proof compares the stamp the store reads back, not the clock.
        DateTimeOffset now = new DateTimeOffset(2026, 10, 3, 1, 2, 3, TimeSpan.Zero).AddTicks(4321);
        ContentDraft proof;
        using (SqliteContentAuthoringStore store = await OpenAsync(database, () => now))
        {
            await ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Name, "first"));
            await ApplyAsync(store, null, ContentTextEdit.Set(Target(DescriptionField, "fr"), "Lame"));
            await ApplyAsync(store, null, ContentTextEdit.Remove(Target(DescriptionField, "fr")));
            proof = await ApplyAsync(store, null, ContentTextEdit.Set(Target(NameField, "EN-US"), full));
        }

        using SqliteContentAuthoringStore reopened = await OpenAsync(database, () => now);
        ContentDraft held = (await reopened.GetOpenDraftAsync())!;
        Assert.Equal(
            new[] { (NameField, "en-us", ContentTextEditOperation.Set), (DescriptionField, "fr", ContentTextEditOperation.Remove) },
            held.TextState!.Edits.Select(edit => (edit.Target.FieldName, edit.Target.Language, edit.Operation)));
        Assert.Equal(full, held.TextState.Edits[0].Value);
        Assert.Equal(new[] { "en-us", "fr" }, held.TextState.Introductions.Select(language => language.WireTag));
        Assert.True(held.TextState.IsSameAs(proof.TextState));

        Assert.True(await ((IContentTextAuthoringStore)reopened).TryDiscardChangesAsync(proof, Actor, Operator));
        Assert.Null(await reopened.GetOpenDraftAsync());
        Assert.Equal(0L, database.Scalar(
            "SELECT (SELECT COUNT(*) FROM catalog_draft_text_edit) + (SELECT COUNT(*) FROM catalog_draft_text_language);"));
    }

    [Fact]
    public async Task Published_text_reopens_with_its_values_empty_languages_and_recorded_hashes()
    {
        using var database = new TemporaryCatalogDatabase();
        ContentVersionRecord record;
        using (SqliteContentAuthoringStore store = await OpenAsync(database))
        {
            await ApplyAsync(
                store,
                new[] { Add(), Add("shield", 2) },
                ContentTextEdit.Set(Target(NameField, "en"), "Sword"),
                ContentTextEdit.Set(Target(NameField, "en", "shield"), "Shield"),
                ContentTextEdit.Set(Target(NameField, "fr"), "Epee"),
                ContentTextEdit.Set(Target(DescriptionField, "de"), "Klinge"));
            await ApplyAsync(store, null, ContentTextEdit.Remove(Target(DescriptionField, "de")));
            record = await PublishAsync(store);
        }

        using SqliteContentAuthoringStore reopened = await OpenAsync(database);
        ContentVersionTextSnapshot text = await ((IContentTextAuthoringStore)reopened).ReadTextSnapshotAsync(1);
        Assert.Equal(
            new[]
            {
                new ContentTextLanguage("de", "de", TextPublishFixtures.Hash("de")),
                new ContentTextLanguage("en", "en", TextPublishFixtures.Hash(
                    "en", (TextPublishFixtures.ShieldName, "Shield"), (TextPublishFixtures.SwordName, "Sword"))),
                new ContentTextLanguage("fr", "fr", TextPublishFixtures.Hash("fr", (TextPublishFixtures.SwordName, "Epee"))),
            },
            text.Languages);
        Assert.Equal(
            new[] { "Epee", "Shield", "Sword" },
            text.Revisions.Select(revision => revision.Value).Order(StringComparer.Ordinal));
        IReadOnlyList<ManifestLanguageEntry> manifest = await TextPublishFixtures.LanguagesAsync(database.Pack(), record);
        Assert.Equal(text.Languages.Select(language => (language.WireTag, language.Hash)), manifest.Select(entry => (entry.Tag, entry.TextHash)));
        Assert.Equal(1L, database.Scalar("SELECT text_snapshot_complete FROM catalog_version WHERE version_number = 1;"));
    }

    [Fact]
    public async Task A_historical_wire_spelling_survives_reopen_publication_and_exact_recovery()
    {
        using var database = new TemporaryCatalogDatabase();
        using (SqliteContentAuthoringStore store = await OpenAsync(database))
        {
            await ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en-us"), "Sword"));
            await PublishAsync(store);
        }

        // Version 1 recorded under the historical spelling a manifest of an earlier producer carried.
        KeyValuePair<string, string>[] entries = TextPublishFixtures.Pairs((TextPublishFixtures.SwordName, "Sword"));
        string historical = ContentTextChunkCodec.Hash("en-US", entries);
        await database.Pack().PutAsync(historical, ContentTextChunkCodec.Encode("en-US", entries));
        database.Execute(
            $"UPDATE catalog_text_chunk SET wire_tag = 'en-US', chunk_hash = '{historical}' WHERE version_number = 1;");

        using SqliteContentAuthoringStore reopened = await OpenAsync(database);
        IContentTextAuthoringStore text = reopened;
        Assert.Equal(new ContentTextLanguage("en-us", "en-US", historical), Assert.Single((await text.ReadTextSnapshotAsync(1)).Languages));

        await ApplyAsync(text, new[] { Add("shield", 2) }, ContentTextEdit.Set(Target(NameField, "EN-us", "shield"), "Shield"));
        ContentVersionRecord second = await PublishAsync(reopened);
        string expected = TextPublishFixtures.Hash(
            "en-US", (TextPublishFixtures.ShieldName, "Shield"), (TextPublishFixtures.SwordName, "Sword"));
        Assert.Equal(new ContentTextLanguage("en-us", "en-US", expected), Assert.Single((await text.ReadTextSnapshotAsync(2)).Languages));
        ManifestLanguageEntry named = Assert.Single(await TextPublishFixtures.LanguagesAsync(database.Pack(), second));
        Assert.Equal(("en-US", expected), (named.Tag, named.TextHash));

        // Recovery of version 2 into an empty pack regenerates the chunk in its recorded spelling and matches
        // both manifest hashes before it writes.
        using var recovered = new TemporaryCatalogDatabase();
        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(reopened, TextRegistry(), 2, recovered.Pack());
        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail);
        Assert.True(await recovered.Pack().ExistsAsync(expected));
        Assert.Equal("Shield", TextPublishFixtures.Value(
            await TextPublishFixtures.TextAsync(recovered.Pack(), expected), TextPublishFixtures.ShieldName));
    }

    [Fact]
    public async Task A_text_audit_fault_rolls_the_mixed_batch_back_out_of_every_table()
    {
        using var database = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore store = await OpenAsync(database);
        database.Execute(
            """
            CREATE TRIGGER catalog_audit_text_fault BEFORE INSERT ON catalog_audit
            WHEN NEW.language_tag IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT, 'text audit fault');
            END;
            """);

        await Assert.ThrowsAnyAsync<Exception>(
            () => ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "fr"), "Epee")));

        Assert.Equal(0L, database.Scalar(
            """
            SELECT (SELECT COUNT(*) FROM catalog_draft) + (SELECT COUNT(*) FROM catalog_draft_edit)
                 + (SELECT COUNT(*) FROM catalog_draft_text_edit) + (SELECT COUNT(*) FROM catalog_draft_text_language)
                 + (SELECT COUNT(*) FROM catalog_audit);
            """));
        database.Execute("DROP TRIGGER catalog_audit_text_fault;");
        await ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "fr"), "Epee"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalog_audit;"));
    }

    [Fact]
    public async Task A_frozen_draft_takes_no_write_of_any_kind_and_old_dtos_are_refused_at_confirmation()
    {
        using var database = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore store = await OpenAsync(database);
        IContentTextAuthoringStore text = store;
        ContentDraft complete = await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        ContentTextPublishSnapshot snapshot = await text.FreezeChangesAsync(0);
        int audits = await AuditCountAsync(store);

        foreach (Func<Task> write in new Func<Task>[]
        {
            () => ApplyAsync(text, null, ContentTextEdit.Set(Name, "late")),
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
        Assert.Equal("Sword", Assert.Single((await store.GetOpenDraftAsync())!.TextState!.Edits).Value);
        Assert.Equal(audits, await AuditCountAsync(store));
    }

    [Fact]
    public async Task A_supplied_plan_is_confirmed_against_epoch_draft_and_chunks_before_any_write()
    {
        using var database = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore store = await OpenAsync(database);
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        ContentTextChunkRecord chunk = Chunk("en-us", ("sword", "Sword"));
        ContentTextChunkRecord wrong = Chunk("en-us", ("sword", "Blade"));
        ContentTextPublishPlan plan = await PlanAsync(store, chunk);
        ContentTextPublishPlan lying = await PlanAsync(store, wrong);
        int audits = await AuditCountAsync(store);

        // The row half handed to the row-only commit, and the plan handed to another store, are both refused.
        var rowHalf = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.CommitPublishAsync(plan.RowPlan, Request(0), null));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, rowHalf.Reason);
        using var otherFiles = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore other = await OpenAsync(otherFiles);
        await ApplyAsync(other, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        await ((IContentTextAuthoringStore)other).FreezeChangesAsync(0);
        var foreign = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => ((IContentTextAuthoringStore)other).CommitTextPublishAsync(plan, Request(0), null));
        Assert.Equal(ContentAuthoringException.TextStateMismatchReason, foreign.Reason);

        // A chunk that is not the plan's own values is refused, never trusted.
        var mismatch = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => text.CommitTextPublishAsync(lying, Request(0), null));
        Assert.Equal(ContentAuthoringException.TextChunkMismatchReason, mismatch.Reason);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Equal(audits, await AuditCountAsync(store));

        await text.CommitTextPublishAsync(plan, Request(0), null);
        Assert.Equal("Sword", Assert.Single((await text.ReadTextSnapshotAsync(1)).Revisions).Value);
        Assert.Equal(chunk.Hash, Assert.Single((await text.ReadTextSnapshotAsync(1)).Languages).Hash);
        Assert.Null(await store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task The_expected_draft_discard_keeps_a_rival_landed_first_and_serializes_one_arriving_during_it()
    {
        using var database = new TemporaryCatalogDatabase();
        Func<Task>? duringDiscard = null;
        Task? rival = null;
        using SqliteContentAuthoringStore store = await OpenAsync(database, () =>
        {
            // The discard's own audit reads the clock while the store holds its lease and transaction. A rival
            // started HERE can only queue on the lease, which is the property under test.
            if (duringDiscard is Func<Task> start)
            {
                duringDiscard = null;
                rival = Task.Run(start);
            }

            return DateTimeOffset.UnixEpoch;
        });
        IContentTextAuthoringStore text = store;
        await store.ApplyEditsAsync(new[] { Add() }, Actor, Operator, "row only");
        await store.PublishAsync(Request(0));

        // A rival that landed before the discard keeps the draft, with no discard audited.
        ContentDraft proof = await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        ContentDraft rivalDraft = await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "fr"), "Epee"));
        int audits = await AuditCountAsync(store);
        Assert.False(await text.TryDiscardChangesAsync(proof, Actor, Operator));
        Assert.Equal(2, (await store.GetOpenDraftAsync())!.TextEditCount);
        Assert.Equal(audits, await AuditCountAsync(store));

        // A rival arriving while the discard runs queues behind it and lands in a draft of its own afterwards,
        // so the discard deleted exactly the draft it compared and the rival's translation is kept.
        duringDiscard = () => ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "de"), "Klinge"));
        Assert.True(await text.TryDiscardChangesAsync(rivalDraft, Actor, Operator));
        Assert.NotNull(rival);
        await rival!;

        ContentDraft survivor = (await store.GetOpenDraftAsync())!;
        Assert.Equal("Klinge", Assert.Single(survivor.TextState!.Edits).Value);
        Assert.Equal("de", Assert.Single(survivor.TextState.Introductions).Language);
        IReadOnlyList<ContentAuditEntry> trail = await store.ListAuditAsync(default, 0, 0, 500);
        Assert.Equal(ContentAuditActions.DraftEdit, trail[0].Action);
        Assert.Equal("de", trail[0].LanguageTag);
        Assert.Equal(ContentAuditActions.DraftDiscard, trail[1].Action);
    }

    /// <summary>A publish through the store's own commit, which dispatches through the companion.</summary>
    static async Task<ContentVersionRecord> PublishAsync(IContentAuthoringStore store)
    {
        int active = await store.GetActiveVersionAsync();
        ContentPublishResult published = await store.PublishAsync(Request(active));
        return (await store.GetVersionAsync(published.VersionNumber))!;
    }

    /// <summary>
    /// The store's frozen draft as a complete text plan: the row plan through the ordinary publisher over a
    /// row-only twin, with the store as the id persistence, and the given chunk as the English record.
    /// </summary>
    static async Task<ContentTextPublishPlan> PlanAsync(SqliteContentAuthoringStore store, ContentTextChunkRecord chunk)
    {
        IContentTextAuthoringStore text = store;
        await store.ClearDraftFreezeAsync();
        ContentTextPublishSnapshot snapshot = await text.FreezeChangesAsync(0);
        var twin = new InMemoryContentAuthoringStore(TextRegistry());
        await twin.ApplyEditsAsync(snapshot.Draft.Changes.Edits, Actor, Operator, "twin");
        ContentPublishBaseline held = snapshot.Baseline;
        var baseline = new ContentPublishBaseline(
            held.VersionNumber, held.Rows, held.Rules, held.Chunks, new[] { new ManifestLanguageEntry("en-us", chunk.Hash) },
            held.MinimumServerBuild, held.MinimumClientBuild);
        ContentPublishPlan rowPlan = await new ContentPublisher(twin, store, TextRegistry()).PrepareAsync(Request(0), baseline);
        Assert.True(rowPlan.IsValid);
        var revision = new ContentTextRevision(Item, IdOf(rowPlan, "sword"), NameField, "en-us", "Sword", 1, null);
        return new ContentTextPublishPlan(rowPlan, snapshot, new ContentTextCandidate(
            new[] { revision }, new[] { new ContentTextLanguageDeclaration("en-us", "en-us") },
            Array.Empty<ContentTextRevision>(), new[] { revision }), new[] { chunk });
    }
}
