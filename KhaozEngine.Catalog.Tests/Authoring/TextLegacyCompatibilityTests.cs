using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Authoring;

public sealed class TextLegacyCompatibilityTests
{
    [Fact]
    public async Task Old_draft_reconstruction_cannot_discard_backend_held_text()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        ContentDraft complete = await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        var old = new ContentDraft(complete.BaseVersion, complete.OpenedBy, complete.OpenedAtUtc,
            complete.Note, complete.Changes, complete.FrozenForBaseVersion);
        int audits = await AuditCountAsync(store);

        Assert.False(await text.TryDiscardChangesAsync(old, Actor, Operator));
        var legacy = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.DiscardDraftAsync(Actor, Operator));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, legacy.Reason);
        Assert.Equal("Sword", (await store.GetOpenDraftAsync())!.TextState!.Edits.Single().Value);
        Assert.Equal(audits, await AuditCountAsync(store));

        Assert.True(await text.TryDiscardChangesAsync(complete, Actor, Operator));
        Assert.Null(await store.GetOpenDraftAsync());
        var discards = (await store.ListAuditAsync(default, 0, 0, 500))
            .Where(entry => entry.Action == ContentAuditActions.DraftDiscard)
            .ToDictionary(entry => entry.FieldName, entry => entry.BeforeValue);
        Assert.Equal("1", discards[string.Empty]);
        Assert.Equal("1", discards["text-edits"]);
        Assert.Equal("1", discards["language-introductions"]);
        Assert.Equal(audits + 3, await AuditCountAsync(store));
    }

    [Fact]
    public async Task A_rival_translation_or_freeze_cannot_be_discarded_by_an_older_complete_proof()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        ContentDraft first = await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        ContentDraft second = await ApplyAsync(text, null, ContentTextEdit.Set(Name, "Rival"));
        int before = await AuditCountAsync(store);
        Assert.False(await text.TryDiscardChangesAsync(first, Actor, Operator));
        Assert.Equal(before, await AuditCountAsync(store));

        ContentTextPublishSnapshot frozen = await text.FreezeChangesAsync(0);
        Assert.True(frozen.Draft.IsFrozen);
        Assert.False(await text.TryDiscardChangesAsync(second, Actor, Operator));
        Assert.False(await text.TryDiscardChangesAsync(frozen.Draft, Actor, Operator));
        await store.ClearDraftFreezeAsync();
        Assert.True(await text.TryDiscardChangesAsync(second, Actor, Operator));
        Assert.Null(await store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task A_rival_declaration_keeps_the_draft_against_an_ownership_proof_taken_earlier()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        ContentDraft proof = await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));

        // The rival introduces French and then removes its only French value, so the surviving intent is a
        // Remove and the declaration stands on its own.
        await ApplyAsync(text, null, ContentTextEdit.Set(Target(DescriptionField, "fr"), "Lame"));
        ContentDraft rival = await ApplyAsync(text, null, ContentTextEdit.Remove(Target(DescriptionField, "fr")));
        Assert.Equal(new[] { "en-us", "fr" }, rival.TextState!.Introductions.Select(i => i.Language));
        int audits = await AuditCountAsync(store);

        Assert.False(await text.TryDiscardChangesAsync(proof, Actor, Operator));
        var forged = new ContentDraft(
            new ContentDraftTextState(rival.TextState.Edits, rival.TextState.Introductions.Take(1).ToArray()),
            rival.BaseVersion, rival.OpenedBy, rival.OpenedAtUtc, rival.Note, rival.Changes);
        Assert.False(await text.TryDiscardChangesAsync(forged, Actor, Operator));
        var emptyProof = new ContentDraft(ContentDraftTextState.Empty,
            rival.BaseVersion, rival.OpenedBy, rival.OpenedAtUtc, rival.Note, rival.Changes);
        Assert.False(await text.TryDiscardChangesAsync(emptyProof, Actor, Operator));

        Assert.Equal(2, (await store.GetOpenDraftAsync())!.LanguageIntroductionCount);
        Assert.Equal(audits, await AuditCountAsync(store));
        Assert.True(await text.TryDiscardChangesAsync(rival, Actor, Operator));
    }

    [Fact]
    public async Task Legacy_publication_is_refused_before_it_can_ignore_text()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = TextStore(files.Pack());
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));

        var freeze = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.FreezeDraftAsync(0));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, freeze.Reason);
        var publish = await Assert.ThrowsAsync<ContentAuthoringException>(() => LegacyPublishAsync(store, files.Pack(), Request(0)));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, publish.Reason);
        Assert.Equal("Sword", (await store.GetOpenDraftAsync())!.TextState!.Edits.Single().Value);
        Assert.False((await store.GetOpenDraftAsync())!.IsFrozen);
        Assert.Empty(await store.ListVersionsAsync());
    }

    [Fact]
    public async Task Row_only_edits_preserve_held_text_and_declarations()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        ContentDraft after = await store.ApplyEditsAsync(new[] { Add("shield") }, Actor, Operator, "row only");

        Assert.Equal(2, after.EditCount);
        Assert.Equal("Sword", after.TextState!.Edits.Single().Value);
        Assert.Single(after.TextState.Introductions);
    }

    [Fact]
    public async Task An_actual_held_text_only_draft_survives_every_legacy_route()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = TextStore(files.Pack());
        IContentTextAuthoringStore text = store;
        await store.ApplyEditsAsync(new[] { Add() }, Actor, Operator, "row only");
        await store.PublishAsync(Request(0));
        ContentDraft held = await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "fr"), "Epee"));
        Assert.Equal(0, held.EditCount);
        Assert.Equal(2, held.TotalWorkCount);

        var old = new ContentDraft(held.BaseVersion, held.OpenedBy, held.OpenedAtUtc, held.Note, held.Changes);
        Assert.False(await text.TryDiscardChangesAsync(old, Actor, Operator));
        await Assert.ThrowsAsync<ContentAuthoringException>(() => store.DiscardDraftAsync(Actor, Operator));
        await Assert.ThrowsAsync<ContentAuthoringException>(() => store.FreezeDraftAsync(1));
        await Assert.ThrowsAsync<ContentAuthoringException>(() => LegacyPublishAsync(store, files.Pack(), Request(1)));
        Assert.False(ContentUpgradeDraftMatch.IsKnownWork(held, Array.Empty<ContentEdit>()));

        ContentDraft standing = (await store.GetOpenDraftAsync())!;
        Assert.Equal("Epee", standing.TextState!.Edits.Single().Value);
        Assert.Equal(1, await store.GetActiveVersionAsync());
    }

    [Fact]
    public async Task An_old_wrapper_handing_the_row_half_of_a_text_plan_to_the_legacy_commit_is_refused()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        ContentTextChunkRecord chunk = Chunk("en-us", ("sword", "Sword"));
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en-us", chunk.Hash) });
        var revision = new ContentTextRevision(Item, IdOf(rowPlan, "sword"), NameField, "en-us", "Sword", 1, null);
        var plan = new ContentTextPublishPlan(rowPlan, snapshot, new ContentTextCandidate(
            new[] { revision }, new[] { new ContentTextLanguageDeclaration("en-us", "en-us") },
            Array.Empty<ContentTextRevision>(), new[] { revision }), new[] { chunk });

        var wrapped = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.CommitPublishAsync(plan.RowPlan, Request(0), null));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, wrapped.Reason);
        var bare = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.CommitPublishAsync(rowPlan, Request(0), null));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, bare.Reason);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Equal("Sword", (await store.GetOpenDraftAsync())!.TextState!.Edits.Single().Value);

        using var files = new TemporaryCatalogDatabase();
        using var sqlite = new SqliteContentAuthoringStore(files.ConnectionString, TextRegistry(), files.Pack());
        await sqlite.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        var provider = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => sqlite.CommitPublishAsync(plan.RowPlan, Request(0), null));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, provider.Reason);
        Assert.Empty(await sqlite.ListVersionsAsync());

        await text.CommitTextPublishAsync(plan, Request(0), null);
        Assert.Equal("Sword", (await text.ReadTextSnapshotAsync(1)).Revisions.Single().Value);
    }

    [Fact]
    public async Task Text_bearing_bundles_are_refused_by_every_row_only_import()
    {
        var textual = new ContentBundle(2, "epoch", 1, Array.Empty<ContentBundleType>(), Array.Empty<ContentBundleRow>(),
            Array.Empty<ContentFamily>(), Array.Empty<RemapRule>(), new ContentBundleTextState(
                new[] { new ContentTextLanguageDeclaration("en", "en") },
                new[] { new ContentBundleTextValue(Target(NameField, "en"), "Sword") }));
        var stripped = new ContentBundle(2, "epoch", 1, Array.Empty<ContentBundleType>(), Array.Empty<ContentBundleRow>(),
            Array.Empty<ContentFamily>(), Array.Empty<RemapRule>());

        using var files = new TemporaryCatalogDatabase();
        var memory = TextStore(files.Pack());
        foreach (ContentBundle bundle in new[] { textual, stripped })
        {
            var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
                () => memory.ImportBundleAsync(bundle, Actor, Operator, "import"));
            Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, refused.Reason);
        }

        Assert.Empty(await memory.ListVersionsAsync());

        using var sqliteFiles = new TemporaryCatalogDatabase();
        using var sqlite = new SqliteContentAuthoringStore(sqliteFiles.ConnectionString, TextRegistry(), sqliteFiles.Pack());
        await sqlite.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        foreach (ContentBundle bundle in new[] { textual, stripped })
        {
            var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
                () => sqlite.ImportBundleAsync(bundle, Actor, Operator, "import"));
            Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, refused.Reason);
        }

        Assert.Empty(await sqlite.ListVersionsAsync());

        // The reference export writes the text half too, as format 2, so the row-only import still refuses it.
        var published = TextStore(files.Pack());
        await PublishNamedRowAsync(published, "sword", "Sword");
        ContentBundle exported = await published.ExportBundleAsync(1);
        Assert.Equal(ContentBundle.TextFormatVersion, exported.FormatVersion);
        Assert.Equal("Sword", exported.TextState!.Values.Single().Value);
        var reimport = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => TextStore(files.Pack()).ImportBundleAsync(exported, Actor, Operator, "import"));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, reimport.Reason);
    }

    [Fact]
    public async Task A_legacy_fork_of_a_row_holding_text_is_refused_rather_than_published_without_its_copy()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = TextStore(files.Pack());
        await PublishNamedRowAsync(store, "sword", "Sword");
        int id = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().Id;
        ContentEdit fork = ContentEdit.Fork(
            Item, id, Sword, new ContentKey("old_sword"), LegacyField, Array.Empty<ContentFieldEdit>());
        await store.ApplyEditsAsync(new[] { fork }, Actor, Operator, "fork");

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => LegacyPublishAsync(store, files.Pack(), Request(1)));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, refused.Reason);
        Assert.Equal(1, await store.GetActiveVersionAsync());

        await store.DiscardDraftAsync(Actor, Operator);
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Update(Item, id, Sword, new[] { Value(5) }) }, Actor, Operator, "update");
        await store.PublishAsync(Request(1));
        IContentTextAuthoringStore text = store;
        Assert.Equal("Sword", (await text.ReadTextSnapshotAsync(2)).Revisions.Single().Value);
        Assert.Equal("en", Assert.Single((await text.ReadTextSnapshotAsync(2)).Languages).WireTag);
    }

    [Fact]
    public async Task Legacy_rollback_refuses_versions_holding_text_and_changes_nothing()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await PublishNamedRowAsync(store, "sword", "Sword");
        ContentTextRevision sword = (await text.ReadTextSnapshotAsync(1)).Revisions.Single();
        await ApplyAsync(text, new[] { ContentEdit.Update(Item, sword.DefinitionId, Sword, new[] { Value(5) }) },
            ContentTextEdit.Set(Target(NameField, "en"), "Blade"));
        ContentTextChunkRecord chunk = Chunk("en", ("sword", "Blade"));
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en", chunk.Hash) });
        var blade = new ContentTextRevision(Item, sword.DefinitionId, NameField, "en", "Blade", 2, null);
        await text.CommitTextPublishAsync(new ContentTextPublishPlan(rowPlan, snapshot, new ContentTextCandidate(
            new[] { blade }, new[] { new ContentTextLanguageDeclaration("en", "en") }, new[] { sword }, new[] { blade }),
            new[] { chunk }), Request(1), null);

        int versions = (await store.ListVersionsAsync()).Count;
        int audits = await AuditCountAsync(store);
        Assert.Equal(2, await store.GetActiveVersionAsync());
        Assert.Null(await store.GetOpenDraftAsync());

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(1, Actor, Operator, "rollback"));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, refused.Reason);
        Assert.Equal(versions, (await store.ListVersionsAsync()).Count);
        Assert.Equal(2, await store.GetActiveVersionAsync());
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(audits, await AuditCountAsync(store));
        Assert.Equal("Blade", (await text.ReadTextSnapshotAsync(2)).Revisions.Single().Value);
    }

    [Fact]
    public async Task Legacy_rollback_still_runs_on_a_store_holding_no_text()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = TextStore(files.Pack());
        await store.ApplyEditsAsync(new[] { Add() }, Actor, Operator, "add");
        await store.PublishAsync(Request(0));
        int id = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().Id;
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Update(Item, id, Sword, new[] { Value(5) }) }, Actor, Operator, "update");
        await store.PublishAsync(Request(1));

        ContentDraft draft = await store.RollbackToAsync(1, Actor, Operator, "rollback");
        Assert.Equal(1, draft.EditCount);
        Assert.Contains(await store.ListAuditAsync(default, 0, 0, 500), e => e.Action == ContentAuditActions.Rollback);
        await store.PublishAsync(Request(2));
        Assert.Equal(3, await store.GetActiveVersionAsync());
    }

    /// <summary>
    /// A publish through the ROW-ONLY seam: the legacy pipeline over a view of the store that exposes no text
    /// companion, which is the route an old wrapper takes. The view still forwards the guarded freeze, which a
    /// publish requires. The store's own publish dispatches through the companion and is covered by the text
    /// publish suites.
    /// </summary>
    static Task<ContentPublishResult> LegacyPublishAsync(
        InMemoryContentAuthoringStore store, IPackStore pack, ContentPublishRequest request)
    {
        var view = new ConditionalRowOnlyStoreView(store);
        return new ContentPublishCommit(view, pack, new ContentPublisher(view, store, TextRegistry())).PublishAsync(request);
    }

    [Fact]
    public void Row_only_upgrade_proofs_refuse_drafts_holding_text()
    {
        var set = new ContentChangeSet();
        set.Apply(Add());
        var withText = new ContentDraft(new ContentDraftTextState(
            new[] { ContentTextEdit.Set(Name, "Sword") }, new[] { new ContentTextLanguageDeclaration("en-us", "en-us") }),
            0, Actor, DateTimeOffset.UnixEpoch, "", set);
        var declarationOnly = new ContentDraft(new ContentDraftTextState(
            Array.Empty<ContentTextEdit>(), new[] { new ContentTextLanguageDeclaration("fr", "fr") }),
            0, Actor, DateTimeOffset.UnixEpoch, "", set);
        var empty = new ContentDraft(ContentDraftTextState.Empty, 0, Actor, DateTimeOffset.UnixEpoch, "", set);
        ContentEdit[] planned = { Add() };

        Assert.False(ContentUpgradeDraftMatch.IsPlan(withText, planned));
        Assert.False(ContentUpgradeDraftMatch.IsKnownWork(withText, planned));
        Assert.False(ContentUpgradeDraftMatch.IsPlan(declarationOnly, planned));
        Assert.False(ContentUpgradeDraftMatch.IsKnownWork(declarationOnly, planned));
        Assert.True(ContentUpgradeDraftMatch.IsPlan(empty, planned));
        Assert.True(ContentUpgradeDraftMatch.IsKnownWork(empty, planned));
    }
}
