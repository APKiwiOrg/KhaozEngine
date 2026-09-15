using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Facts 16 and 17, the AUDIT half: what an audit row carries, and what happens when writing one fails.
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>
    /// FACT 16. Every audit row carries a BEFORE and an AFTER for a field change. An audit that recorded only
    /// the new value would answer "what is it now", which the row table already answers, and not "what was it"
    /// which is the only question an audit is read for.
    /// </summary>
    [Fact]
    public virtual async Task Fact16_EveryAuditRowCarriesABeforeAndAnAfter()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await PublishAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(99)));

        IReadOnlyList<ContentAuditEntry> entries = await store.ListAuditAsync(Thing, 1, 0, 100);

        ContentAuditEntry changed = Assert.Single(
            entries,
            entry => string.Equals(entry.Action, ContentAuditActions.Publish, StringComparison.Ordinal)
                && entry.VersionNumber == 2
                && string.Equals(entry.FieldName, CatalogFixtures.ValueField, StringComparison.Ordinal));

        Assert.Equal("11", changed.BeforeValue);
        Assert.Equal("99", changed.AfterValue);
        Assert.Equal(CatalogFixtures.Actor, changed.Actor);
        Assert.Equal(CatalogFixtures.Operator, changed.Operator);
        Assert.Equal("one", changed.Key.ToString());

        // The add is the other half of the pair: no before, because there was no row, and an after.
        ContentAuditEntry added = Assert.Single(
            entries,
            entry => string.Equals(entry.Action, ContentAuditActions.Publish, StringComparison.Ordinal)
                && entry.VersionNumber == 1
                && string.Equals(entry.FieldName, CatalogFixtures.ValueField, StringComparison.Ordinal));
        Assert.Null(added.BeforeValue);
        Assert.Equal("11", added.AfterValue);
    }

    /// <summary>
    /// FACT 17. An audit insert failure ROLLS BACK the edit. The default this rejects is the common one: the
    /// audit append is IN the same transaction as the edit rather than best effort, because a content edit
    /// with no audit row is indistinguishable from no edit.
    /// <para>
    /// Each backend arms the failure the way its own store can be made to fail, which is what
    /// <see cref="ArmAnAuditWriteFault"/> is for. What is asserted here is the same on all three: the edit is
    /// not in the draft afterwards, no audit row was written for it, and the store takes the edit normally
    /// once the fault is gone.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task Fact17_AnAuditInsertFailureRollsBackTheEdit()
    {
        IContentAuthoringStore store = await OpenAsync();

        // A draft that is already OPEN, so the only thing left for the edit below to fail at is the audit.
        await ApplyAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        int auditBefore = (await store.ListAuditAsync(default, 0, 0, 500)).Count;

        using (ArmAnAuditWriteFault(store))
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => ApplyAsync(store, ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22))));
        }

        ContentDraft draft = await DraftAsync(store);
        Assert.Equal(1, draft.EditCount);
        Assert.False(draft.Changes.TryGet(new ContentEditTarget(Thing, 0, new ContentKey("two")), out _));
        Assert.Equal(auditBefore, (await store.ListAuditAsync(default, 0, 0, 500)).Count);

        // The fault is gone, so the same edit lands, which is what makes the rollback a rollback rather than a
        // store that is now wedged.
        await ApplyAsync(store, ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)));
        Assert.Equal(2, (await DraftAsync(store)).EditCount);
        Assert.True(auditBefore < (await store.ListAuditAsync(default, 0, 0, 500)).Count);
    }
}
