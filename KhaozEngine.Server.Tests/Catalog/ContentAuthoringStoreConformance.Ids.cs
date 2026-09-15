using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Facts 13, 14, 15 and 23, the ALLOCATOR half: reserve before issue, no id twice, a family inside its own
/// aligned blocks, and the plain counter staying out of them.
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>
    /// FACT 13. Allocation RESERVES before it ISSUES, asserted by reading the two marks after a single
    /// allocate: the reserved mark stands a whole batch ahead of the issued one, which is only possible if the
    /// reservation was written first.
    /// <para>
    /// The order is the contract and the batch size is not. What the order buys is that a crash between the
    /// two writes skips ids that were never issued instead of reissuing one, and a duplicate definition id is
    /// the single failure the allocator exists to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task Fact13_AllocationReservesBeforeItIssues()
    {
        IContentAuthoringStore store = await OpenAsync();
        IContentIdPersistence ids = Ids(store);

        Assert.Equal(new ContentIdHighWater(0, 0), await ids.ReadHighWaterAsync(Thing));

        int first = await store.AllocateAsync(Thing, 1);

        Assert.Equal(1, first);
        ContentIdHighWater mark = await ids.ReadHighWaterAsync(Thing);
        Assert.Equal(1, mark.IssuedThrough);
        Assert.Equal(ContentIdAllocator.ReserveBatch, mark.ReservedThrough);
        Assert.True(mark.ReservedThrough > mark.IssuedThrough);
    }

    /// <summary>
    /// FACT 14. Two allocations never return the same id, across 10,000 in a loop. Ids are never reused
    /// (contracts 5.1), so a duplicate is not a collision to be handled later: it is two definitions at one
    /// address in every pack, every save file and every trade log that already named it.
    /// </summary>
    [Fact]
    public virtual async Task Fact14_NoIdIsEverIssuedTwice()
    {
        IContentAuthoringStore store = await OpenAsync();
        var seen = new HashSet<int>();

        for (int i = 0; i < 10_000; i++)
        {
            int id = await store.AllocateAsync(Thing, 1);
            Assert.True(seen.Add(id), FormattableString.Invariant($"Id {id} was issued twice, at allocation {i + 1}."));
        }

        Assert.Equal(10_000, seen.Count);
        ContentIdHighWater mark = await Ids(store).ReadHighWaterAsync(Thing);
        Assert.Equal(10_000, mark.IssuedThrough);
        Assert.True(mark.ReservedThrough >= mark.IssuedThrough);
    }

    /// <summary>
    /// FACT 15. A family allocation stays INSIDE its aligned block, and reserves a second block when the first
    /// is full. Family membership is an arithmetic test over the id (contracts 5.2), so an id outside the
    /// block is a row that is in the family by column and not by id.
    /// </summary>
    [Fact]
    public virtual async Task Fact15_AFamilyStaysInItsBlockAndReservesASecondWhenFull()
    {
        const int blockSize = 16;
        IContentAuthoringStore store = await OpenAsync();
        ContentFamily family = await store.CreateFamilyAsync(
            Thing, "weapons", blockSize, CatalogFixtures.Actor, CatalogFixtures.Operator);

        ContentFamilyBlock first = Assert.Single(family.Blocks);
        Assert.Equal(0, first.BaseId % blockSize);

        var issued = new List<int>();
        for (int i = 0; i < blockSize; i++)
        {
            issued.Add(await store.AllocateInFamilyAsync(family.FamilyId));
        }

        Assert.All(issued, id => Assert.True(
            first.Contains(id),
            FormattableString.Invariant($"Id {id} is outside block [{first.BaseId}, {first.TopExclusive}).")));
        Assert.Equal(blockSize, new HashSet<int>(issued).Count);

        // The block is full, so the next allocation has to reserve another one, aligned, and the id comes out
        // of that one.
        int overflow = await store.AllocateInFamilyAsync(family.FamilyId);

        IReadOnlyList<ContentFamily> families = await store.ListFamiliesAsync(Thing);
        ContentFamily reread = Assert.Single(families);
        Assert.Equal(2, reread.Blocks.Count);
        ContentFamilyBlock second = reread.Blocks[1];
        Assert.Equal(0, second.BaseId % blockSize);
        Assert.NotEqual(first.BaseId, second.BaseId);
        Assert.True(second.Contains(overflow));
        Assert.True(reread.Contains(overflow));
    }

    /// <summary>
    /// FACT 23. A PLAIN allocation taken after a family block is reserved never returns an id inside that
    /// block, asserted by draining the whole gap under the block and then some.
    /// <para>
    /// Reserving a block advances the type's ISSUED mark to the block top, which is what keeps the plain
    /// counter out of the block and is why the plain path needs no knowledge of families at all. Without that
    /// advance the counter walks up through the gap under the block and hands out an id inside it a second
    /// time, to a row that is not in the family.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task Fact23_APlainAllocationNeverLandsInsideAFamilyBlock()
    {
        const int blockSize = 16;
        IContentAuthoringStore store = await OpenAsync();

        // A few plain ids first, so the block does not open at the very bottom of the id space and there IS a
        // gap under it to drain.
        await store.AllocateAsync(Thing, 3);

        ContentFamily family = await store.CreateFamilyAsync(
            Thing, "weapons", blockSize, CatalogFixtures.Actor, CatalogFixtures.Operator);
        ContentFamilyBlock block = Assert.Single(family.Blocks);
        Assert.True(block.BaseId > 3, "The block has to open above the ids already issued for this fact to say anything.");

        // Drain the gap under the block and then some: every id the plain counter can reach from here, past
        // where the block sits, plus a margin on the far side of it.
        int drained = (block.TopExclusive - 3) + 64;
        var plain = new List<int>(drained);
        for (int i = 0; i < drained; i++)
        {
            plain.Add(await store.AllocateAsync(Thing, 1));
        }

        Assert.All(plain, id => Assert.False(
            block.Contains(id),
            FormattableString.Invariant($"Plain id {id} landed inside family block [{block.BaseId}, {block.TopExclusive}).")));
        Assert.Equal(drained, new HashSet<int>(plain).Count);

        // And the family still issues from inside its own block afterwards, so the drain did not consume it.
        int inFamily = await store.AllocateInFamilyAsync(family.FamilyId);
        Assert.True(block.Contains(inFamily));
        Assert.DoesNotContain(inFamily, plain);
    }
}
