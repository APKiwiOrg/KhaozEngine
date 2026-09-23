using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Validation;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Authoring;

/// <summary>
/// The reserve-then-issue allocator of spec 4.7 and contracts 6.2, against the in-memory store.
/// <para>
/// The ORDER is the contract and the batch size is not. A range is RESERVED durably BEFORE any id in it is
/// issued, so the worst a crash can do is skip a block of ids that were never issued, and it can never
/// reissue one. Two tests assert the order: one reads the high-water mark after a single allocate and finds
/// the reservation already above the issued id, and one records the write sequence and finds the reservation
/// written first.
/// </para>
/// <para>
/// Nothing here writes process-global state, so no test class in this file enlists in a
/// <c>DisableParallelization</c> collection. Every store is per test.
/// </para>
/// </summary>
public class ContentIdAllocatorTests
{
    const string Actor = "allocator-tests";
    const string Operator = "oid:tests";

    static readonly ContentTypeId Item = new(EngineContentTypes.ItemTypeId);
    static readonly ContentTypeId Game = new(ContentValidationFixtures.GameTypeId);

    static InMemoryContentAuthoringStore EngineStore()
        => new(ContentValidationFixtures.EngineRegistry());

    /// <summary>A store whose one type declares the per-type id CEILING of spec 4.7.</summary>
    static InMemoryContentAuthoringStore CeilingStore(int maxDefinitionId)
        => new(ContentValidationFixtures.GameRegistry(
            ContentValidationFixtures.Schema(
                new ContentFieldEntry("value", ContentFieldKind.Int, null, ContentVisibility.Client, true)),
            maxDefinitionId: maxDefinitionId));

    [Fact]
    public async Task AllocateHandsOutAContiguousRangeAndAdvancesTheIssuedMark()
    {
        InMemoryContentAuthoringStore store = EngineStore();

        int first = await store.AllocateAsync(Item, 4);

        // AllocateAsync returns the FIRST id of the range, which is [first, first + count - 1].
        Assert.Equal(1, first);
        Assert.Equal(4, (await store.ReadHighWaterAsync(Item)).IssuedThrough);
        Assert.Equal(5, await store.AllocateAsync(Item, 2));
        Assert.Equal(6, (await store.ReadHighWaterAsync(Item)).IssuedThrough);
    }

    [Fact]
    public async Task AReservationIsAlreadyAboveTheIssuedIdAfterASingleAllocate()
    {
        // Spec 15.5 fact 13, and the whole of contracts 6.2: a range is reserved durably BEFORE any id in
        // it is issued, so a crash can skip ids that were never issued and can never reissue one.
        InMemoryContentAuthoringStore store = EngineStore();

        int id = await store.AllocateAsync(Item, 1);

        ContentIdHighWater mark = await store.ReadHighWaterAsync(Item);
        Assert.Equal(1, id);
        Assert.Equal(1, mark.IssuedThrough);
        Assert.Equal(ContentIdAllocator.ReserveBatch, mark.ReservedThrough);
        Assert.True(
            mark.ReservedThrough > id,
            "the reservation must already cover ids nothing has issued yet");
    }

    [Fact]
    public async Task TheReservationIsWrittenBeforeTheIssueIsWritten()
    {
        // The order is what a batching optimisation quietly inverts, so it is asserted on the WRITES rather
        // than only on the numbers they leave behind.
        InMemoryContentAuthoringStore store = EngineStore();
        var recorder = new RecordingIdPersistence(store);
        var allocator = new ContentIdAllocator(recorder);

        await allocator.AllocateAsync(Item, 1);

        Assert.Equal(["reserve 1024", "issue 1"], recorder.Writes);
    }

    [Fact]
    public async Task TenThousandAllocationsNeverRepeatAnId()
    {
        // Spec 15.5 fact 14. Ten thousand crosses nine reservation boundaries, so the loop also proves a
        // reservation never re-hands an id the previous one already issued.
        InMemoryContentAuthoringStore store = EngineStore();
        var seen = new HashSet<int>();

        for (int i = 0; i < 10_000; i++)
        {
            int id = await store.AllocateAsync(Item, 1);
            Assert.True(seen.Add(id), FormattableString.Invariant($"id {id} was issued twice"));
        }

        Assert.Equal(10_000, seen.Count);
        Assert.Equal(10_000, (await store.ReadHighWaterAsync(Item)).IssuedThrough);
    }

    [Fact]
    public async Task AllocateRefusesACountBelowOne()
    {
        InMemoryContentAuthoringStore store = EngineStore();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AllocateAsync(Item, 0));
    }

    [Fact]
    public async Task AnAllocationAboveTheCeilingIsRefusedNamingTheTypeTheCeilingAndTheMark()
    {
        InMemoryContentAuthoringStore store = CeilingStore(maxDefinitionId: 8);
        Assert.Equal(1, await store.AllocateAsync(Game, 8));

        ContentAuthoringException error = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.AllocateAsync(Game, 1));

        Assert.Equal(Game, error.Type);
        Assert.Equal(ContentAuthoringException.IdCeilingReason, error.Reason);
        Assert.Contains(
            ContentValidationFixtures.GameTypeId.ToString(CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("ceiling of 8", error.Message, StringComparison.Ordinal);
        Assert.Contains("issued through 8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedAllocationMovesNeitherMark()
    {
        InMemoryContentAuthoringStore store = CeilingStore(maxDefinitionId: 8);
        await store.AllocateAsync(Game, 8);

        await Assert.ThrowsAsync<ContentAuthoringException>(() => store.AllocateAsync(Game, 1));

        ContentIdHighWater mark = await store.ReadHighWaterAsync(Game);
        Assert.Equal(8, mark.IssuedThrough);
        Assert.Equal(8, mark.ReservedThrough);
    }

    [Fact]
    public async Task ARangeThatWouldCrossTheCeilingIsRefusedWholeRatherThanTruncated()
    {
        InMemoryContentAuthoringStore store = CeilingStore(maxDefinitionId: 8);
        await store.AllocateAsync(Game, 6);

        await Assert.ThrowsAsync<ContentAuthoringException>(() => store.AllocateAsync(Game, 4));

        Assert.Equal(6, (await store.ReadHighWaterAsync(Game)).IssuedThrough);
        Assert.Equal(7, await store.AllocateAsync(Game, 2));
    }

    [Fact]
    public async Task TheReservationStopsAtTheCeilingRatherThanOverrunningIt()
    {
        // Step 2 reserves no further than the ceiling, so the durable promise never names an id the format
        // cannot hold.
        InMemoryContentAuthoringStore store = CeilingStore(maxDefinitionId: 255);

        await store.AllocateAsync(Game, 1);

        Assert.Equal(255, (await store.ReadHighWaterAsync(Game)).ReservedThrough);
    }

    [Fact]
    public async Task RetiredRowsKeepTheirIdsSoTheCeilingCountsEveryIdEverIssued()
    {
        // Spec 4.7: ids are never reused, so a retired row keeps its id and the space it occupies. There is
        // no path in this seam that hands an id BACK, and the high-water mark is the proof: it only ever
        // rises, so a type that retires aggressively burns its ceiling exactly as fast as one that does not.
        InMemoryContentAuthoringStore store = CeilingStore(maxDefinitionId: 8);
        await store.AllocateAsync(Game, 8);
        ContentIdHighWater afterIssue = await store.ReadHighWaterAsync(Game);

        await Assert.ThrowsAsync<ContentAuthoringException>(() => store.AllocateAsync(Game, 1));

        ContentIdHighWater afterRefusal = await store.ReadHighWaterAsync(Game);
        Assert.Equal(afterIssue, afterRefusal);
        Assert.Equal(8, afterRefusal.IssuedThrough);
    }

    [Fact]
    public async Task AFamilyReservesAnAlignedBlockAtCreationAndIssuesFromIt()
    {
        InMemoryContentAuthoringStore store = EngineStore();

        ContentFamily family = await store.CreateFamilyAsync(Item, "sword", 16, Actor, Operator);

        ContentFamilyBlock block = Assert.Single(family.Blocks);
        Assert.Equal(0, block.BaseId % 16);
        Assert.Equal(block.BaseId, block.NextFreeId);

        int first = await store.AllocateInFamilyAsync(family.FamilyId);
        int second = await store.AllocateInFamilyAsync(family.FamilyId);

        Assert.Equal(block.BaseId, first);
        Assert.Equal(block.BaseId + 1, second);
        Assert.True(block.Contains(first), "the membership test of contracts 5.2 answers for an issued id");
        Assert.True(block.Contains(second));
    }

    [Fact]
    public async Task AFullBlockReservesASecondBlockForTheSameFamily()
    {
        InMemoryContentAuthoringStore store = EngineStore();
        ContentFamily family = await store.CreateFamilyAsync(Item, "sword", 16, Actor, Operator);
        ContentFamilyBlock first = Assert.Single(family.Blocks);

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(first.BaseId + i, await store.AllocateInFamilyAsync(family.FamilyId));
        }

        int seventeenth = await store.AllocateInFamilyAsync(family.FamilyId);

        ContentFamily? reread = await store.ReadFamilyAsync(family.FamilyId);
        Assert.NotNull(reread);
        Assert.Equal(2, reread.Blocks.Count);
        ContentFamilyBlock second = reread.Blocks[1];
        Assert.Equal(1, second.BlockOrdinal);
        Assert.Equal(0, second.BaseId % 16);
        Assert.Equal(second.BaseId, seventeenth);
        Assert.False(first.Contains(seventeenth), "a second block is a DIFFERENT aligned range");
        Assert.True(reread.Contains(seventeenth), "the family's membership test is the loop over both blocks");
    }

    [Fact]
    public async Task APlainAllocationTakenAfterAFamilyBlockNeverReturnsAnIdInsideIt()
    {
        // Spec 15.5 fact 23, and the bug spec 4.7 spends a page on. Reserving a block raises only
        // reserved_through, and step 1's single guard would then walk the plain counter through the gap
        // under the block and into it. The family path ADVANCES issued_through past the block top, which is
        // what keeps step 1 out of the block with no knowledge of families at all.
        InMemoryContentAuthoringStore store = EngineStore();
        for (int i = 0; i < 10; i++)
        {
            await store.AllocateAsync(Item, 1);
        }

        ContentIdHighWater beforeFamily = await store.ReadHighWaterAsync(Item);
        Assert.Equal(10, beforeFamily.IssuedThrough);
        Assert.Equal(1024, beforeFamily.ReservedThrough);

        ContentFamily sword = await store.CreateFamilyAsync(Item, "sword", 16, Actor, Operator);
        ContentFamilyBlock block = Assert.Single(sword.Blocks);
        Assert.Equal(1024, block.BaseId);
        Assert.Equal(1040, block.TopExclusive);
        Assert.Equal(1024, await store.AllocateInFamilyAsync(sword.FamilyId));

        ContentIdHighWater afterFamily = await store.ReadHighWaterAsync(Item);
        Assert.Equal(1039, afterFamily.ReservedThrough);
        Assert.Equal(1039, afterFamily.IssuedThrough);

        // Drain the whole gap under the block and then some. Without the advance the plain counter climbs
        // 11, 12 and on to 1023, and the next one hands out 1024 a SECOND time, to a row not in the family.
        Assert.Equal(1040, await store.AllocateAsync(Item, 1));
        for (int i = 0; i < 1_100; i++)
        {
            int id = await store.AllocateAsync(Item, 1);
            Assert.False(
                block.Contains(id),
                FormattableString.Invariant($"plain id {id} landed inside the sword block at {block.BaseId}"));
        }
    }

    [Fact]
    public async Task ANewBlockNeverOverlapsAnIdTheTypeHasAlreadyIssued()
    {
        // The rounding is "round the type's reserved_through UP to the block size", and at an exactly
        // aligned mark that is the mark itself. When every reserved id has also been ISSUED, that block
        // would open on an id already stamped onto a row, which is the reuse contracts 5.1 forbids. The
        // floor is therefore the reserved mark or the first UNISSUED id, whichever is higher.
        InMemoryContentAuthoringStore store = EngineStore();
        await store.AllocateAsync(Item, ContentIdAllocator.ReserveBatch);
        ContentIdHighWater exhausted = await store.ReadHighWaterAsync(Item);
        Assert.Equal(1024, exhausted.IssuedThrough);
        Assert.Equal(1024, exhausted.ReservedThrough);

        ContentFamily family = await store.CreateFamilyAsync(Item, "sword", 16, Actor, Operator);

        ContentFamilyBlock block = Assert.Single(family.Blocks);
        Assert.Equal(1040, block.BaseId);
        Assert.False(block.Contains(1024), "id 1024 is already issued to a plain row");
    }

    [Fact]
    public async Task ABlockWhoseTopWouldCrossTheCeilingIsRefusedWithNothingWritten()
    {
        InMemoryContentAuthoringStore store = CeilingStore(maxDefinitionId: 100);

        ContentAuthoringException error = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.CreateFamilyAsync(Game, "epic", 64, Actor, Operator));

        Assert.Equal(ContentAuthoringException.IdCeilingReason, error.Reason);
        Assert.Contains("ceiling of 100", error.Message, StringComparison.Ordinal);
        ContentIdHighWater mark = await store.ReadHighWaterAsync(Game);
        Assert.Equal(0, mark.ReservedThrough);
        Assert.Equal(0, mark.IssuedThrough);
    }

    [Fact]
    public async Task AllocatingFromAnUnknownFamilyIsRefused()
    {
        InMemoryContentAuthoringStore store = EngineStore();

        ContentAuthoringException error = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.AllocateInFamilyAsync(404));

        Assert.Equal(ContentAuthoringException.UnknownFamilyReason, error.Reason);
        Assert.Contains("404", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(131072)]
    public async Task AFamilyBlockSizeOutsideThePowersOfTwoInRangeIsRefused(int blockSize)
    {
        InMemoryContentAuthoringStore store = EngineStore();

        await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.CreateFamilyAsync(Item, "sword", blockSize, Actor, Operator));
    }

    [Fact]
    public async Task ATakenFamilyKeyIsRefusedWithinItsTypeAndFreeInAnother()
    {
        InMemoryContentAuthoringStore store = EngineStore();
        await store.CreateFamilyAsync(Item, "sword", 16, Actor, Operator);

        await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.CreateFamilyAsync(Item, "sword", 16, Actor, Operator));

        ContentFamily other = await store.CreateFamilyAsync(
            new ContentTypeId(EngineContentTypes.StatTypeId), "sword", 16, Actor, Operator);
        Assert.Equal("sword", other.FamilyKey);
    }

    [Fact]
    public void TheReserveBatchIsTheThousandAndTwentyFourOfSpecFourSeven()
    {
        Assert.Equal(1024, ContentIdAllocator.ReserveBatch);
    }

    /// <summary>
    /// Forwards every call to the store and records the WRITE order, which is the half of contracts 6.2 the
    /// numbers alone cannot show: a store that issued first and reserved afterwards would leave the same two
    /// numbers behind.
    /// </summary>
    sealed class RecordingIdPersistence(IContentIdPersistence inner) : IContentIdPersistence
    {
        readonly List<string> _writes = [];

        public IReadOnlyList<string> Writes => _writes;

        public Task<ContentIdHighWater> ReadHighWaterAsync(
            ContentTypeId type, CancellationToken cancellationToken = default)
            => inner.ReadHighWaterAsync(type, cancellationToken);

        public Task<int?> ReadMaxDefinitionIdAsync(
            ContentTypeId type, CancellationToken cancellationToken = default)
            => inner.ReadMaxDefinitionIdAsync(type, cancellationToken);

        public Task CommitReservedThroughAsync(
            ContentTypeId type, int reservedThrough, CancellationToken cancellationToken = default)
        {
            _writes.Add(FormattableString.Invariant($"reserve {reservedThrough}"));
            return inner.CommitReservedThroughAsync(type, reservedThrough, cancellationToken);
        }

        public async Task<int> CommitIssueAsync(
            ContentTypeId type, int count, CancellationToken cancellationToken = default)
        {
            int first = await inner.CommitIssueAsync(type, count, cancellationToken).ConfigureAwait(false);
            _writes.Add(FormattableString.Invariant($"issue {first + count - 1}"));
            return first;
        }

        public Task<bool> CommitCarriedThroughAsync(
            ContentTypeId type, int carriedThrough, CancellationToken cancellationToken = default)
        {
            _writes.Add(FormattableString.Invariant($"carried {carriedThrough}"));
            return inner.CommitCarriedThroughAsync(type, carriedThrough, cancellationToken);
        }

        public Task<ContentFamily?> ReadFamilyAsync(
            long familyId, CancellationToken cancellationToken = default)
            => inner.ReadFamilyAsync(familyId, cancellationToken);

        public Task<ContentFamilyBlock?> CommitFamilyBlockAsync(
            long familyId, int baseId, int issuedThrough, CancellationToken cancellationToken = default)
        {
            _writes.Add(FormattableString.Invariant($"block {baseId} issue {issuedThrough}"));
            return inner.CommitFamilyBlockAsync(familyId, baseId, issuedThrough, cancellationToken);
        }

        public async Task<int> CommitFamilyIssueAsync(
            long familyId, int blockOrdinal, CancellationToken cancellationToken = default)
        {
            int id = await inner.CommitFamilyIssueAsync(familyId, blockOrdinal, cancellationToken).ConfigureAwait(false);
            _writes.Add(FormattableString.Invariant($"next {id + 1}"));
            return id;
        }
    }
}
