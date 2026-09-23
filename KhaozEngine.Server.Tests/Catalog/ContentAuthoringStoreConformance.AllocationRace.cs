using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Two allocations on ONE catalog that interleave, proved the same way on the in-memory reference, SQLite and
/// SQL Server. Two console hosts, or two upgrade runners, allocate from one database with no lock spanning
/// either allocation, so the only thing that keeps their ids apart is what each durable write checks.
/// <para>
/// <b>The interleaving is placed, not hoped for.</b> The allocator under test runs over a wrapped id seam, and a
/// rival's WHOLE allocation, through the store's own allocator, lands immediately before the first write the
/// wrapped allocator makes. Anything that allocator decided from its earlier read is stale by then, which is
/// exactly the window a slow host has.
/// </para>
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>A plain allocation a rival's plain allocation lands inside is issued a different id.</summary>
    [Fact]
    public virtual async Task TwoPlainAllocationsThatInterleaveAreIssuedDifferentIds()
    {
        IContentAuthoringStore store = await OpenAsync();
        int rival = 0;
        var allocator = new ContentIdAllocator(new RivalFirstIdPersistence(
            Ids(store), async () => rival = await store.AllocateAsync(Thing, 1)));

        int mine = await allocator.AllocateAsync(Thing, 1);

        Assert.NotEqual(0, rival);
        Assert.NotEqual(rival, mine);
        Assert.Equal(2, (await Ids(store).ReadHighWaterAsync(Thing)).IssuedThrough);
    }

    /// <summary>An allocation inside a family a rival's allocation in the same family lands inside.</summary>
    [Fact]
    public virtual async Task TwoFamilyAllocationsThatInterleaveAreIssuedDifferentIds()
    {
        IContentAuthoringStore store = await OpenAsync();
        ContentFamily family = await store.CreateFamilyAsync(
            Thing, "swords", ContentFamily.MinBlockSize, CatalogFixtures.Actor, CatalogFixtures.Operator);
        int rival = 0;
        var allocator = new ContentIdAllocator(new RivalFirstIdPersistence(
            Ids(store), async () => rival = await store.AllocateInFamilyAsync(family.FamilyId)));

        int mine = await allocator.AllocateInFamilyAsync(family.FamilyId);

        ContentFamilyBlock block = family.Blocks[0];
        Assert.NotEqual(rival, mine);
        Assert.InRange(rival, block.BaseId, block.TopExclusive - 1);
        Assert.InRange(mine, block.BaseId, block.TopExclusive - 1);
    }

    /// <summary>
    /// A block reservation a rival's block reservation lands inside opens a block of its own, because a family
    /// is a range of ids no other row may take and two families over one range share every id in it.
    /// </summary>
    [Fact]
    public virtual async Task TwoBlockReservationsThatInterleaveNeverOverlap()
    {
        IContentAuthoringStore store = await OpenAsync();
        ContentFamily family = await store.CreateFamilyAsync(
            Thing, "swords", ContentFamily.MinBlockSize, CatalogFixtures.Actor, CatalogFixtures.Operator);
        var allocator = new ContentIdAllocator(new RivalFirstIdPersistence(
            Ids(store),
            () => store.CreateFamilyAsync(
                Thing, "shields", ContentFamily.MinBlockSize, CatalogFixtures.Actor, CatalogFixtures.Operator)));

        await allocator.ReserveBlockAsync(family.FamilyId);

        var blocks = new List<ContentFamilyBlock>();
        foreach (ContentFamily held in await store.ListFamiliesAsync(Thing))
        {
            blocks.AddRange(held.Blocks);
        }

        Assert.Equal(3, blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            for (int j = i + 1; j < blocks.Count; j++)
            {
                bool apart = blocks[i].TopExclusive <= blocks[j].BaseId || blocks[j].TopExclusive <= blocks[i].BaseId;
                Assert.True(apart, FormattableString.Invariant(
                    $"Blocks [{blocks[i].BaseId}, {blocks[i].TopExclusive}) and [{blocks[j].BaseId}, {blocks[j].TopExclusive}) overlap."));
            }
        }
    }

    /// <summary>
    /// A plain allocation a rival's block reservation lands inside takes an id above the block rather than
    /// refusing, because the block moved the issued mark past the one the allocation read.
    /// </summary>
    [Fact]
    public virtual async Task APlainAllocationThatABlockReservationPassesTakesAnIdAboveTheBlock()
    {
        IContentAuthoringStore store = await OpenAsync();
        await store.AllocateAsync(Thing, 1);
        ContentFamily? rival = null;
        var allocator = new ContentIdAllocator(new RivalFirstIdPersistence(
            Ids(store),
            async () => rival = await store.CreateFamilyAsync(
                Thing, "shields", ContentFamily.MinBlockSize, CatalogFixtures.Actor, CatalogFixtures.Operator)));

        int mine = await allocator.AllocateAsync(Thing, 1);

        Assert.NotNull(rival);
        Assert.True(
            mine >= rival.Blocks[0].TopExclusive,
            FormattableString.Invariant($"Id {mine} is not above the rival's block [{rival.Blocks[0].BaseId}, {rival.Blocks[0].TopExclusive})."));
    }

    /// <summary>
    /// Many plain allocations at once, over the store's own allocator, are all issued different ids and none of
    /// them is refused. It is the unplaced companion of the facts above, and on SQL Server it is also what says
    /// two allocations on one row wait for each other rather than deadlocking.
    /// </summary>
    [Fact]
    public virtual async Task ManyConcurrentPlainAllocationsAreAllIssuedDifferentIds()
    {
        const int Hosts = 8;
        const int Each = 20;
        IContentAuthoringStore store = await OpenAsync();
        await store.AllocateAsync(Thing, 1);

        var runs = new Task<List<int>>[Hosts];
        for (int h = 0; h < Hosts; h++)
        {
            runs[h] = Task.Run(async () =>
            {
                var issued = new List<int>(Each);
                for (int i = 0; i < Each; i++)
                {
                    issued.Add(await store.AllocateAsync(Thing, 1));
                }

                return issued;
            });
        }

        var seen = new HashSet<int> { 1 };
        foreach (List<int> issued in await Task.WhenAll(runs))
        {
            foreach (int id in issued)
            {
                Assert.True(seen.Add(id), FormattableString.Invariant($"Id {id} was issued twice."));
            }
        }

        Assert.Equal(1 + (Hosts * Each), seen.Count);
    }

    /// <summary>
    /// Forwards every call, and runs the rival once, immediately before the FIRST write reaches the store.
    /// </summary>
    sealed class RivalFirstIdPersistence(IContentIdPersistence inner, Func<Task> rival) : IContentIdPersistence
    {
        bool _fired;

        public Task<ContentIdHighWater> ReadHighWaterAsync(
            ContentTypeId type, CancellationToken cancellationToken = default)
            => inner.ReadHighWaterAsync(type, cancellationToken);

        public Task<int?> ReadMaxDefinitionIdAsync(
            ContentTypeId type, CancellationToken cancellationToken = default)
            => inner.ReadMaxDefinitionIdAsync(type, cancellationToken);

        public async Task CommitReservedThroughAsync(
            ContentTypeId type, int reservedThrough, CancellationToken cancellationToken = default)
        {
            await RivalFirstAsync().ConfigureAwait(false);
            await inner.CommitReservedThroughAsync(type, reservedThrough, cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> CommitIssueAsync(
            ContentTypeId type, int count, CancellationToken cancellationToken = default)
        {
            await RivalFirstAsync().ConfigureAwait(false);
            return await inner.CommitIssueAsync(type, count, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> CommitCarriedThroughAsync(
            ContentTypeId type, int carriedThrough, CancellationToken cancellationToken = default)
        {
            await RivalFirstAsync().ConfigureAwait(false);
            return await inner.CommitCarriedThroughAsync(type, carriedThrough, cancellationToken).ConfigureAwait(false);
        }

        public Task<ContentFamily?> ReadFamilyAsync(long familyId, CancellationToken cancellationToken = default)
            => inner.ReadFamilyAsync(familyId, cancellationToken);

        public async Task<ContentFamilyBlock?> CommitFamilyBlockAsync(
            long familyId, int baseId, int issuedThrough, CancellationToken cancellationToken = default)
        {
            await RivalFirstAsync().ConfigureAwait(false);
            return await inner.CommitFamilyBlockAsync(familyId, baseId, issuedThrough, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<int> CommitFamilyIssueAsync(
            long familyId, int blockOrdinal, CancellationToken cancellationToken = default)
        {
            await RivalFirstAsync().ConfigureAwait(false);
            return await inner.CommitFamilyIssueAsync(familyId, blockOrdinal, cancellationToken).ConfigureAwait(false);
        }

        async Task RivalFirstAsync()
        {
            if (_fired)
            {
                return;
            }

            _fired = true;
            await rival().ConfigureAwait(false);
        }
    }
}
