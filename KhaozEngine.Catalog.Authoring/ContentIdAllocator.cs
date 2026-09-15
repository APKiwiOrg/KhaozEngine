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
/// family with its ordered blocks, and the four writes the order rule spends.
/// <para>
/// <b>Every <c>Commit</c> member COMMITS ON ITS OWN</b>, and that is the whole of contracts 6.2. The
/// allocator calls <see cref="CommitReservedThroughAsync"/> and waits for it before any id below the new
/// mark leaves <see cref="ContentIdAllocator"/>, so a crash between the two writes skips ids that were never
/// issued rather than reissuing one. An implementation that batched the two into one transaction, or that
/// deferred the reservation to the caller's publish transaction, would invert the rule while leaving the
/// same two numbers behind afterwards.
/// </para>
/// <para>
/// It is a separate seam from <see cref="IContentAuthoringStore"/> because the allocator needs FIVE reads
/// and four writes rather than a whole store, and because a backend implements it with its own transactions.
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
    /// Writes the new reservation AND COMMITS IT ON ITS OWN. This is the write the order rule is about: it
    /// returns only once the promise is durable.
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="reservedThrough">The new reserved mark, never below the current one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task CommitReservedThroughAsync(
        ContentTypeId type,
        int reservedThrough,
        CancellationToken cancellationToken = default);

    /// <summary>Writes the new issued mark, which is only ever raised and never lowered.</summary>
    /// <param name="type">The content type.</param>
    /// <param name="issuedThrough">The new issued mark, at most the reserved mark.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task CommitIssuedThroughAsync(
        ContentTypeId type,
        int issuedThrough,
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
    /// </summary>
    /// <param name="familyId">The family the block belongs to.</param>
    /// <param name="baseId">The block's aligned base id, already reserved by the caller.</param>
    /// <param name="issuedThrough">The block's highest id, which the type's issued mark moves to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The inserted block, carrying the ordinal and version the store stamped it with.</returns>
    Task<ContentFamilyBlock> CommitFamilyBlockAsync(
        long familyId,
        int baseId,
        int issuedThrough,
        CancellationToken cancellationToken = default);

    /// <summary>Advances one block's next free id, which is how an id inside a block is issued.</summary>
    /// <param name="familyId">The family the block belongs to.</param>
    /// <param name="blockOrdinal">The block's position in the family's ordered list.</param>
    /// <param name="nextFreeId">The new next free id, at most the block's top exclusive.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task CommitFamilyNextFreeIdAsync(
        long familyId,
        int blockOrdinal,
        int nextFreeId,
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

        ContentIdHighWater mark = await _persistence
            .ReadHighWaterAsync(type, cancellationToken).ConfigureAwait(false);
        long ceiling = await ReadCeilingAsync(type, cancellationToken).ConfigureAwait(false);

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

        await _persistence.CommitIssuedThroughAsync(type, (int)top, cancellationToken).ConfigureAwait(false);
        return mark.IssuedThrough + 1;
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
        ContentFamily family = await RequireFamilyAsync(familyId, cancellationToken).ConfigureAwait(false);

        for (int i = 0; i < family.Blocks.Count; i++)
        {
            ContentFamilyBlock block = family.Blocks[i];
            if (!block.IsFull)
            {
                return await IssueFromBlockAsync(familyId, block, cancellationToken).ConfigureAwait(false);
            }
        }

        ContentFamilyBlock reserved = await ReserveBlockAsync(family, cancellationToken).ConfigureAwait(false);
        return await IssueFromBlockAsync(familyId, reserved, cancellationToken).ConfigureAwait(false);
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
        ContentFamily family = await RequireFamilyAsync(familyId, cancellationToken).ConfigureAwait(false);
        return await ReserveBlockAsync(family, cancellationToken).ConfigureAwait(false);
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

    async Task<ContentFamilyBlock> ReserveBlockAsync(ContentFamily family, CancellationToken cancellationToken)
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

    async Task<int> IssueFromBlockAsync(
        long familyId,
        ContentFamilyBlock block,
        CancellationToken cancellationToken)
    {
        // The whole block is already reserved AND marked issued on the type, so the durable promise is made.
        // The next free id still moves before the id is handed back, so a crash skips it rather than
        // handing the same id to the next caller.
        int id = block.NextFreeId;
        await _persistence
            .CommitFamilyNextFreeIdAsync(familyId, block.BlockOrdinal, id + 1, cancellationToken)
            .ConfigureAwait(false);
        return id;
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
