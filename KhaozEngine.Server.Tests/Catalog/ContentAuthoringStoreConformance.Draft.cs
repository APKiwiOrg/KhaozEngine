using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Facts 4, 5, 6 and 21, the DRAFT half: what a key is, what a second edit of one target does, what an update
/// means, and what a refused publish leaves behind.
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>
    /// FACT 4. A key differing only in CASE is a different key. This is the binary collation assertion, and it
    /// is taken at the draft rather than at a publish because the key rules refuse an upper-case key at the
    /// validator, so two keys that differ only in case never reach a published version.
    /// <para>
    /// A case-insensitive column collation, which is the ordinary SQL Server default, would either refuse the
    /// second edit on the unique index or overwrite the first, and both show up here as ONE edit.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task Fact04_AKeyDifferingOnlyInCaseIsADifferentKey()
    {
        IContentAuthoringStore store = await OpenAsync();

        await ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("alpha"), CatalogFixtures.Fields(1)),
            ContentEdit.Add(Thing, new ContentKey("ALPHA"), CatalogFixtures.Fields(2)));

        ContentDraft draft = await DraftAsync(store);
        Assert.Equal(2, draft.EditCount);
        Assert.True(draft.Changes.TryGet(new ContentEditTarget(Thing, 0, new ContentKey("alpha")), out ContentEdit? lower));
        Assert.True(draft.Changes.TryGet(new ContentEditTarget(Thing, 0, new ContentKey("ALPHA")), out ContentEdit? upper));
        Assert.Equal(1, lower.Fields[0].Value.Number);
        Assert.Equal(2, upper.Fields[0].Value.Number);
    }

    /// <summary>
    /// FACT 5. Two <c>Add</c> edits for the same key in one draft COLLIDE on
    /// <c>catalog_draft_edit</c>'s unique index over (type, definition id, key), and the collision resolves as
    /// a replace: the newer edit takes the slot the older one holds, so the draft never carries two intents
    /// for one row and the publish writes ONE row.
    /// <para>
    /// A second edit under a DIFFERENT operation is the other half of the same index, and it is REFUSED rather
    /// than replaced, because an update followed by a retire would silently flip the first edit's operation
    /// and drop its fields.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task Fact05_TwoAddsOfOneKeyCollideOnTheUniqueIndex()
    {
        IContentAuthoringStore store = await OpenAsync();

        await ApplyAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await ApplyAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(22)));

        ContentDraft draft = await DraftAsync(store);
        Assert.Equal(1, draft.EditCount);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => ApplyAsync(store, ContentEdit.Retire(Thing, 0, new ContentKey("one"), ContentRetirePolicy.Placeholder, 0)));
        Assert.Equal(ContentAuthoringException.EditTargetCollisionReason, refused.Reason);

        await store.PublishAsync(Request(0));

        ContentRowPage page = await RowsAsync(store);
        Assert.Equal(1, page.Total);
        Assert.Equal(22, page.Rows[0].Fields[0].Number);
    }

    /// <summary>
    /// FACT 6. An <c>Update</c> MERGES fields rather than replacing the row's field set. An update naming one
    /// field leaves every field it does not name exactly as it was, which is what lets a console save one cell
    /// of a grid without sending the row back.
    /// </summary>
    [Fact]
    public virtual async Task Fact06_AnUpdateMergesFieldsRatherThanReplacingTheRow()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(
            store,
            ContentEdit.Add(
                Thing,
                new ContentKey("one"),
                [
                    new ContentFieldEdit(CatalogFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 11)),
                    new ContentFieldEdit(CatalogFixtures.LegacyField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1)),
                ]));

        await PublishAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(99)));

        ContentRowPage page = await RowsAsync(store);
        ContentRow row = page.Rows[0];
        Assert.Equal(99, row.Fields[0].Number);

        // The field the update never named. A replace would have dropped it, and a row that quietly lost a
        // field is the defect this fact exists for.
        Assert.False(row.Fields[1].IsAbsent);
        Assert.Equal(1, row.Fields[1].Number);
    }

    /// <summary>
    /// FACT 21. A publish that fails at the VALIDATOR leaves the draft intact. The work of authoring a change
    /// set is the expensive thing an operator did, and a refusal that also discarded it would make every
    /// finding cost the whole draft.
    /// </summary>
    [Fact]
    public virtual async Task Fact21_APublishRefusedByTheValidatorLeavesTheDraftIntact()
    {
        IContentAuthoringStore store = await OpenAsync();

        // An upper-case key is malformed under the key rules, so the candidate carries a finding and the
        // publish never reaches step 9. The draft carries a legal edit beside it, which is what has to survive.
        await ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)),
            ContentEdit.Add(Thing, new ContentKey("NOT_A_LEGAL_KEY"), CatalogFixtures.Fields(22)));

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.PublishAsync(Request(0)));
        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, refused.Reason);
        Assert.NotEmpty(refused.Findings);

        ContentDraft draft = await DraftAsync(store);
        Assert.Equal(2, draft.EditCount);
        Assert.Equal(0, await store.GetActiveVersionAsync());
        Assert.Empty((await RowsAsync(store)).Rows);
    }
}
