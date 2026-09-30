using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Store-level changes and their audits land together, including a family's first reservation.
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

    /// <summary>An audit failure leaves an empty catalog with no family or first reservation.</summary>
    [Fact]
    public virtual Task AnInitialFamilyCreationWhoseAuditWriteFailsReservesNothing()
        => AssertFamilyCreationAtomicAsync(existingFamily: false);

    /// <summary>An audit failure preserves existing families and marks, and a retry can allocate.</summary>
    [Fact]
    public virtual Task AFamilyCreationWhoseAuditWriteFailsPreservesExistingReservations()
        => AssertFamilyCreationAtomicAsync(existingFamily: true);

    /// <summary>A refused first block changes neither marks nor audit, and plain allocation still works.</summary>
    [Fact]
    public virtual async Task AFamilyCreationRefusedAtTheCeilingReservesNothing()
    {
        IContentAuthoringStore store = await OpenAsync();
        IContentIdPersistence ids = Ids(store);
        IReadOnlyList<ContentAuditEntry> auditBefore = await store.ListAuditAsync(default, 0, 0, 500);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.CreateFamilyAsync(Capped, "too_large", 16, CatalogFixtures.Actor, CatalogFixtures.Operator));

        Assert.Equal(ContentAuthoringException.IdCeilingReason, refused.Reason);
        Assert.Empty(await store.ListFamiliesAsync(Capped));
        Assert.Equal(new ContentIdHighWater(0, 0), await ids.ReadHighWaterAsync(Capped));
        Assert.Equal(auditBefore, await store.ListAuditAsync(default, 0, 0, 500));
        Assert.Equal(1, await store.AllocateAsync(Capped, 1));
    }

    /// <summary>A cancelled create exposes no reservation and leaves its key available for retry.</summary>
    [Fact]
    public virtual async Task ACancelledFamilyCreationReservesNothing()
    {
        IContentAuthoringStore store = await OpenAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateFamilyAsync(
            Thing, "retry", 16, CatalogFixtures.Actor, CatalogFixtures.Operator, cancelled.Token));

        Assert.Empty(await store.ListFamiliesAsync(Thing));
        Assert.Equal(new ContentIdHighWater(0, 0), await Ids(store).ReadHighWaterAsync(Thing));
        Assert.Empty(await store.ListAuditAsync(default, 0, 0, 500));
        ContentFamily created = await store.CreateFamilyAsync(
            Thing, "retry", 16, CatalogFixtures.Actor, CatalogFixtures.Operator);
        Assert.Equal(16, Assert.Single(created.Blocks).BaseId);
        Assert.Equal(16, await store.AllocateInFamilyAsync(created.FamilyId));
    }

    async Task AssertFamilyCreationAtomicAsync(bool existingFamily)
    {
        IContentAuthoringStore store = await OpenAsync();
        IContentIdPersistence ids = Ids(store);
        int expectedBase = 16;
        if (existingFamily)
        {
            ContentFamily held = await store.CreateFamilyAsync(
                Thing, "held", 16, CatalogFixtures.Actor, CatalogFixtures.Operator);
            Assert.Equal(16, await store.AllocateInFamilyAsync(held.FamilyId));
            Assert.Equal(32, await store.AllocateAsync(Thing, 3));
            expectedBase = 1056;
        }

        IReadOnlyList<ContentFamily> familiesBefore = await store.ListFamiliesAsync(Thing);
        ContentIdHighWater markBefore = await ids.ReadHighWaterAsync(Thing);
        IReadOnlyList<ContentAuditEntry> auditBefore = await store.ListAuditAsync(default, 0, 0, 500);

        using (ArmAnAuditWriteFault(store))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => store.CreateFamilyAsync(
                Thing, "retry", 16, CatalogFixtures.Actor, CatalogFixtures.Operator));
        }

        IReadOnlyList<ContentFamily> familiesAfter = await store.ListFamiliesAsync(Thing);
        Assert.Equal(familiesBefore.Count, familiesAfter.Count);
        for (int i = 0; i < familiesBefore.Count; i++)
        {
            Assert.Equal(familiesBefore[i].FamilyId, familiesAfter[i].FamilyId);
            Assert.Equal(familiesBefore[i].FamilyKey, familiesAfter[i].FamilyKey);
            Assert.Equal(familiesBefore[i].Blocks, familiesAfter[i].Blocks);
        }

        Assert.Equal(markBefore, await ids.ReadHighWaterAsync(Thing));
        Assert.Equal(auditBefore, await store.ListAuditAsync(default, 0, 0, 500));

        ContentFamily created = await store.CreateFamilyAsync(
            Thing, "retry", 16, CatalogFixtures.Actor, CatalogFixtures.Operator);
        ContentFamilyBlock block = Assert.Single(created.Blocks);
        Assert.Equal(expectedBase, block.BaseId);
        Assert.Equal(expectedBase, block.NextFreeId);
        Assert.Equal(0, block.BlockOrdinal);
        Assert.Equal(1, block.ReservedInVersion);
        Assert.Equal(new ContentIdHighWater(expectedBase + 15, expectedBase + 15),
            await ids.ReadHighWaterAsync(Thing));

        IReadOnlyList<ContentAuditEntry> auditAfter = await store.ListAuditAsync(default, 0, 0, 500);
        Assert.Equal(auditBefore.Count + 1, auditAfter.Count);
        Assert.Equal(ContentAuditActions.FamilyCreate, auditAfter[0].Action);
        Assert.Equal(new ContentKey("retry"), auditAfter[0].Key);
        Assert.Equal(expectedBase.ToString(System.Globalization.CultureInfo.InvariantCulture), auditAfter[0].AfterValue);

        Assert.Equal(expectedBase, await store.AllocateInFamilyAsync(created.FamilyId));
        Assert.Equal(expectedBase + 1, await store.AllocateInFamilyAsync(created.FamilyId));
        Assert.Equal(expectedBase + 16, await store.AllocateAsync(Thing, 1));
        Assert.Equal(expectedBase + 2, Assert.Single(
            (await ids.ReadFamilyAsync(created.FamilyId))!.Blocks).NextFreeId);
    }
}
