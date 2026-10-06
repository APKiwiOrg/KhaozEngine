using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The GUARDED freeze of <see cref="IContentConditionalDraftFreeze"/>: a row freeze that compares its expected base
/// with the active version, checks the draft and its representability, writes the marker and reads the draft back in
/// one atomic step, and a release that clears only the marker its own freeze recorded.
/// <para>
/// Every fact reads through the seam, so a refusal is proven to have written nothing by an unchanged draft, an
/// unchanged marker and an unchanged audit count. The relational update times live in the timestamp facts.
/// </para>
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>
    /// A freeze for a base the store does not stand at is refused with nothing written, a freeze for the active
    /// base returns the complete frozen draft exactly as the draft read reports it, and a repeat returns the same.
    /// A marker naming the ACTIVE version is not stale, so a freeze expecting an older base leaves it standing.
    /// </summary>
    [Fact]
    public virtual async Task ConditionalFreeze_ValidatesBaseAndReturnsCompleteDraft()
    {
        IContentAuthoringStore store = await OpenAsync();
        var safe = Assert.IsAssignableFrom<IContentConditionalDraftFreeze>(store);
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await ApplyAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(22)));
        int auditBefore = await AuditCountAsync(store);

        var moved = await Assert.ThrowsAsync<ContentAuthoringException>(() => safe.FreezeDraftForBaseAsync(0));
        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, moved.Reason);
        Assert.Null((await DraftAsync(store)).FrozenForBaseVersion);

        ContentDraft frozen = await safe.FreezeDraftForBaseAsync(1);
        Assert.Equal(1, frozen.FrozenForBaseVersion);
        Assert.Equal(1, frozen.EditCount);
        AssertDraftEqual(frozen, await DraftAsync(store));
        AssertDraftEqual(frozen, await safe.FreezeDraftForBaseAsync(1));
        Assert.Equal(auditBefore, await AuditCountAsync(store));

        // Version 2 lands, and a fresh draft on it is frozen for 2 by the publisher standing there.
        await store.ClearDraftFreezeAsync();
        await store.PublishAsync(Request(1));
        await ApplyAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(33)));
        ContentDraft winner = await safe.FreezeDraftForBaseAsync(2);
        int auditAtTwo = await AuditCountAsync(store);

        var late = await Assert.ThrowsAsync<ContentAuthoringException>(() => safe.FreezeDraftForBaseAsync(1));
        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, late.Reason);
        AssertDraftEqual(winner, await DraftAsync(store));
        Assert.Equal(2, (await DraftAsync(store)).FrozenForBaseVersion);
        Assert.Equal(auditAtTwo, await AuditCountAsync(store));
    }

    /// <summary>
    /// A marker a dead publish left naming an older base is NOT swept by a refused guarded freeze: the call expecting
    /// a base the store has moved past writes nothing, the stale marker included. A freeze for the active base then
    /// replaces it directly, and a release naming another base leaves the new marker alone.
    /// </summary>
    [Fact]
    public virtual async Task ConditionalFreeze_MovedBasePreservesStaleMarkerAndTimestamp()
    {
        IContentAuthoringStore store = await OpenAsync();
        var safe = Assert.IsAssignableFrom<IContentConditionalDraftFreeze>(store);
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await PublishAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(22)));
        await ApplyAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(33)));
        await store.FreezeDraftAsync(0);
        ContentDraft before = await DraftAsync(store);
        int auditBefore = await AuditCountAsync(store);

        var staleMoved = await Assert.ThrowsAsync<ContentAuthoringException>(() => safe.FreezeDraftForBaseAsync(1));
        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, staleMoved.Reason);
        AssertDraftEqual(before, await DraftAsync(store));
        Assert.Equal(0, (await DraftAsync(store)).FrozenForBaseVersion);

        ContentDraft replaced = await safe.FreezeDraftForBaseAsync(2);
        Assert.Equal(2, replaced.FrozenForBaseVersion);
        AssertDraftEqual(replaced, await DraftAsync(store));
        Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(1));
        Assert.Equal(2, (await DraftAsync(store)).FrozenForBaseVersion);
        Assert.Equal(auditBefore, await AuditCountAsync(store));

        // The legacy clear stays the unconditional recovery lever.
        await store.ClearDraftFreezeAsync();
        Assert.Null((await DraftAsync(store)).FrozenForBaseVersion);
    }

    /// <summary>
    /// The release clears the marker only when it names the recorded base, answers whether it cleared, and is a
    /// no-op on any later call. It compares the marker alone, never the active version, so a stale marker is
    /// released by the base it names. Edits, opener and audit are untouched throughout.
    /// </summary>
    [Fact]
    public virtual async Task ConditionalRelease_ClearsOnlyMatchingBase()
    {
        IContentAuthoringStore store = await OpenAsync();
        var safe = Assert.IsAssignableFrom<IContentConditionalDraftFreeze>(store);
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await ApplyAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(22)));
        ContentDraft frozen = await safe.FreezeDraftForBaseAsync(1);
        int auditBefore = await AuditCountAsync(store);

        Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(0));
        AssertDraftEqual(frozen, await DraftAsync(store));
        Assert.True(await safe.ReleaseDraftFreezeForBaseAsync(1));
        ContentDraft released = await DraftAsync(store);
        Assert.Null(released.FrozenForBaseVersion);
        AssertDraftEqual(Unfrozen(frozen), released);
        Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(1));
        AssertDraftEqual(released, await DraftAsync(store));

        // A stale marker, older than the active version, is cleared by the base it names.
        await store.FreezeDraftAsync(0);
        Assert.True(await safe.ReleaseDraftFreezeForBaseAsync(0));
        AssertDraftEqual(released, await DraftAsync(store));
        Assert.Equal(auditBefore, await AuditCountAsync(store));
    }

    /// <summary>
    /// The argument and empty-draft contract: a negative base is refused on both members, base 0 freezes a staged
    /// draft on a catalog that has published nothing, a store with no open draft refuses the freeze with
    /// <c>no-open-draft</c> after its base check and answers the release with false, and a cancelled token writes
    /// nothing.
    /// </summary>
    [Fact]
    public virtual async Task ConditionalFreeze_RejectsNegativeBaseAndEmptyDraft()
    {
        // A catalog with no published version, but with an applied nonempty row draft, supports base zero.
        IContentAuthoringStore staged = await OpenAsync();
        var emptySafe = Assert.IsAssignableFrom<IContentConditionalDraftFreeze>(staged);
        await ApplyAsync(staged, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => emptySafe.FreezeDraftForBaseAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => emptySafe.ReleaseDraftFreezeForBaseAsync(-1));
        Assert.Null((await DraftAsync(staged)).FrozenForBaseVersion);

        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => emptySafe.FreezeDraftForBaseAsync(0, cancelled.Token));
            Assert.Null((await DraftAsync(staged)).FrozenForBaseVersion);
        }

        Assert.Equal(0, (await emptySafe.FreezeDraftForBaseAsync(0)).FrozenForBaseVersion);
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => emptySafe.ReleaseDraftFreezeForBaseAsync(0, cancelled.Token));
            Assert.Equal(0, (await DraftAsync(staged)).FrozenForBaseVersion);
        }

        // A published catalog with no open draft. The base check comes first, so a moved base names the move.
        IContentAuthoringStore published = await ResetToEmptyAsync();
        var noDraftSafe = Assert.IsAssignableFrom<IContentConditionalDraftFreeze>(published);
        await PublishAsync(published, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        int currentBase = await published.GetActiveVersionAsync();
        int auditBefore = await AuditCountAsync(published);

        Assert.False(await noDraftSafe.ReleaseDraftFreezeForBaseAsync(5));
        Assert.Equal(ContentAuthoringException.NoOpenDraftReason, (await Assert.ThrowsAsync<ContentAuthoringException>(
            () => noDraftSafe.FreezeDraftForBaseAsync(currentBase))).Reason);
        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, (await Assert.ThrowsAsync<ContentAuthoringException>(
            () => noDraftSafe.FreezeDraftForBaseAsync(currentBase - 1))).Reason);
        Assert.Null(await published.GetOpenDraftAsync());
        Assert.Equal(auditBefore, await AuditCountAsync(published));
    }

    /// <summary>
    /// Asserts two drafts are the same complete draft by value: base, opener, stamp, note, marker, every row edit
    /// in order with its field values, and the text intents and introductions in order.
    /// </summary>
    /// <param name="expected">The draft expected.</param>
    /// <param name="actual">The draft read.</param>
    internal static void AssertDraftEqual(ContentDraft expected, ContentDraft actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        Assert.Equal(expected.BaseVersion, actual.BaseVersion);
        Assert.Equal(expected.OpenedBy, actual.OpenedBy);
        Assert.Equal(expected.OpenedAtUtc, actual.OpenedAtUtc);
        Assert.Equal(expected.Note, actual.Note);
        Assert.Equal(expected.FrozenForBaseVersion, actual.FrozenForBaseVersion);
        Assert.Equal(expected.EditCount, actual.EditCount);
        for (int i = 0; i < expected.EditCount; i++)
        {
            ContentEdit want = expected.Changes.Edits[i];
            ContentEdit got = actual.Changes.Edits[i];
            Assert.Equal(
                (want.Type, want.DefinitionId, want.Key, want.Operation, want.RetirePolicy, want.ReplacementId),
                (got.Type, got.DefinitionId, got.Key, got.Operation, got.RetirePolicy, got.ReplacementId));
            Assert.Equal(
                (want.ForkKey, want.ForkFlagField, want.FamilyId, want.ImportedAsRetired),
                (got.ForkKey, got.ForkFlagField, got.FamilyId, got.ImportedAsRetired));
            Assert.Equal(want.Fields, got.Fields);
        }

        Assert.Equal(expected.TextEditCount, actual.TextEditCount);
        Assert.Equal(expected.LanguageIntroductionCount, actual.LanguageIntroductionCount);
        Assert.True(
            (expected.TextState ?? ContentDraftTextState.Empty).IsSameAs(actual.TextState ?? ContentDraftTextState.Empty),
            "The text intents or language introductions differ.");
    }

    /// <summary>The same draft with no freeze marker, which is what a release is expected to leave.</summary>
    /// <param name="draft">The frozen draft.</param>
    internal static ContentDraft Unfrozen(ContentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new ContentDraft(
            draft.TextState ?? ContentDraftTextState.Empty,
            draft.BaseVersion,
            draft.OpenedBy,
            draft.OpenedAtUtc,
            draft.Note,
            draft.Changes,
            null);
    }

    /// <summary>How many audit rows the store holds, which a refusal or a marker write must leave unchanged.</summary>
    /// <param name="store">The store.</param>
    static async Task<int> AuditCountAsync(IContentAuthoringStore store)
        => (await store.ListAuditAsync(default, 0, 0, 1000)).Count;
}
