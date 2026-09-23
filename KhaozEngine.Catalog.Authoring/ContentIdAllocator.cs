using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The two durable numbers <c>catalog_id_high_water</c> carries per content type (spec 4.3).
/// <para>
/// <see cref="ReservedThrough"/> is the highest id the store has DURABLY promised not to hand out twice.
/// <see cref="IssuedThrough"/> is the highest id actually stamped onto a row. The gap between them is the
/// live reservation, and it is what a crash is allowed to waste.
/// </para>
/// </summary>
/// <param name="ReservedThrough">The highest id durably reserved, 0 on a type that has allocated nothing.</param>
/// <param name="IssuedThrough">The highest id issued, never above <paramref name="ReservedThrough"/>.</param>
public readonly record struct ContentIdHighWater(int ReservedThrough, int IssuedThrough);

/// <summary>
/// The DURABLE half the allocator sits on: the two high-water numbers per type, the per-type id ceiling, a
/// family with its ordered blocks, the four writes the order rule spends, and the seeding write a carried id
/// needs.
/// <para>
/// <b>Every <c>Commit</c> member COMMITS ON ITS OWN</b>, and that is the whole of contracts 6.2. The
/// allocator calls <see cref="CommitReservedThroughAsync"/> and waits for it before any id below the new
/// mark leaves <see cref="ContentIdAllocator"/>, so a crash between the two writes skips ids that were never
/// issued rather than reissuing one. An implementation that batched the two into one transaction, or that
/// deferred the reservation to the caller's publish transaction, would invert the rule while leaving the
/// same two numbers behind afterwards.
/// </para>
/// <para>
/// <b>Every <c>Commit</c> member COMPARES INSIDE its own commit</b> rather than writing a number its caller
/// computed from an earlier read. Two hosts allocate from one catalog with no lock spanning either
/// allocation, so a mark read in one call can be passed by a rival before the next call writes. A write that
/// trusted the read would hand out an id twice or refuse a promise the rival already made. The members
/// therefore raise a mark to at least a value, or take the next ids only when the live reservation still
/// covers them, and answer when it does not, so the allocator can read again.
/// </para>
/// <para>
/// It is a separate seam from <see cref="IContentAuthoringStore"/> because the allocator and the seeding step
/// need three reads and five writes rather than a whole store, and because a backend implements it with its
/// own transactions.
/// </para>
/// </summary>
public interface IContentIdPersistence
{
    /// <summary>The type's two marks, <c>(0, 0)</c> for a type that has allocated nothing yet.</summary>
    /// <param name="type">The content type.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ContentIdHighWater> ReadHighWaterAsync(ContentTypeId type, CancellationToken cancellationToken = default);

    /// <summary>
    /// The type's declared id CEILING, or null when its only ceiling is the 31 bits of positive
    /// <c>int</c> space. A type declares one when a FORMAT it is carried in cannot hold a bigger number.
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<int?> ReadMaxDefinitionIdAsync(ContentTypeId type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raises the reservation to at least <paramref name="reservedThrough"/> AND COMMITS IT ON ITS OWN. This is
    /// the write the order rule is about: it returns only once the promise is durable. A reservation already
    /// at or above it is left where it stands, because a rival that reserved further first has already made
    /// the promise this call asks for, and a promise is never taken back.
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="reservedThrough">The mark the reservation must reach.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task CommitReservedThroughAsync(
        ContentTypeId type,
        int reservedThrough,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues the next <paramref name="count"/> plain ids above the issued mark, in one commit that reads the
    /// mark it advances, and answers the first of them. When the live reservation does not cover all of them,
    /// because a rival issued ids since the caller last read, nothing is written and the answer is 0, which is
    /// never an id.
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="count">How many ids, at least 1.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The first id issued, or 0 when the reservation no longer covers the range.</returns>
    Task<int> CommitIssueAsync(
        ContentTypeId type,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raises BOTH marks to at least an id an edit CARRIED, which is the publish's seeding step, and leaves a
    /// mark that already covers it where it stands. The reserved mark commits on its own first and the issued
    /// mark second, the same order as every other write here.
    /// <para>
    /// <b>Each comparison happens INSIDE the commit that writes it</b>, and that is the reason this member
    /// exists. Two publishers seed one type without a lock spanning either publish, so a mark read in one call
    /// and written in the next can be passed by a rival in between, and writing the stale number would take a
    /// durable reservation back.
    /// </para>
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="carriedThrough">The largest id an edit of that type carried, at least 1.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when either mark moved, false when both already covered the carried id.</returns>
    Task<bool> CommitCarriedThroughAsync(
        ContentTypeId type,
        int carriedThrough,
        CancellationToken cancellationToken = default);

    /// <summary>One family with its blocks in ORDINAL order, or null when the store holds no such family.</summary>
    /// <param name="familyId">The family's own id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ContentFamily?> ReadFamilyAsync(long familyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the family's next block at <paramref name="baseId"/> AND advances the type's issued mark to
    /// the block's top, in one commit. The advance is what keeps the plain counter out of the block, so it
    /// may not be split from the insert: a store that wrote the block row and crashed before the advance
    /// would hand the next plain allocation an id inside the new block.
    /// <para>
    /// <b>The block is still free only while the type's issued mark is below its base</b>, because every id
    /// already issued, carried or promised to another block sits at or below that mark. The commit checks that
    /// against the mark it reads itself, and when a rival has moved the mark to the base or past it, nothing
    /// is written and the answer is null, so the caller reads again and opens a block further up.
    /// </para>
    /// </summary>
    /// <param name="familyId">The family the block belongs to.</param>
    /// <param name="baseId">The block's aligned base id, already reserved by the caller.</param>
    /// <param name="issuedThrough">The block's highest id, which the type's issued mark moves to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The inserted block, carrying the ordinal and version the store stamped it with, or null when the range is no longer free.</returns>
    Task<ContentFamilyBlock?> CommitFamilyBlockAsync(
        long familyId,
        int baseId,
        int issuedThrough,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues the next free id of one block, in one commit that reads the next free id it advances, which is
    /// how an id inside a block is issued. A block a rival filled since the caller read it answers 0, which is
    /// never an id.
    /// </summary>
    /// <param name="familyId">The family the block belongs to.</param>
    /// <param name="blockOrdinal">The block's position in the family's ordered list.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id issued, or 0 when the block is full.</returns>
    Task<int> CommitFamilyIssueAsync(
        long familyId,
        int blockOrdinal,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The definition-id allocator of spec 4.7: RESERVE BEFORE ISSUE on the plain counter, and the same rule on
/// a family's narrower aligned blocks (contracts 5.2, 6.2).
/// <para>
/// <b>The ORDER is the contract and the batch size is not.</b> A range is reserved durably before any id in
/// it is issued, so the worst a crash can do is skip a block of ids that were never issued, and it can never
/// reissue one. Persisting the reservation after issuing leaves a window in which a crash hands the next
/// boot an id it has already put on a row, and a duplicate definition id is the one failure this type exists
/// to prevent. A crash between the two commits skips up to <see cref="ReserveBatch"/> ids, which is free:
/// ids are 31 bits of positive <c>int</c> space per type against an owner figure of 50,000 definitions.
/// </para>
/// <para>
/// <b>Reserving a family block ADVANCES the type's issued mark past the block top</b>, which is what keeps
/// the plain counter out of the block and is why the plain path needs no knowledge of families at all.
/// Without it the plain counter walks through the gap under the block and hands out an id inside it a second
/// time, to a row that is not in the family. The alternative, teaching the plain path to read the block list
/// and skip past it, was weighed and refused in spec 4.7: the plain path stays one comparison against one
/// durable number, and a skip would have to define what a multi-id range straddling a block means.
/// </para>
/// <para>
/// <b>Two allocators on one catalog are safe because every write compares inside its own commit.</b> A take
/// that a rival got to first answers that it did not happen, and the allocator reads the marks again and
/// tries once more. That is not a retry on a timer: an attempt only fails because another allocation on the
/// same type SUCCEEDED in between, so the loop cannot turn without the catalog moving, and the ceiling check
/// on every pass ends it when the id space runs out.
/// </para>
/// <para>
/// It holds no ambient state and no connection. Everything durable arrives through
/// <see cref="IContentIdPersistence"/>, so the same allocator runs over a database, over a test store and
/// over a recording decorator that asserts the write ORDER.
/// </para>
/// </summary>
public sealed class ContentIdAllocator
{
    /// <summary>
    /// How far ahead a reservation reaches when the live one cannot cover a request, spec 4.7's 1,024. It
    /// is a BATCH SIZE rather than a rule: raising it wastes more ids per crash and lowering it costs more
    /// commits, and neither changes the guarantee.
    /// </summary>
    public const int ReserveBatch = 1024;

    readonly IContentIdPersistence _persistence;

    /// <summary>Builds an allocator over one durable seam.</summary>
    /// <param name="persistence">The durable half, whose every commit lands on its own.</param>
    /// <exception cref="ArgumentNullException"><paramref name="persistence"/> is null.</exception>
    public ContentIdAllocator(IContentIdPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        _persistence = persistence;
    }

    /// <summary>
    /// Issues a contiguous range of plain definition ids and returns the FIRST id of it, so the range is
    /// <c>[first, first + count - 1]</c>.
    /// </summary>
    /// <param name="type">The content type to allocate from.</param>
    /// <param name="count">How many ids, at least 1.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1.</exception>
    /// <exception cref="ContentAuthoringException">The range would cross the type's declared id ceiling.</exception>
    public async Task<int> AllocateAsync(
        ContentTypeId type,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        long ceiling = await ReadCeilingAsync(type, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            ContentIdHighWater mark = await _persistence
                .ReadHighWaterAsync(type, cancellationToken).ConfigureAwait(false);

            long top = mark.IssuedThrough + (long)count;
            if (top > ceiling)
            {
                throw CeilingRefusal(type, ceiling, mark, count);
            }

            if (top > mark.ReservedThrough)
            {
                // Step 2 of spec 4.7. The reservation commits ON ITS OWN, and nothing below it is issued until
                // this await has returned. It stops at the ceiling rather than overrunning it.
                long target = mark.IssuedThrough + Math.Max(count, (long)ReserveBatch);
                await _persistence
                    .CommitReservedThroughAsync(type, (int)Math.Min(target, ceiling), cancellationToken)
                    .ConfigureAwait(false);
            }

            // The take reads the issued mark inside its own commit, so the ids it answers are ones no rival
            // holds. A zero means a rival issued past the reservation this pass saw, and the next pass reads
            // the marks it left.
            int first = await _persistence.CommitIssueAsync(type, count, cancellationToken).ConfigureAwait(false);
            if (first != 0)
            {
                return first;
            }
        }
    }

    /// <summary>
    /// Issues ONE id from a family's blocks in ordinal order, reserving a new aligned block when every block
    /// is full.
    /// </summary>
    /// <param name="familyId">The family to allocate from.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The family is not in the store, or a new block's top would cross the type's ceiling.</exception>
    public async Task<int> AllocateInFamilyAsync(long familyId, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ContentFamily family = await RequireFamilyAsync(familyId, cancellationToken).ConfigureAwait(false);

            // The whole block is already reserved AND marked issued on the type, so the durable promise is
            // made. The take moves the block's next free id before the id is handed back, so a crash skips it
            // rather than handing the same id to the next caller, and a block a rival filled answers 0.
            for (int i = 0; i < family.Blocks.Count; i++)
            {
                if (!family.Blocks[i].IsFull)
                {
                    int id = await _persistence
                        .CommitFamilyIssueAsync(familyId, family.Blocks[i].BlockOrdinal, cancellationToken)
                        .ConfigureAwait(false);
                    if (id != 0)
                    {
                        return id;
                    }
                }
            }

            ContentFamilyBlock? reserved = await TryReserveBlockAsync(family, cancellationToken)
                .ConfigureAwait(false);
            if (reserved is ContentFamilyBlock opened)
            {
                int id = await _persistence
                    .CommitFamilyIssueAsync(familyId, opened.BlockOrdinal, cancellationToken)
                    .ConfigureAwait(false);
                if (id != 0)
                {
                    return id;
                }
            }
        }
    }

    /// <summary>
    /// Reserves one more ALIGNED block for a family, which is what a family creation does for its first
    /// block and what a full family does for its next one.
    /// </summary>
    /// <param name="familyId">The family to reserve for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The family is not in the store, or the block's top would cross the type's ceiling.</exception>
    public async Task<ContentFamilyBlock> ReserveBlockAsync(
        long familyId,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ContentFamily family = await RequireFamilyAsync(familyId, cancellationToken).ConfigureAwait(false);
            ContentFamilyBlock? reserved = await TryReserveBlockAsync(family, cancellationToken)
                .ConfigureAwait(false);
            if (reserved is ContentFamilyBlock opened)
            {
                return opened;
            }
        }
    }

    /// <summary>
    /// Where a family's next block opens: the type's reserved mark rounded UP to the block size, floored at
    /// the first UNISSUED id.
    /// <para>
    /// Spec 4.7 states the rounding against <c>reserved_through</c> alone, and at an exactly aligned mark
    /// that is the mark itself, which is right while ids below it are reserved and unissued. When every
    /// reserved id has also been ISSUED, that same block would open on an id already stamped onto a row, and
    /// contracts 5.1 says an id is never reused. The floor is the refinement that keeps both true, and it
    /// changes nothing in the ordinary case: spec 4.7's own worked example, a mark of 1,024 reserved with 10
    /// issued, still opens at 1,024.
    /// </para>
    /// </summary>
    /// <param name="mark">The type's two marks.</param>
    /// <param name="blockSize">The family's declared block size, a power of two.</param>
    public static long NextBlockBase(ContentIdHighWater mark, int blockSize)
    {
        long floor = Math.Max(mark.ReservedThrough, mark.IssuedThrough + 1L);
        long remainder = floor % blockSize;
        return remainder == 0 ? floor : floor + (blockSize - remainder);
    }

    /// <summary>
    /// One attempt at the next block: the block the marks just read say is free, reserved through its top and
    /// then inserted, or null when a rival moved the issued mark to its base or past it in between.
    /// </summary>
    async Task<ContentFamilyBlock?> TryReserveBlockAsync(ContentFamily family, CancellationToken cancellationToken)
    {
        ContentIdHighWater mark = await _persistence
            .ReadHighWaterAsync(family.Type, cancellationToken).ConfigureAwait(false);
        long ceiling = await ReadCeilingAsync(family.Type, cancellationToken).ConfigureAwait(false);

        long baseId = NextBlockBase(mark, family.BlockSize);
        long topId = baseId + family.BlockSize - 1;
        if (topId > ceiling)
        {
            throw BlockCeilingRefusal(family, ceiling, mark, baseId, topId);
        }

        // Reserve through the top of the new block FIRST, on its own commit, then insert the block row
        // together with the advance of the issued mark to that same top.
        await _persistence
            .CommitReservedThroughAsync(family.Type, (int)topId, cancellationToken)
            .ConfigureAwait(false);
        return await _persistence
            .CommitFamilyBlockAsync(family.FamilyId, (int)baseId, (int)topId, cancellationToken)
            .ConfigureAwait(false);
    }

    async Task<ContentFamily> RequireFamilyAsync(long familyId, CancellationToken cancellationToken)
        => await _persistence.ReadFamilyAsync(familyId, cancellationToken).ConfigureAwait(false)
            ?? throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Family {familyId} is not in the store, so no id can be allocated from it."),
                default,
                0,
                ContentAuthoringException.UnknownFamilyReason);

    async Task<long> ReadCeilingAsync(ContentTypeId type, CancellationToken cancellationToken)
        => await _persistence.ReadMaxDefinitionIdAsync(type, cancellationToken).ConfigureAwait(false)
            ?? int.MaxValue;

    static ContentAuthoringException CeilingRefusal(
        ContentTypeId type,
        long ceiling,
        ContentIdHighWater mark,
        int count)
        => new(
            FormattableString.Invariant(
                $"Content type {type.Value} declares an id ceiling of {ceiling} and has issued through {mark.IssuedThrough}, so a range of {count} more cannot be allocated. The ceiling is a format constraint rather than a preference, and ids are never reused, so a retired row keeps its id and the space it occupies."),
            type,
            0,
            ContentAuthoringException.IdCeilingReason);

    static ContentAuthoringException BlockCeilingRefusal(
        ContentFamily family,
        long ceiling,
        ContentIdHighWater mark,
        long baseId,
        long topId)
        => new(
            FormattableString.Invariant(
                $"Content type {family.Type.Value} declares an id ceiling of {ceiling}, so family '{family.FamilyKey}' cannot reserve the block [{baseId}, {topId + 1}) whose highest id is {topId}. The type has reserved through {mark.ReservedThrough} and issued through {mark.IssuedThrough}."),
            family.Type,
            0,
            ContentAuthoringException.IdCeilingReason);
}
