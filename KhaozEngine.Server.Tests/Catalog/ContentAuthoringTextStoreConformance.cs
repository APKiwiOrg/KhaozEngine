using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The TEXT AUTHORING companion's provider conformance: one abstract class with one concrete subclass per
/// backend, the in-memory reference first. Every fact drives <see cref="IContentTextAuthoringStore"/> and the
/// row-only seam through their public members and asserts observable behaviour, never a table.
/// <para>
/// <b>Every fact is virtual.</b> A backend gated by an environment variable overrides each one with its own
/// gated attribute and calls the base body, which is how the SQL Server subclass joins under
/// <c>KE_CATALOG_SQLSERVER</c> without a fact that silently passes when the server is absent.
/// </para>
/// </summary>
public abstract partial class ContentAuthoringTextStoreConformance
{
    /// <summary>The actor every fixture call carries.</summary>
    protected const string Actor = "text-conformance";

    /// <summary>The operator every fixture call carries.</summary>
    protected const string Operator = "oid:text-conformance";

    /// <summary>The localized name marker of the fixture type.</summary>
    protected const string NameField = "name";

    /// <summary>The localized description marker of the fixture type.</summary>
    protected const string DescriptionField = "description";

    /// <summary>A SERVER-only localized marker, which text may not target.</summary>
    protected const string SecretField = "secret_name";

    /// <summary>The CLIENT-visible item type every fact authors text against.</summary>
    protected static readonly ContentTypeId Item = new(1024);

    /// <summary>A fresh registry carrying the fixture type, per store, never ambient.</summary>
    protected static ContentTypeRegistry TextRegistry()
    {
        var schema = new ContentFieldSchema(new ContentFieldEntry[]
        {
            new("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new("legacy", ContentFieldKind.Bool, null, ContentVisibility.Client, false),
            new(NameField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, false),
            new(DescriptionField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, false),
            new(SecretField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.ServerOnly, false),
        });
        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(
            ContentRegistrationBand.Game, Item.Value, "item", new ItemCodec(schema), null, schema, ContentVisibility.Client, 256);
        return registry;
    }

    /// <summary>An INITIALIZED store over an empty database and its own pack target, reading the given clock.</summary>
    /// <param name="clock">The clock every stamp is read from, or null for the system clock.</param>
    protected abstract Task<IContentTextAuthoringStore> OpenAsync(Func<DateTimeOffset>? clock = null);

    /// <summary>Makes the next text audit write fail, and undoes it on dispose. Row audit writes still succeed.</summary>
    /// <param name="store">The store whose text audit write is to fail.</param>
    protected abstract IDisposable ArmATextAuditFault(IContentTextAuthoringStore store);

    /// <summary>The pack target a store was opened over.</summary>
    /// <param name="store">The store.</param>
    protected abstract IPackStore PackOf(IContentTextAuthoringStore store);

    [Fact]
    public virtual async Task Text01_MixedApplyIsAtomicAndItsAuditCarriesTheLanguage()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        ContentDraft draft = await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "EN-us", "Sword"));
        Assert.Equal(1, draft.EditCount);
        Assert.Equal(1, draft.TextEditCount);
        Assert.Equal("en-us", Assert.Single(draft.TextState!.Introductions).WireTag);
        ContentAuditEntry text = (await AuditAsync(store)).Single(entry => entry.LanguageTag is not null);
        Assert.Equal(("en-us", NameField, "Sword"), (text.LanguageTag, text.FieldName, text.AfterValue));
        int audits = (await AuditAsync(store)).Count;

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => ApplyAsync(
            store, new[] { Add("shield") }, Set("sword", NameField, "en-us", "Changed"), Set("sword", "value", "en-us", "No")));
        Assert.Equal(ContentAuthoringException.TextTargetIneligibleReason, refused.Reason);
        ContentDraft held = (await store.GetOpenDraftAsync())!;
        Assert.Equal(1, held.EditCount);
        Assert.Equal("Sword", Assert.Single(held.TextState!.Edits).Value);
        Assert.Equal(audits, (await AuditAsync(store)).Count);

        var hidden = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => ApplyAsync(store, null, Set("sword", SecretField, "en", "Hidden")));
        Assert.Equal(ContentAuthoringException.TextTargetIneligibleReason, hidden.Reason);
        var ghost = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => ApplyAsync(store, null, Set("ghost", NameField, "en", "Nobody")));
        Assert.Equal(ContentAuthoringException.UnknownRowReason, ghost.Reason);
    }

    [Fact]
    public virtual async Task Text02_SetThenRemoveKeepsTheIntroductionAndOnlyAnUndeclaredRemoveIsRefused()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "fr", "Epee"));
        ContentDraft removed = await ApplyAsync(store, null, Remove("sword", NameField, "fr"));
        Assert.Equal("fr", Assert.Single(removed.TextState!.Introductions).Language);
        Assert.Equal(ContentTextEditOperation.Remove, Assert.Single(removed.TextState.Edits).Operation);

        ContentDraft absent = await ApplyAsync(store, null, Remove("sword", DescriptionField, "fr"));
        Assert.Single(absent.TextState!.Edits);

        var undeclared = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => ApplyAsync(store, null, Remove("sword", NameField, "de")));
        Assert.Equal(ContentAuthoringException.TextLanguageUndeclaredReason, undeclared.Reason);

        ContentDraft again = await ApplyAsync(store, null, Set("sword", NameField, "FR", "Lame"));
        Assert.Single(again.TextState!.Introductions);
        Assert.Equal("Lame", Assert.Single(again.TextState.Edits).Value);
    }

    [Fact]
    public virtual async Task Text03_TheLastIntentWinsAndKeepsItsFirstOrdinal()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "en", "first"));
        await ApplyAsync(store, null, Set("sword", DescriptionField, "en", "middle"));
        ContentDraft draft = await ApplyAsync(store, null, Set("sword", NameField, "EN", "last"));

        Assert.Equal(new[] { NameField, DescriptionField }, draft.TextState!.Edits.Select(edit => edit.Target.FieldName));
        Assert.Equal(new[] { "last", "middle" }, draft.TextState.Edits.Select(edit => edit.Value));
    }

    [Fact]
    public virtual async Task Text04_ATextAuditFaultTakesTheWholeMixedBatchDown()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        using (ArmATextAuditFault(store))
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "en", "Sword")));
        }

        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Empty(await AuditAsync(store));
    }

    [Fact]
    public virtual async Task Text05_AFullValueIsHeldWhileItsAuditIsAbbreviatedOnAWholeCharacter()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        string value = Utf8Value(ContentTextEdit.MaxValueBytes);
        ContentDraft draft = await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "en", value));
        Assert.Equal(value, Assert.Single(draft.TextState!.Edits).Value);
        Assert.Equal(value, Assert.Single((await store.GetOpenDraftAsync())!.TextState!.Edits).Value);

        string after = (await AuditAsync(store)).Single(entry => entry.LanguageTag is not null).AfterValue!;
        Assert.True(after.Length <= ContentAuditEntry.MaxValueLength);
        Assert.EndsWith(ContentTextAuditRendering.CutMarker, after, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(after[^(ContentTextAuditRendering.CutMarker.Length + 1)]));

        await store.PublishAsync(Request(0));
        Assert.Equal(value, Assert.Single((await store.ReadTextSnapshotAsync(1)).Revisions).Value);
    }

    [Fact]
    public virtual async Task Text06_EveryNewCommitIsCompleteAndAMixedPublishLandsTogether()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        await store.ApplyEditsAsync(new[] { Add("shield") }, Actor, Operator, "row only");
        await store.PublishAsync(Request(0));
        ContentVersionTextSnapshot rowOnly = await store.ReadTextSnapshotAsync(1);
        Assert.Empty(rowOnly.Languages);
        Assert.Empty(rowOnly.Revisions);

        await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "en", "Sword"));
        ContentPublishResult published = await store.PublishAsync(Request(1));
        ContentVersionTextSnapshot text = await store.ReadTextSnapshotAsync(published.VersionNumber);
        Assert.Equal(await store.GetStoreEpochAsync(), text.StoreEpoch);
        ContentTextRevision sword = Assert.Single(text.Revisions);
        Assert.Equal(("Sword", 2, (int?)null), (sword.Value, sword.ValidFromVersion, sword.ReplacedInVersion));
        ContentTextLanguage english = Assert.Single(text.Languages);
        Assert.Equal(Hash("en", ("item.sword.name", "Sword")), english.Hash);
        Assert.Equal("en", Assert.Single((await store.ReadPublishBaselineAsync()).Languages).Tag);
        Assert.Null(await store.GetOpenDraftAsync());
        ContentAuditEntry audit = (await AuditAsync(store))
            .Single(entry => entry.Action == ContentAuditActions.Publish && entry.LanguageTag is not null);
        Assert.Equal(("en", "Sword", 2), (audit.LanguageTag, audit.AfterValue, audit.VersionNumber));

        // A row-only publish carries the language record, and the value stays live.
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Update(Item, sword.DefinitionId, new ContentKey("sword"), Value(9)) }, Actor, Operator, "reprice");
        await store.PublishAsync(Request(2));
        ContentVersionTextSnapshot carried = await store.ReadTextSnapshotAsync(3);
        Assert.Equal(english, Assert.Single(carried.Languages));
        Assert.Equal(sword, Assert.Single(carried.Revisions));

        var zero = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.ReadTextSnapshotAsync(0));
        Assert.Equal(ContentAuthoringException.UnknownVersionReason, zero.Reason);
        var missing = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.ReadTextSnapshotAsync(9));
        Assert.Equal(ContentAuthoringException.UnknownVersionReason, missing.Reason);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadTextSnapshotAsync(-1));
    }

    [Fact]
    public virtual async Task Text07_SetThenRemovePublishesAnEmptyLanguageAndRemovingTheLastValueKeepsIt()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "fr", "Epee"));
        await ApplyAsync(store, null, Remove("sword", NameField, "fr"));
        await store.PublishAsync(Request(0));
        ContentTextLanguage french = Assert.Single((await store.ReadTextSnapshotAsync(1)).Languages);
        Assert.Equal(Hash("fr"), french.Hash);

        await ApplyAsync(store, null, Set("sword", NameField, "fr", "Epee"));
        await store.PublishAsync(Request(1));
        await ApplyAsync(store, null, Remove("sword", NameField, "fr"));
        await store.PublishAsync(Request(2));
        ContentVersionTextSnapshot emptied = await store.ReadTextSnapshotAsync(3);
        Assert.Empty(emptied.Revisions);
        Assert.Equal(Hash("fr"), Assert.Single(emptied.Languages).Hash);
        Assert.Equal("Epee", Assert.Single((await store.ReadTextSnapshotAsync(2)).Revisions).Value);
    }

    [Fact]
    public virtual async Task Text08_FreezeRefusesAMovedOrEmptyBaseAndAFrozenDraftTakesNoWrite()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        var empty = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.FreezeChangesAsync(0));
        Assert.Equal(ContentAuthoringException.NoOpenDraftReason, empty.Reason);

        await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "en", "Sword"));
        var moved = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.FreezeChangesAsync(4));
        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, moved.Reason);
        Assert.False((await store.GetOpenDraftAsync())!.IsFrozen);

        ContentTextPublishSnapshot snapshot = await store.FreezeChangesAsync(0);
        Assert.Equal(0, snapshot.Draft.FrozenForBaseVersion);
        Assert.Empty(snapshot.BaselineText.Languages);
        var late = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => ApplyAsync(store, null, Set("sword", NameField, "en", "late")));
        Assert.Equal(ContentAuthoringException.PublishInProgressReason, late.Reason);
        Assert.False(await store.TryDiscardChangesAsync(snapshot.Draft, Actor, Operator));
        var discard = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.DiscardDraftAsync(Actor, Operator));
        Assert.Equal(ContentAuthoringException.PublishInProgressReason, discard.Reason);
        Assert.Equal("Sword", Assert.Single((await store.GetOpenDraftAsync())!.TextState!.Edits).Value);
    }

    [Fact]
    public virtual async Task Text09_TheExpectedDraftDiscardKeepsARivalAndAuditsEveryCategory()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        ContentDraft proof = await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "en", "Sword"));
        await ApplyAsync(store, null, Set("sword", DescriptionField, "fr", "Lame"));
        ContentDraft rival = await ApplyAsync(store, null, Remove("sword", DescriptionField, "fr"));
        int audits = (await AuditAsync(store)).Count;

        Assert.False(await store.TryDiscardChangesAsync(proof, Actor, Operator));
        var forged = new ContentDraft(
            new ContentDraftTextState(rival.TextState!.Edits, rival.TextState.Introductions.Take(1).ToArray()),
            rival.BaseVersion, rival.OpenedBy, rival.OpenedAtUtc, rival.Note, rival.Changes);
        Assert.False(await store.TryDiscardChangesAsync(forged, Actor, Operator));
        Assert.Equal(2, (await store.GetOpenDraftAsync())!.LanguageIntroductionCount);
        Assert.Equal(audits, (await AuditAsync(store)).Count);

        Assert.True(await store.TryDiscardChangesAsync((await store.GetOpenDraftAsync())!, Actor, Operator));
        Assert.Null(await store.GetOpenDraftAsync());
        Dictionary<string, string?> discards = (await AuditAsync(store))
            .Where(entry => entry.Action == ContentAuditActions.DraftDiscard)
            .ToDictionary(entry => entry.FieldName, entry => entry.BeforeValue);
        Assert.Equal("1", discards[string.Empty]);
        Assert.Equal("2", discards["text-edits"]);
        Assert.Equal("2", discards["language-introductions"]);
    }

    [Fact]
    public virtual async Task Text10_AnOldDtoReconstructionIsRefusedByTheBackend()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        ContentDraft complete = await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "en", "Sword"));
        var old = new ContentDraft(
            complete.BaseVersion, complete.OpenedBy, complete.OpenedAtUtc, complete.Note, complete.Changes);
        int audits = (await AuditAsync(store)).Count;

        Assert.False(await store.TryDiscardChangesAsync(old, Actor, Operator));
        var discard = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.DiscardDraftAsync(Actor, Operator));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, discard.Reason);
        var freeze = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.FreezeDraftAsync(0));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, freeze.Reason);
        Assert.False((await store.GetOpenDraftAsync())!.IsFrozen);
        Assert.Equal(audits, (await AuditAsync(store)).Count);

        // An old wrapper exposing only the row-only seam takes the legacy publish route, which is refused at
        // the backend's guarded freeze before any file or row is written. Its forwarding base declares that
        // freeze and still hides the text companion.
        var view = new RowOnlyView(store);
        var legacy = new ContentPublishCommit(
            view, PackOf(store), new ContentPublisher(view, (IContentIdPersistence)store, TextRegistry()));
        var published = await Assert.ThrowsAsync<ContentAuthoringException>(() => legacy.PublishAsync(Request(0)));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, published.Reason);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Equal("Sword", Assert.Single((await store.GetOpenDraftAsync())!.TextState!.Edits).Value);
    }

    [Fact]
    public virtual async Task Text11_LegacyRollbackRefusesTextVersionsAndRunsOnTextFreeOnes()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        await store.ApplyEditsAsync(new[] { Add("shield") }, Actor, Operator, "add");
        await store.PublishAsync(Request(0));
        int shield = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().Id;
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Update(Item, shield, new ContentKey("shield"), Value(5)) }, Actor, Operator, "update");
        await store.PublishAsync(Request(1));
        Assert.Equal(1, (await store.RollbackToAsync(1, Actor, Operator, "rollback")).EditCount);
        Assert.NotNull(await store.ExportBundleAsync(2));
        await store.PublishAsync(Request(2));

        await ApplyAsync(store, null, Set("shield", NameField, "en", "Shield"));
        await store.PublishAsync(Request(3));
        int audits = (await AuditAsync(store)).Count;
        var rollback = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(1, Actor, Operator, "rollback"));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, rollback.Reason);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(audits, (await AuditAsync(store)).Count);
    }

    [Fact]
    public virtual async Task Text12_ALegacyForkOfARowHoldingTextIsRefusedAndTheCompanionCopiesIt()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "en", "Sword"));
        await store.PublishAsync(Request(0));
        int sword = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().Id;
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Fork(Item, sword, new ContentKey("sword"), new ContentKey("old_sword"), "legacy", Array.Empty<ContentFieldEdit>()) },
            Actor,
            Operator,
            "fork");

        var freeze = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.FreezeDraftAsync(1));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, freeze.Reason);

        await store.PublishAsync(Request(1));
        Dictionary<string, string> names = (await store.ReadTextSnapshotAsync(2)).Revisions
            .ToDictionary(revision => revision.DefinitionId == sword ? "sword" : "copy", revision => revision.Value);
        Assert.Equal("Sword", names["sword"]);
        Assert.Equal("Sword", names["copy"]);
    }

    [Fact]
    public virtual async Task Text14_ATextPlanWhoseDraftChangedSinceItsFreezeIsRefusedAtCommit()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "en", "Sword"));

        // Between the pack writes and the commit, a rival releases the freeze, changes the text and freezes
        // again for the same base, so the plan in flight names a draft the store no longer holds.
        bool raced = false;
        var commit = new ContentPublishCommit(
            store,
            PackOf(store),
            new ContentPublisher(store, (IContentIdPersistence)store, TextRegistry(), step =>
            {
                if (step != ContentPublishStep.BeforeCommit || raced)
                {
                    return;
                }

                raced = true;
                Task.Run(async () =>
                {
                    await store.ClearDraftFreezeAsync();
                    await ApplyAsync(store, null, Set("sword", NameField, "en", "Rival"));
                    await store.FreezeChangesAsync(0);
                }).GetAwaiter().GetResult();
            }));

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => commit.PublishAsync(Request(0)));
        Assert.Equal(ContentAuthoringException.TextStateMismatchReason, refused.Reason);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Equal("Rival", Assert.Single((await store.GetOpenDraftAsync())!.TextState!.Edits).Value);

        await store.PublishAsync(Request(0));
        Assert.Equal("Rival", Assert.Single((await store.ReadTextSnapshotAsync(1)).Revisions).Value);
    }

    /// <summary>Applies one batch with the fixture's actor, operator and note.</summary>
    protected static Task<ContentDraft> ApplyAsync(
        IContentTextAuthoringStore store, IEnumerable<ContentEdit>? rows, params ContentTextEdit[] text)
        => store.ApplyChangesAsync(
            new ContentAuthoringChanges((rows ?? Array.Empty<ContentEdit>()).ToArray(), text), Actor, Operator, "text conformance");

    /// <summary>A plain add of one fixture row.</summary>
    protected static ContentEdit Add(string key) => ContentEdit.Add(Item, new ContentKey(key), Value(1));

    /// <summary>The required value field.</summary>
    protected static ContentFieldEdit[] Value(int value)
        => [new ContentFieldEdit("value", ContentFieldValue.OfNumber(ContentFieldKind.Int, value))];

    /// <summary>A Set of one string.</summary>
    protected static ContentTextEdit Set(string key, string field, string language, string value)
        => ContentTextEdit.Set(new ContentTextTarget(Item, new ContentKey(key), field, language), value);

    /// <summary>A Remove of one string.</summary>
    protected static ContentTextEdit Remove(string key, string field, string language)
        => ContentTextEdit.Remove(new ContentTextTarget(Item, new ContentKey(key), field, language));

    /// <summary>A publish request standing on one base version.</summary>
    protected static ContentPublishRequest Request(int expectedBaseVersion)
        => new(Actor, Operator, "text conformance", expectedBaseVersion);

    /// <summary>Every audit row, newest first.</summary>
    protected static Task<IReadOnlyList<ContentAuditEntry>> AuditAsync(IContentAuthoringStore store)
        => store.ListAuditAsync(default, 0, 0, 500);

    /// <summary>The literal KECT hash the shipped codec computes for one language's entries.</summary>
    protected static string Hash(string tag, params (string Key, string Value)[] entries)
        => ContentTextChunkCodec.Hash(
            tag, entries.Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value)).ToArray());

    /// <summary>A UTF-8 value of exactly <paramref name="bytes"/> bytes carrying an astral character at the cut.</summary>
    protected static string Utf8Value(int bytes)
    {
        string value = new string('a', ContentAuditEntry.MaxValueLength - 6) + "\U0001F600";
        return value + new string('b', bytes - Encoding.UTF8.GetByteCount(value));
    }

    /// <summary>
    /// A view of a store exposing only the row-only seam and the guarded freeze its forwarding base declares,
    /// which is the route an old wrapper takes. It hides the text companion.
    /// </summary>
    sealed class RowOnlyView(IContentAuthoringStore inner) : ForwardingContentAuthoringStore(inner);

    sealed class ItemCodec(ContentFieldSchema schema) : ContentRowCodecBase(new ContentTypeId(1024), schema);
}
