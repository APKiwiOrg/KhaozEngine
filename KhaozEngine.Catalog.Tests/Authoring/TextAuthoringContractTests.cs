using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Authoring;

public sealed class TextAuthoringContractTests
{
    [Fact]
    public void The_atomic_text_companion_exposes_every_settled_operation()
    {
        Type contract = typeof(IContentTextAuthoringStore);
        Assert.True(contract.IsInterface);
        Assert.True(typeof(IContentAuthoringStore).IsAssignableFrom(contract));

        AssertSignature(contract, "ApplyChangesAsync", typeof(Task<ContentDraft>),
            typeof(ContentAuthoringChanges), typeof(string), typeof(string), typeof(string), typeof(CancellationToken));
        AssertSignature(contract, "FreezeChangesAsync", typeof(Task<ContentTextPublishSnapshot>),
            typeof(int), typeof(CancellationToken));
        AssertSignature(contract, "ReadTextSnapshotAsync", typeof(Task<ContentVersionTextSnapshot>),
            typeof(int), typeof(CancellationToken));
        AssertSignature(contract, "CommitTextPublishAsync", typeof(Task<ContentVersionRecord>),
            typeof(ContentTextPublishPlan), typeof(ContentPublishRequest), typeof(IPackVersionPointerStore),
            typeof(CancellationToken));
        AssertSignature(contract, "TryDiscardChangesAsync", typeof(Task<bool>),
            typeof(ContentDraft), typeof(string), typeof(string), typeof(CancellationToken));
        AssertSignature(contract, "ImportTextBundleAsync", typeof(Task<ContentPublishResult>),
            typeof(ContentBundle), typeof(string), typeof(string), typeof(string), typeof(CancellationToken));
        AssertSignature(contract, "RollbackTextToAsync", typeof(Task<ContentDraft>),
            typeof(int), typeof(string), typeof(string), typeof(string), typeof(CancellationToken));
        Assert.Equal(7, contract.GetMethods().Length);
        Assert.True(typeof(IContentTextAuthoringStore).IsAssignableFrom(typeof(InMemoryContentAuthoringStore)));
    }

    [Theory]
    [InlineData("EN-us", "en-us")]
    [InlineData("qz-123", "qz-123")]
    [InlineData("en", "en")]
    [InlineData("ZH-Hant-TW", "zh-hant-tw")]
    [InlineData("abcdefgh-abcdefgh-abcdefgh-abcdefgh", "abcdefgh-abcdefgh-abcdefgh-abcdefgh")]
    public void Language_identity_is_ascii_invariant_without_installed_culture_validation(string input, string expected)
        => Assert.Equal(expected, ContentTextLanguageTag.Normalize(input));

    [Theory]
    [InlineData("en_US")]
    [InlineData("en--us")]
    [InlineData("-en")]
    [InlineData("en-")]
    [InlineData("1en")]
    [InlineData("é")]
    [InlineData("en‐us")]
    [InlineData("")]
    [InlineData("abcdefgh-abcdefgh-abcdefgh-abcdefghi")]
    public void Invalid_language_identity_is_refused(string input)
    {
        Assert.Throws<ArgumentException>(() => ContentTextLanguageTag.Normalize(input));
        Assert.False(ContentTextLanguageTag.TryNormalize(input, out _));
    }

    [Fact]
    public void Targets_compare_by_canonical_language_and_refuse_blank_identity()
    {
        var spelled = new ContentTextTarget(Item, Sword, NameField, "en-US");
        Assert.Equal("en-us", Name.Language);
        Assert.Equal(Name, spelled);
        Assert.Equal(Name.GetHashCode(), spelled.GetHashCode());
        Assert.NotEqual(Name, Target(NameField, "en-gb"));
        Assert.Throws<ArgumentException>(() => new ContentTextTarget(Item, default, NameField, "en"));
        Assert.Throws<ArgumentException>(() => new ContentTextTarget(Item, Sword, " ", "en"));
        Assert.Throws<ArgumentException>(() => new ContentTextTarget(Item, Sword, NameField, "en_US"));
    }

    [Fact]
    public void Batch_canonical_collisions_and_invalid_utf8_values_are_refused()
    {
        var same = new ContentTextTarget(Item, Sword, NameField, "en-US");
        Assert.Throws<ArgumentException>(() => new ContentAuthoringChanges(Array.Empty<ContentEdit>(), new[]
        {
            ContentTextEdit.Set(Name, "one"), ContentTextEdit.Remove(same),
        }));
        Assert.Throws<ArgumentException>(() => ContentTextEdit.Set(same, "\ud800"));
        Assert.Throws<ArgumentException>(() => ContentTextEdit.Set(same, "a\udc00b"));
        Assert.Throws<ArgumentException>(() => ContentTextEdit.Set(same, new string('a', 8193)));
        Assert.Throws<ArgumentException>(() => ContentTextEdit.Set(same, Utf8Value(8193)));
        Assert.Equal(Utf8Value(8192), ContentTextEdit.Set(same, Utf8Value(8192)).Value);
        Assert.Equal(8192, ContentTextEdit.MaxValueBytes);
        Assert.Equal(35, ContentTextLanguageTag.MaxBytes);

        ContentTextEdit empty = ContentTextEdit.Set(same, string.Empty);
        Assert.Equal(string.Empty, empty.Value);
        Assert.Equal(ContentTextEditOperation.Set, empty.Operation);
        ContentTextEdit removal = ContentTextEdit.Remove(same);
        Assert.Null(removal.Value);
        Assert.Equal(ContentTextEditOperation.Remove, removal.Operation);
        Assert.NotEqual(empty, removal);
    }

    [Fact]
    public void Submitted_row_byte_payloads_and_mutable_lists_do_not_alias_complete_state()
    {
        byte[] payload = { 1, 2 };
        var row = ContentEdit.Add(Item, Sword, new[]
        {
            new ContentFieldEdit("blob", ContentFieldValue.OfBytes(ContentFieldKind.OpaqueBytes, payload)),
        });
        var rows = new[] { row };
        var text = new[] { ContentTextEdit.Set(Name, "one") };
        var changes = new ContentAuthoringChanges(rows, text);
        payload[0] = 9;
        rows[0] = Add();
        text[0] = ContentTextEdit.Set(Name, "two");
        Assert.Equal(1, changes.RowEdits.Single().Fields.Single().Value.Bytes.Span[0]);
        Assert.Equal("one", changes.TextEdits.Single().Value);

        var submitted = new ContentChangeSet(changes.RowEdits);
        var draft = new ContentDraft(ContentDraftTextState.Empty, 0, Actor, DateTimeOffset.UnixEpoch, "", submitted);
        submitted.Apply(Add());
        Assert.Equal("blob", draft.Changes.Edits.Single().Fields.Single().Name);
        Assert.Equal(1, draft.EditCount);
        Assert.Equal(0, draft.TextEditCount);
        Assert.Equal(0, draft.LanguageIntroductionCount);
        Assert.Equal(1, draft.TotalWorkCount);

        var edits = new[] { ContentTextEdit.Set(Name, "x") };
        var introductions = new[] { new ContentTextLanguageDeclaration("en-us", "en-us") };
        var state = new ContentDraftTextState(edits, introductions);
        edits[0] = ContentTextEdit.Remove(Name);
        introductions[0] = new ContentTextLanguageDeclaration("fr", "fr");
        Assert.Equal("x", state.Edits.Single().Value);
        Assert.Equal("en-us", state.Introductions.Single().Language);

        byte[] stored = { 7, 8, 9 };
        var chunk = new ContentTextChunkRecord("en", "ab12", stored, false);
        stored[0] = 0;
        Assert.Equal(7, chunk.StoredFile.Span[0]);
        Assert.True(chunk.AsReused().StoredFile.IsEmpty);
    }

    [Fact]
    public void Text_state_and_declarations_refuse_duplicates_and_unmapped_spellings()
    {
        Assert.Throws<ArgumentException>(() => new ContentDraftTextState(
            new[] { ContentTextEdit.Set(Name, "a"), ContentTextEdit.Set(Target(NameField, "en-US"), "b") },
            Array.Empty<ContentTextLanguageDeclaration>()));
        Assert.Throws<ArgumentException>(() => new ContentDraftTextState(
            Array.Empty<ContentTextEdit>(),
            new[] { new ContentTextLanguageDeclaration("en", "en"), new ContentTextLanguageDeclaration("en", "EN") }));

        var historic = new ContentTextLanguageDeclaration("en-us", "en-US");
        Assert.Equal("en-us", historic.Language);
        Assert.Equal("en-US", historic.WireTag);
        Assert.Throws<ArgumentException>(() => new ContentTextLanguageDeclaration("en-us", "en-GB"));
        Assert.Throws<ArgumentException>(() => new ContentTextLanguageDeclaration("en-us", "en_US"));
        Assert.Throws<ArgumentException>(() => new ContentTextLanguage("en", "en", "NOT-HEX"));
        Assert.True(ContentDraftTextState.Empty.IsEmpty);
    }

    [Fact]
    public void Old_constructors_deconstruction_and_defaults_are_unchanged()
    {
        ConstructorInfo? oldDraft = typeof(ContentDraft).GetConstructor(new[]
        {
            typeof(int), typeof(string), typeof(DateTimeOffset), typeof(string), typeof(ContentChangeSet), typeof(int?),
        });
        Assert.NotNull(oldDraft);
        Assert.True(oldDraft.GetParameters()[5].HasDefaultValue);
        Assert.Null(oldDraft.GetParameters()[5].DefaultValue);
        var draft = new ContentDraft(3, Actor, DateTimeOffset.UnixEpoch, "note", new ContentChangeSet());
        Assert.Null(draft.TextState);
        Assert.Equal(0, draft.TextEditCount);

        Type[] auditShape =
        {
            typeof(long), typeof(DateTimeOffset), typeof(string), typeof(string), typeof(string), typeof(ContentTypeId),
            typeof(int), typeof(ContentKey), typeof(string), typeof(string), typeof(string), typeof(int), typeof(string),
        };
        Assert.NotNull(typeof(ContentAuditEntry).GetConstructor(auditShape));
        MethodInfo deconstruct = Assert.Single(typeof(ContentAuditEntry).GetMethods(), m => m.Name == "Deconstruct");
        Assert.Equal(13, deconstruct.GetParameters().Length);
        Assert.Equal(4096, ContentAuditEntry.MaxValueLength);
        PropertyInfo tag = typeof(ContentAuditEntry).GetProperty(nameof(ContentAuditEntry.LanguageTag))!;
        Assert.Equal(typeof(string), tag.PropertyType);
        Assert.Contains(tag.SetMethod!.ReturnParameter.GetRequiredCustomModifiers(),
            modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        var entry = new ContentAuditEntry(1, DateTimeOffset.UnixEpoch, "a", "o", ContentAuditActions.DraftEdit,
            Item, 0, Sword, "value", null, "1", 0, "");
        Assert.Null(entry.LanguageTag);

        Assert.NotNull(typeof(ContentBundle).GetConstructor(new[]
        {
            typeof(int), typeof(string), typeof(int), typeof(IReadOnlyList<ContentBundleType>),
            typeof(IReadOnlyList<ContentBundleRow>), typeof(IReadOnlyList<ContentFamily>), typeof(IReadOnlyList<RemapRule>),
        }));
        var bundle = new ContentBundle(1, "epoch", 0, Array.Empty<ContentBundleType>(), Array.Empty<ContentBundleRow>(),
            Array.Empty<ContentFamily>(), Array.Empty<RemapRule>());
        Assert.Null(bundle.TextState);
        Assert.Equal(1, ContentBundle.CurrentFormatVersion);
        Assert.Equal(typeof(ContentDraftTextState),
            typeof(ContentPublishPlan).GetProperty(nameof(ContentPublishPlan.FrozenTextState))!.PropertyType);
    }

    [Fact]
    public void Audit_rendering_keeps_valid_pairs_and_a_visible_marker()
    {
        string full = Utf8Value(8192);
        string rendered = ContentTextAuditRendering.Render(full)!;
        Assert.True(rendered.Length <= ContentAuditEntry.MaxValueLength);
        Assert.EndsWith("[cut]", rendered);
        string kept = rendered[..^5];
        Assert.StartsWith(kept, full, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(kept[^1]));
        Assert.Equal("short", ContentTextAuditRendering.Render("short"));
        Assert.Equal(string.Empty, ContentTextAuditRendering.Render(string.Empty));
        Assert.Null(ContentTextAuditRendering.Render(null));
    }

    [Fact]
    public void A_fresh_chunk_carries_bytes_and_a_format_one_bundle_carries_no_text()
    {
        Assert.Throws<ArgumentException>(() => new ContentTextChunkRecord("en", "ab12", ReadOnlyMemory<byte>.Empty, false));
        Assert.True(new ContentTextChunkRecord("en", "ab12", default, true).StoredFile.IsEmpty);

        var section = new ContentBundleTextState(
            new[] { new ContentTextLanguageDeclaration("en", "en") }, Array.Empty<ContentBundleTextValue>());
        Assert.Throws<ArgumentException>(() => new ContentBundle(1, "epoch", 0, Array.Empty<ContentBundleType>(),
            Array.Empty<ContentBundleRow>(), Array.Empty<ContentFamily>(), Array.Empty<RemapRule>(), section));
        Assert.Same(section, new ContentBundle(2, "epoch", 0, Array.Empty<ContentBundleType>(),
            Array.Empty<ContentBundleRow>(), Array.Empty<ContentFamily>(), Array.Empty<RemapRule>(), section).TextState);
    }

    [Fact]
    public async Task A_text_plan_refuses_a_candidate_that_drops_or_changes_a_frozen_set()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Name, "Sword"));
        ContentTextChunkRecord chunk = Chunk("en-us", "Sword");
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en-us", chunk.Hash) });
        var declared = new[] { new ContentTextLanguageDeclaration("en-us", "en-us") };
        var none = Array.Empty<ContentTextRevision>();

        Assert.Throws<ArgumentException>(() => new ContentTextPublishPlan(
            rowPlan, snapshot, new ContentTextCandidate(none, declared, none, none), new[] { chunk }));
        var wrong = new ContentTextRevision(Item, IdOf(rowPlan, "sword"), NameField, "en-us", "Blade", 1, null);
        Assert.Throws<ArgumentException>(() => new ContentTextPublishPlan(
            rowPlan, snapshot, new ContentTextCandidate(new[] { wrong }, declared, none, new[] { wrong }), new[] { chunk }));

        Assert.Empty(await store.ListVersionsAsync());
        Assert.Equal("Sword", (await store.GetOpenDraftAsync())!.TextState!.Edits.Single().Value);
    }

    [Fact]
    public async Task A_text_plan_refuses_a_candidate_that_leaves_a_frozen_remove_live()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await PublishNamedRowAsync(store, "sword", "Sword");
        ContentTextRevision sword = (await text.ReadTextSnapshotAsync(1)).Revisions.Single();
        await ApplyAsync(text, new[] { ContentEdit.Update(Item, sword.DefinitionId, Sword, new[] { Value(5) }) },
            ContentTextEdit.Remove(Target(NameField, "en")));
        var declared = new[] { new ContentTextLanguageDeclaration("en", "en") };
        var none = Array.Empty<ContentTextRevision>();

        ContentTextChunkRecord kept = Chunk("en", "Sword");
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en", kept.Hash) });
        Assert.Throws<ArgumentException>(() => new ContentTextPublishPlan(
            rowPlan, snapshot, new ContentTextCandidate(new[] { sword }, declared, none, none), new[] { kept }));

        ContentTextChunkRecord empty = Chunk("en", string.Empty);
        (snapshot, rowPlan) = await FreezeAndPlanAsync(store, new[] { new ManifestLanguageEntry("en", empty.Hash) });
        var closed = new ContentTextPublishPlan(
            rowPlan, snapshot, new ContentTextCandidate(none, declared, new[] { sword }, none), new[] { empty });
        await text.CommitTextPublishAsync(closed, Request(1), null);
        Assert.Empty((await text.ReadTextSnapshotAsync(2)).Revisions);
    }

    [Fact]
    public async Task A_text_plan_binds_frozen_targets_through_the_row_plan_fork_copy_included()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await PublishNamedRowAsync(store, "sword", "Sword");
        ContentTextRevision sword = (await text.ReadTextSnapshotAsync(1)).Revisions.Single();
        ContentEdit fork = ContentEdit.Fork(
            Item, sword.DefinitionId, Sword, new ContentKey("old_sword"), LegacyField, Array.Empty<ContentFieldEdit>());
        await ApplyAsync(text, new[] { fork }, ContentTextEdit.Set(Target(NameField, "en"), "Blade"));
        ContentTextChunkRecord chunk = Chunk("en", "Blade and Sword");
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en", chunk.Hash) });
        int copy = IdOf(rowPlan, "old_sword");
        var declared = new[] { new ContentTextLanguageDeclaration("en", "en") };

        // The explicit Set lands on the copy and the original keeps the baseline value: swapped.
        var swapped = new ContentTextRevision(Item, copy, NameField, "en", "Blade", 2, null);
        Assert.Throws<ArgumentException>(() => new ContentTextPublishPlan(rowPlan, snapshot, new ContentTextCandidate(
            new[] { sword, swapped }, declared, Array.Empty<ContentTextRevision>(), new[] { swapped }), new[] { chunk }));

        var renamed = new ContentTextRevision(Item, sword.DefinitionId, NameField, "en", "Blade", 2, null);
        var copied = new ContentTextRevision(Item, copy, NameField, "en", "Sword", 2, null);
        var plan = new ContentTextPublishPlan(rowPlan, snapshot, new ContentTextCandidate(
            new[] { renamed, copied }, declared, new[] { sword }, new[] { renamed, copied }), new[] { chunk });
        await text.CommitTextPublishAsync(plan, Request(1), null);
        var published = (await text.ReadTextSnapshotAsync(2)).Revisions.ToDictionary(r => r.DefinitionId, r => r.Value);
        Assert.Equal("Blade", published[sword.DefinitionId]);
        Assert.Equal("Sword", published[copy]);
    }

    static void AssertSignature(Type contract, string name, Type returns, params Type[] parameters)
    {
        MethodInfo? method = contract.GetMethod(name, parameters);
        Assert.NotNull(method);
        Assert.Equal(returns, method.ReturnType);
        Assert.True(method.GetParameters()[^1].HasDefaultValue);
    }
}
