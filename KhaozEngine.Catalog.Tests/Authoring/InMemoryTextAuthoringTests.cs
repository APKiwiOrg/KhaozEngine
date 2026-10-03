using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Authoring;

public sealed class InMemoryTextAuthoringTests
{
    [Fact]
    public async Task Mixed_apply_is_atomic_and_text_only_followup_keeps_the_row_count()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        ContentDraft draft = await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        Assert.Equal(1, draft.EditCount);
        Assert.Equal(1, draft.TextEditCount);
        Assert.Equal(1, draft.LanguageIntroductionCount);
        Assert.Equal("en-us", draft.TextState!.Introductions.Single().WireTag);
        var audit = await store.ListAuditAsync(default, 0, 0, 500);
        Assert.Equal(2, audit.Count);
        ContentAuditEntry textAudit = audit.Single(e => e.LanguageTag is not null);
        Assert.Equal("en-us", textAudit.LanguageTag);
        Assert.Equal(NameField, textAudit.FieldName);
        Assert.Equal("Sword", textAudit.AfterValue);

        ContentTextTarget bad = Target("value", "en-us");
        var failure = await Assert.ThrowsAsync<ContentAuthoringException>(() => ApplyAsync(
            text, new[] { Add("shield") }, ContentTextEdit.Set(Name, "Changed"), ContentTextEdit.Set(bad, "No")));
        Assert.Equal(ContentAuthoringException.TextTargetIneligibleReason, failure.Reason);
        ContentDraft held = (await store.GetOpenDraftAsync())!;
        Assert.Equal("Sword", held.TextState!.Edits.Single().Value);
        Assert.Equal(1, held.EditCount);
        Assert.Equal(audit.Count, await AuditCountAsync(store));

        ContentDraft followup = await ApplyAsync(text, null, ContentTextEdit.Set(Target(DescriptionField, "en-us"), "Sharp"));
        Assert.Equal(1, followup.EditCount);
        Assert.Equal(2, followup.TextEditCount);
        Assert.Equal(1, followup.LanguageIntroductionCount);
        Assert.Equal(4, followup.TotalWorkCount);
    }

    [Fact]
    public async Task Set_then_remove_retains_the_declaration_and_declared_absent_remove_is_idempotent()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, string.Empty));
        ContentDraft removed = await ApplyAsync(text, null, ContentTextEdit.Remove(Name));
        Assert.Single(removed.TextState!.Introductions);
        Assert.Equal(ContentTextEditOperation.Remove, removed.TextState.Edits.Single().Operation);

        ContentDraft again = await ApplyAsync(text, null, ContentTextEdit.Remove(Name));
        Assert.Single(again.TextState!.Edits);
        Assert.Single(again.TextState.Introductions);

        ContentDraft absent = await ApplyAsync(text, null, ContentTextEdit.Remove(Target(DescriptionField, "en-us")));
        Assert.Single(absent.TextState!.Edits);

        var unknown = await Assert.ThrowsAsync<ContentAuthoringException>(() => ApplyAsync(
            text, null, ContentTextEdit.Remove(Target(NameField, "fr"))));
        Assert.Equal(ContentAuthoringException.TextLanguageUndeclaredReason, unknown.Reason);
        Assert.Single((await store.GetOpenDraftAsync())!.TextState!.Introductions);
    }

    [Fact]
    public async Task The_last_intent_wins_across_calls_and_keeps_its_first_ordinal()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "first"));
        await ApplyAsync(text, null, ContentTextEdit.Set(Target(DescriptionField, "en-us"), "middle"));
        ContentDraft draft = await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "EN-US"), "last"));

        Assert.Equal(new[] { NameField, DescriptionField }, draft.TextState!.Edits.Select(e => e.Target.FieldName));
        Assert.Equal(new[] { "last", "middle" }, draft.TextState.Edits.Select(e => e.Value));
    }

    [Fact]
    public async Task A_clock_failure_leaves_the_entire_mixed_batch_and_audit_unchanged()
    {
        bool fail = false;
        var store = TextStore(() => fail ? throw new InvalidOperationException("clock") : DateTimeOffset.UnixEpoch);
        IContentTextAuthoringStore text = store;
        fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(
            text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword")));
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Empty(await store.ListAuditAsync(default, 0, 0, 500));

        fail = false;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(
            text, null, ContentTextEdit.Set(Name, "Changed")));
        Assert.Equal("Sword", (await store.GetOpenDraftAsync())!.TextState!.Edits.Single().Value);
        fail = false;
        Assert.Equal(2, await AuditCountAsync(store));
    }

    [Fact]
    public async Task Full_text_is_retained_while_audit_abbreviates_valid_surrogate_pairs()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        string value = Utf8Value(8192);
        ContentDraft draft = await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, value));
        Assert.Equal(value, draft.TextState!.Edits.Single().Value);
        ContentAuditEntry audit = (await store.ListAuditAsync(default, 0, 0, 500)).Single(e => e.LanguageTag is not null);
        Assert.NotNull(audit.AfterValue);
        Assert.True(audit.AfterValue.Length <= ContentAuditEntry.MaxValueLength);
        Assert.EndsWith("[cut]", audit.AfterValue);
        Assert.DoesNotContain('�', audit.AfterValue);
        Assert.False(char.IsHighSurrogate(audit.AfterValue[^6]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_type_and_field_client_visibility_are_required(bool typeHidden)
    {
        var store = new InMemoryContentAuthoringStore(TextRegistry(
            typeHidden ? ContentVisibility.ServerOnly : ContentVisibility.Client));
        IContentTextAuthoringStore text = store;
        ContentTextTarget target = Target(typeHidden ? NameField : SecretField, "en");
        var failure = await Assert.ThrowsAsync<ContentAuthoringException>(() => ApplyAsync(
            text, new[] { Add() }, ContentTextEdit.Set(target, "Secret")));
        Assert.Equal(ContentAuthoringException.TextTargetIneligibleReason, failure.Reason);
        Assert.Null(await store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task Unknown_rows_and_over_long_derived_keys_are_refused_before_any_write()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        var missing = await Assert.ThrowsAsync<ContentAuthoringException>(() => ApplyAsync(
            text, null, ContentTextEdit.Set(Target(NameField, "en", "ghost"), "Nobody")));
        Assert.Equal(ContentAuthoringException.UnknownRowReason, missing.Reason);

        string longField = new('f', ContentTextKey.MaxKeyLength);
        ContentFieldSchema schema = new(new ContentFieldEntry[]
        {
            new("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new(longField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, false),
        });
        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(
            ContentRegistrationBand.Game, Item.Value, "item", new LongCodec(schema), null, schema, ContentVisibility.Client, 256);
        IContentTextAuthoringStore wide = new InMemoryContentAuthoringStore(registry);
        var tooLong = await Assert.ThrowsAsync<ContentAuthoringException>(() => ApplyAsync(
            wide, new[] { Add() }, ContentTextEdit.Set(Target(longField, "en"), "x")));
        Assert.Equal(ContentAuthoringException.TextBoundsReason, tooLong.Reason);
        Assert.Null(await wide.GetOpenDraftAsync());
        Assert.Null(await store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task A_retired_row_takes_set_and_remove_without_changing_retirement()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = TextStore(files.Pack());
        IContentTextAuthoringStore text = store;
        await PublishNamedRowAsync(store, "sword", "Sword");
        int id = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().Id;
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Retire(Item, id, Sword, ContentRetirePolicy.Placeholder, 0) }, Actor, Operator, "retire");
        await store.PublishAsync(Request(1));
        Assert.True((await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().IsRetired);

        ContentDraft set = await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "en"), "Old sword"));
        Assert.Equal(0, set.EditCount);
        Assert.Equal(0, set.LanguageIntroductionCount);
        ContentDraft removed = await ApplyAsync(text, null, ContentTextEdit.Remove(Target(NameField, "en")));
        Assert.Equal(ContentTextEditOperation.Remove, removed.TextState!.Edits.Single().Operation);
        Assert.Equal(0, removed.EditCount);
        Assert.True((await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().IsRetired);
        Assert.Equal("Sword", (await text.ReadTextSnapshotAsync(2)).Revisions.Single().Value);
    }

    [Fact]
    public async Task Freeze_returns_a_protected_complete_snapshot_and_refuses_moved_or_empty_bases()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        var empty = await Assert.ThrowsAsync<ContentAuthoringException>(() => text.FreezeChangesAsync(0));
        Assert.Equal(ContentAuthoringException.NoOpenDraftReason, empty.Reason);

        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, string.Empty));
        await ApplyAsync(text, null, ContentTextEdit.Remove(Name));
        var moved = await Assert.ThrowsAsync<ContentAuthoringException>(() => text.FreezeChangesAsync(4));
        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, moved.Reason);
        Assert.False((await store.GetOpenDraftAsync())!.IsFrozen);

        ContentTextPublishSnapshot snapshot = await text.FreezeChangesAsync(0);
        Assert.Equal(await store.GetStoreEpochAsync(), snapshot.StoreEpoch);
        Assert.Equal(0, snapshot.BaseVersion);
        Assert.Equal(0, snapshot.BaselineText.VersionNumber);
        Assert.Empty(snapshot.BaselineText.Languages);
        Assert.Equal(0, snapshot.Draft.FrozenForBaseVersion);
        Assert.Single(snapshot.TextState.Introductions);
        Assert.Equal(ContentTextEditOperation.Remove, snapshot.TextState.Edits.Single().Operation);

        snapshot.Draft.Changes.Apply(Add("shield"));
        Assert.Equal(1, (await store.GetOpenDraftAsync())!.EditCount);
        var frozen = await Assert.ThrowsAsync<ContentAuthoringException>(() => ApplyAsync(
            text, null, ContentTextEdit.Set(Name, "late")));
        Assert.Equal(ContentAuthoringException.PublishInProgressReason, frozen.Reason);
    }

    [Fact]
    public async Task A_supplied_mixed_plan_commits_rows_text_declarations_and_audit_together()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        ContentVersionRecord record = await PublishNamedRowAsync(store, "sword", "Sword");

        Assert.Equal(1, record.VersionNumber);
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Null(await store.GetOpenDraftAsync());
        ContentVersionTextSnapshot snapshot = await text.ReadTextSnapshotAsync(1);
        Assert.Equal(1, snapshot.VersionNumber);
        Assert.Equal(await store.GetStoreEpochAsync(), snapshot.StoreEpoch);
        ContentTextRevision revision = Assert.Single(snapshot.Revisions);
        Assert.Equal("Sword", revision.Value);
        Assert.Equal(1, revision.ValidFromVersion);
        ContentTextLanguage language = Assert.Single(snapshot.Languages);
        Assert.Equal("en", language.WireTag);
        Assert.Equal(Chunk("en", ("sword", "Sword")).Hash, language.Hash);
        Assert.Equal("en", Assert.Single((await store.ReadPublishBaselineAsync()).Languages).Tag);

        ContentAuditEntry published = (await store.ListAuditAsync(default, 0, 0, 500))
            .Single(e => e.Action == ContentAuditActions.Publish && e.LanguageTag is not null);
        Assert.Equal("en", published.LanguageTag);
        Assert.Equal("Sword", published.AfterValue);
        Assert.Equal(1, published.VersionNumber);
    }

    [Fact]
    public async Task Set_then_remove_publishes_an_independent_empty_language()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        await ApplyAsync(text, null, ContentTextEdit.Remove(Name));
        ContentTextChunkRecord chunk = Chunk("en-us");
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en-us", chunk.Hash) });
        var candidate = new ContentTextCandidate(
            Array.Empty<ContentTextRevision>(),
            new[] { new ContentTextLanguageDeclaration("en-us", "en-us") },
            Array.Empty<ContentTextRevision>(),
            Array.Empty<ContentTextRevision>());
        var plan = new ContentTextPublishPlan(rowPlan, snapshot, candidate, new[] { chunk });
        Assert.Same(snapshot.TextState, plan.FrozenText);
        Assert.NotNull(plan.RowPlan.FrozenTextState);
        await text.CommitTextPublishAsync(plan, Request(0), null);

        ContentVersionTextSnapshot published = await text.ReadTextSnapshotAsync(1);
        Assert.Empty(published.Revisions);
        Assert.Equal("en-us", Assert.Single(published.Languages).Language);
    }

    [Fact]
    public async Task Commit_confirms_epoch_and_the_actual_frozen_text_before_any_write()
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

        var foreign = TextStore();
        IContentTextAuthoringStore other = foreign;
        await ApplyAsync(other, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        await other.FreezeChangesAsync(0);
        var epoch = await Assert.ThrowsAsync<ContentAuthoringException>(() => other.CommitTextPublishAsync(plan, Request(0), null));
        Assert.Equal(ContentAuthoringException.TextStateMismatchReason, epoch.Reason);
        Assert.Empty(await foreign.ListVersionsAsync());

        await store.ClearDraftFreezeAsync();
        await ApplyAsync(text, null, ContentTextEdit.Set(Name, "Rival"));
        await text.FreezeChangesAsync(0);
        int audits = await AuditCountAsync(store);
        var rival = await Assert.ThrowsAsync<ContentAuthoringException>(() => text.CommitTextPublishAsync(plan, Request(0), null));
        Assert.Equal(ContentAuthoringException.TextStateMismatchReason, rival.Reason);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Equal("Rival", (await store.GetOpenDraftAsync())!.TextState!.Edits.Single().Value);
        Assert.Equal(audits, await AuditCountAsync(store));
    }

    [Fact]
    public async Task A_commit_clock_failure_rolls_back_rows_text_and_the_draft()
    {
        bool fail = false;
        var store = TextStore(() => fail ? throw new InvalidOperationException("clock") : DateTimeOffset.UnixEpoch);
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        ContentTextChunkRecord chunk = Chunk("en-us", ("sword", "Sword"));
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en-us", chunk.Hash) });
        var revision = new ContentTextRevision(Item, IdOf(rowPlan, "sword"), NameField, "en-us", "Sword", 1, null);
        var plan = new ContentTextPublishPlan(rowPlan, snapshot, new ContentTextCandidate(
            new[] { revision }, new[] { new ContentTextLanguageDeclaration("en-us", "en-us") },
            Array.Empty<ContentTextRevision>(), new[] { revision }), new[] { chunk });

        fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => text.CommitTextPublishAsync(plan, Request(0), null));
        fail = false;
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Equal(0, await store.GetActiveVersionAsync());
        Assert.True((await store.GetOpenDraftAsync())!.IsFrozen);
        Assert.Equal(2, await AuditCountAsync(store));
        await Assert.ThrowsAsync<ContentAuthoringException>(() => text.ReadTextSnapshotAsync(1));

        await text.CommitTextPublishAsync(plan, Request(0), null);
        Assert.Equal("Sword", (await text.ReadTextSnapshotAsync(1)).Revisions.Single().Value);
    }

    [Fact]
    public async Task Every_new_commit_is_complete_and_only_positive_committed_versions_are_read()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = TextStore(files.Pack());
        IContentTextAuthoringStore text = store;
        await store.ApplyEditsAsync(new[] { Add() }, Actor, Operator, "row only");
        await store.PublishAsync(Request(0));

        ContentVersionTextSnapshot rowOnly = await text.ReadTextSnapshotAsync(1);
        Assert.Empty(rowOnly.Revisions);
        Assert.Empty(rowOnly.Languages);

        var zero = await Assert.ThrowsAsync<ContentAuthoringException>(() => text.ReadTextSnapshotAsync(0));
        Assert.Equal(ContentAuthoringException.UnknownVersionReason, zero.Reason);
        var missing = await Assert.ThrowsAsync<ContentAuthoringException>(() => text.ReadTextSnapshotAsync(2));
        Assert.Equal(ContentAuthoringException.UnknownVersionReason, missing.Reason);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => text.ReadTextSnapshotAsync(-1));
    }

    [Fact]
    public async Task Returned_drafts_are_protected_copies_that_still_prove_ownership()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        ContentDraft applied = await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        applied.Changes.Apply(Add("shield"));
        Assert.Equal(1, (await store.GetOpenDraftAsync())!.EditCount);

        ContentDraft edited = await store.ApplyEditsAsync(new[] { Add("axe") }, Actor, Operator, "row only");
        edited.Changes.Apply(Add("shield"));
        ContentDraft read = (await store.GetOpenDraftAsync())!;
        Assert.Equal(2, read.EditCount);
        read.Changes.Apply(Add("shield"));
        Assert.Equal(2, (await store.GetOpenDraftAsync())!.EditCount);

        int audits = await AuditCountAsync(store);
        Assert.False(await text.TryDiscardChangesAsync(read, Actor, Operator));
        Assert.Equal(audits, await AuditCountAsync(store));
        ContentDraft proof = (await store.GetOpenDraftAsync())!;
        Assert.True(await text.TryDiscardChangesAsync(proof, Actor, Operator));
        Assert.Null(await store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task Bundle_import_and_rollback_through_the_companion_stay_explicitly_unavailable()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        var bundle = new ContentBundle(1, "epoch", 0, Array.Empty<ContentBundleType>(), Array.Empty<ContentBundleRow>(),
            Array.Empty<ContentFamily>(), Array.Empty<RemapRule>());
        var import = await Assert.ThrowsAsync<ContentAuthoringException>(() => text.ImportTextBundleAsync(bundle, Actor, Operator, ""));
        Assert.Equal(ContentAuthoringException.TextOperationUnavailableReason, import.Reason);
        var rollback = await Assert.ThrowsAsync<ContentAuthoringException>(() => text.RollbackTextToAsync(1, Actor, Operator, ""));
        Assert.Equal(ContentAuthoringException.TextOperationUnavailableReason, rollback.Reason);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Null(await store.GetOpenDraftAsync());
    }

    sealed class LongCodec(ContentFieldSchema schema) : ContentRowCodecBase(Item, schema);
}
