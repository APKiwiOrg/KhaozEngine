using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The guarded row freeze and its recorded-base release on a TEXT-capable store. The row freeze keeps every refusal
/// the row-only freeze has, before any marker is written, and the release leaves the text state of a draft the text
/// freeze held exactly as it was.
/// </summary>
public abstract partial class ContentAuthoringTextStoreConformance
{
    /// <summary>
    /// A draft with no row work is refused <c>no-open-draft</c> even when it holds text, a draft mixing rows, text
    /// and a language introduction is refused <c>text-unrepresented</c>, and so is a row-only fork of a row whose
    /// active version holds text. Each refusal leaves the marker, the draft and the audit untouched.
    /// </summary>
    [Fact]
    public virtual async Task ConditionalFreeze_RefusesUnrepresentedTextBeforeWriting()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        var safe = Assert.IsAssignableFrom<IContentConditionalDraftFreeze>(store);
        await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "en", "Sword"));
        await store.PublishAsync(Request(0));

        // Text work alone is no row work.
        ContentDraft textOnly = await ApplyAsync(store, null, Set("sword", DescriptionField, "fr", "Lame"));
        Assert.Equal(0, textOnly.EditCount);
        int audits = (await AuditAsync(store)).Count;
        var empty = await Assert.ThrowsAsync<ContentAuthoringException>(() => safe.FreezeDraftForBaseAsync(1));
        Assert.Equal(ContentAuthoringException.NoOpenDraftReason, empty.Reason);
        ContentAuthoringStoreConformance.AssertDraftEqual(textOnly, (await store.GetOpenDraftAsync())!);
        Assert.Equal(audits, (await AuditAsync(store)).Count);

        // Rows, a text intent and a language introduction together.
        ContentDraft mixed = await ApplyAsync(store, new[] { Add("shield") });
        Assert.Equal(1, mixed.EditCount);
        Assert.Equal(1, mixed.LanguageIntroductionCount);
        audits = (await AuditAsync(store)).Count;
        var unrepresented = await Assert.ThrowsAsync<ContentAuthoringException>(() => safe.FreezeDraftForBaseAsync(1));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, unrepresented.Reason);
        ContentAuthoringStoreConformance.AssertDraftEqual(mixed, (await store.GetOpenDraftAsync())!);
        Assert.Equal(audits, (await AuditAsync(store)).Count);

        // A row-only fork of a row whose active version holds text.
        Assert.True(await store.TryDiscardChangesAsync(mixed, Actor, Operator));
        int sword = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows[0].Id;
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Fork(Item, sword, new ContentKey("sword"), new ContentKey("old_sword"), "legacy", Array.Empty<ContentFieldEdit>()) },
            Actor,
            Operator,
            "fork");
        ContentDraft fork = (await store.GetOpenDraftAsync())!;
        audits = (await AuditAsync(store)).Count;
        var forked = await Assert.ThrowsAsync<ContentAuthoringException>(() => safe.FreezeDraftForBaseAsync(1));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, forked.Reason);
        ContentAuthoringStoreConformance.AssertDraftEqual(fork, (await store.GetOpenDraftAsync())!);
        Assert.Null((await store.GetOpenDraftAsync())!.FrozenForBaseVersion);
        Assert.Equal(audits, (await AuditAsync(store)).Count);
    }

    /// <summary>
    /// A text draft frozen through the unchanged text freeze is released by the recorded base only, and neither the
    /// refused nor the clearing release changes its row edits, text intents, introductions, opener or audit.
    /// </summary>
    [Fact]
    public virtual async Task ConditionalRelease_PreservesTextAndIntroductions()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        var safe = Assert.IsAssignableFrom<IContentConditionalDraftFreeze>(store);
        await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "fr", "Epee"));
        await ApplyAsync(store, null, Set("sword", DescriptionField, "en", "A blade"));
        ContentTextPublishSnapshot snapshot = await store.FreezeChangesAsync(0);
        ContentDraft frozen = (await store.GetOpenDraftAsync())!;
        ContentAuthoringStoreConformance.AssertDraftEqual(snapshot.Draft, frozen);
        Assert.Equal(2, frozen.LanguageIntroductionCount);
        int audits = (await AuditAsync(store)).Count;

        Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(1));
        ContentAuthoringStoreConformance.AssertDraftEqual(frozen, (await store.GetOpenDraftAsync())!);

        Assert.True(await safe.ReleaseDraftFreezeForBaseAsync(0));
        ContentAuthoringStoreConformance.AssertDraftEqual(
            ContentAuthoringStoreConformance.Unfrozen(frozen), (await store.GetOpenDraftAsync())!);
        Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(0));
        Assert.Equal(audits, (await AuditAsync(store)).Count);
    }
}
