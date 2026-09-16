using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Fact 26, the STORE-LEVEL half of fact 17: a change and the audit row describing it land together or
/// neither does, on the two paths that are not a draft edit.
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>
    /// FACT 26. A store-level change whose audit write fails is NOT made. Fact 17 pins the draft edit, and
    /// the pin and the discard are the same claim about the other two writers: a change with no audit row
    /// against it is indistinguishable from no change, which is the whole reason the audit exists.
    /// <para>
    /// A provider gets this from the one transaction it already runs each of these in. The in-memory
    /// reference had to be taught it, because a gate is not a transaction: it moved the pin and dropped the
    /// draft and THEN appended, so an append that failed left the change with nothing recording it
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/927). The remedy is the one fact 17 already drove,
    /// render the entry first and append it after the change.
    /// </para>
    /// <para>
    /// The second half is what keeps it honest: with the fault gone, both changes have to land, so a store
    /// that simply refused them would not pass.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task Fact26_AStoreLevelChangeWhoseAuditWriteFailsIsNotMade()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        // An open draft for the discard to have something to take, and a version for the pin to name.
        await ApplyAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(99)));
        int auditBefore = (await store.ListAuditAsync(default, 0, 0, 500)).Count;

        using (ArmAnAuditWriteFault(store))
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => store.SetPinnedVersionAsync(1, CatalogFixtures.Actor, CatalogFixtures.Operator));
            await Assert.ThrowsAnyAsync<Exception>(
                () => store.DiscardDraftAsync(CatalogFixtures.Actor, CatalogFixtures.Operator));
        }

        Assert.Null(await store.GetPinnedVersionAsync());
        Assert.Equal(1, (await DraftAsync(store)).EditCount);
        Assert.Equal(auditBefore, (await store.ListAuditAsync(default, 0, 0, 500)).Count);

        // The fault is gone, so both land, which is what makes the refusal a rollback rather than a store
        // that is now wedged.
        await store.SetPinnedVersionAsync(1, CatalogFixtures.Actor, CatalogFixtures.Operator);
        Assert.Equal(1, await store.GetPinnedVersionAsync());

        await store.DiscardDraftAsync(CatalogFixtures.Actor, CatalogFixtures.Operator);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.True(auditBefore < (await store.ListAuditAsync(default, 0, 0, 500)).Count);
    }
}
