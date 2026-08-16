using System;

namespace KhaozEngine.Primitives;

/// <summary>
/// Fixed-capacity free-list object pool. Items are created up front; <see cref="TryRent"/> and the return
/// paths are O(1) (return uses a linear scan of the active set, which is small in practice).
/// Active items are kept in a compacted array via swap-removal, so <see cref="GetActive"/> over
/// <see cref="ActiveCount"/> visits every live item with no gaps. Nothing here allocates after construction.
/// <para>
/// TWO RENT/RETURN PAIRS, and which one to use.
/// <see cref="TryRent"/> plus <see cref="Return(in PoolRental{T})"/> is the checked pair and the one to
/// reach for. The rental handle it hands out identifies ONE RENTAL, so a return that arrives after that
/// rental is over is refused rather than acted on.
/// <see cref="Rent"/> plus <see cref="Return(T)"/> is the older pair, kept working for existing callers. It
/// identifies rentals by the item reference alone, which is not enough: the pool hands the same object out
/// again on the next rent, so a stale return of a finished rental is indistinguishable from a return of the
/// live one and frees the live one. See
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/149">#149</see>.
/// </para>
/// <para>
/// HOW THE CHECK WORKS. Each slot carries a generation counter, bumped once when the slot is rented and once
/// when it is released, so the counter is ODD exactly while the slot is rented and EVEN exactly while it sits
/// on the free list. A rental handle records the generation it was stamped with, and a return is accepted only
/// when the slot's counter still reads that value. Both bumps are unchecked, and wrapping preserves parity
/// (the counter is plain mod-2^32 arithmetic), so the odd/even invariant survives overflow intact. A wrapped
/// counter can only produce a false accept for a caller still holding a rental 2^32 rent/release cycles after
/// it ended, which is not a practical concern: a slot rented and released every frame at 60Hz reaches that
/// after roughly two years of unbroken running.
/// </para>
/// </summary>
public sealed class ObjectPool<T> where T : class, IPoolable
{
    private readonly T[] items;
    private readonly int[] activeIndices;
    private readonly int[] freeIndices;

    /// <summary>Per-slot rental generation: odd while that slot is rented, even while it is free.</summary>
    private readonly int[] generations;

    private int activeCount;
    private int freeCount;

    /// <summary>
    /// Creates a pool of <paramref name="prewarmCount"/> items (clamped to a minimum of 1), building each via
    /// <paramref name="factory"/> and stamping its <see cref="IPoolable.PoolIndex"/>.
    /// </summary>
    public ObjectPool(Func<T> factory, int prewarmCount = 32)
    {
        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        int count = prewarmCount <= 0 ? 1 : prewarmCount;
        items = new T[count];
        activeIndices = new int[count];
        freeIndices = new int[count];
        generations = new int[count];   // all zero, which is even: every slot starts free

        for (int i = 0; i < count; i++)
        {
            T item = factory();
            item.PoolIndex = i;
            items[i] = item;
            freeIndices[i] = count - 1 - i;
        }

        activeCount = 0;
        freeCount = count;
    }

    /// <summary>Number of items currently rented out.</summary>
    public int ActiveCount => activeCount;

    /// <summary>Number of items available to rent.</summary>
    public int FreeCount => freeCount;

    /// <summary>
    /// Rents an item, or returns <c>null</c> if the pool is exhausted. The unchecked half of the older pair:
    /// prefer <see cref="TryRent"/>, whose rental handle lets the pool refuse a stale return.
    /// </summary>
    public T? Rent() => TryRent(out PoolRental<T> rental) ? rental.Item : null;

    /// <summary>
    /// Rents an item and hands back the <see cref="PoolRental{T}"/> that identifies THIS rental of it, or
    /// returns <c>false</c> and writes the empty rental when the pool is exhausted. Hold the rental for as
    /// long as the item is in use and hand it to <see cref="Return(in PoolRental{T})"/> once. Allocates nothing.
    /// </summary>
    public bool TryRent(out PoolRental<T> rental)
    {
        if (freeCount <= 0)
        {
            rental = default;
            return false;
        }

        int slot = freeIndices[--freeCount];
        activeIndices[activeCount++] = slot;
        int generation = unchecked(++generations[slot]);   // now odd: this slot is rented
        rental = new PoolRental<T>(this, items[slot], PoolRental<T>.Pack(slot, generation));
        return true;
    }

    /// <summary>
    /// Returns an item to the pool, calling <see cref="IPoolable.Reset"/> on it. Items that did not come from
    /// this pool (out-of-range or unknown <see cref="IPoolable.PoolIndex"/>) are ignored.
    /// <para>
    /// CANNOT SEE A STALE RETURN, which is why <see cref="Return(in PoolRental{T})"/> exists. This overload
    /// has only the item reference to go on, and the pool hands the same reference out again on the next rent,
    /// so a return of a finished rental after its slot was re-rented reads as a return of the live rental and
    /// frees it. Kept as-is for existing callers, and safe for the strict rent-then-return-once pattern, but
    /// new code should take the rental handle instead.
    /// </para>
    /// </summary>
    public void Return(T item)
    {
        int poolIndex = item.PoolIndex;
        if (poolIndex < 0 || poolIndex >= items.Length)
        {
            return;
        }

        for (int i = 0; i < activeCount; i++)
        {
            if (activeIndices[i] != poolIndex)
            {
                continue;
            }

            ReleaseAtActiveSlot(i);
            return;
        }
    }

    /// <summary>
    /// Ends the rental <paramref name="rental"/> names, calling <see cref="IPoolable.Reset"/> on the item and
    /// putting its slot back on the free list. Throws <see cref="StalePoolReturnException"/> when that rental
    /// is already over (returned once already, or its slot re-rented since) or did not come from this pool.
    /// Use <see cref="TryReturn"/> instead where a refusal is expected rather than a bug, and in particular
    /// inside a <c>finally</c>, where a throw here would replace the exception already unwinding.
    /// </summary>
    public void Return(in PoolRental<T> rental)
    {
        if (TryReturn(in rental))
        {
            return;
        }

        throw new StalePoolReturnException(rental.BelongsTo(this) ? rental.Slot : -1);
    }

    /// <summary>
    /// The non-throwing <see cref="Return(in PoolRental{T})"/>: ends the rental and returns <c>true</c>, or
    /// leaves the pool untouched and returns <c>false</c> when the rental is over, foreign, or empty. Returning
    /// the same rental twice is therefore safe and idempotent through this path, so it is the one to use for an
    /// idempotent dispose or from a <c>finally</c> block.
    /// </summary>
    public bool TryReturn(in PoolRental<T> rental)
    {
        if (!rental.BelongsTo(this))
        {
            return false;
        }

        int slot = rental.Slot;
        if (slot < 0 || slot >= items.Length || generations[slot] != rental.Generation)
        {
            return false;   // that rental is over: the slot is free again, or somebody else holds it now
        }

        for (int i = 0; i < activeCount; i++)
        {
            if (activeIndices[i] != slot)
            {
                continue;
            }

            ReleaseAtActiveSlot(i);
            return true;
        }

        // Unreachable while the invariant holds (an odd generation means the slot IS in the active set), and a
        // refusal is the safe direction if it ever stops holding.
        return false;
    }

    /// <summary>Returns the active item at the given slot (<c>0 .. ActiveCount-1</c>).</summary>
    public T GetActive(int activeSlot)
    {
        return items[activeIndices[activeSlot]];
    }

    /// <summary>Returns every active item to the pool, resetting each. Every outstanding rental is over
    /// afterwards, so returning one is refused rather than freeing whatever has since taken its slot.</summary>
    public void Clear()
    {
        while (activeCount > 0)
        {
            ReleaseAtActiveSlot(activeCount - 1);
        }
    }

    /// <summary>
    /// Test seam (<c>InternalsVisibleTo</c>): winds a slot's generation counter to an arbitrary value, so the
    /// wraparound behaviour can be exercised without performing 2^32 rentals. The value must be even, because
    /// even means free and the slot has to be free for the next rent to bump it to the odd live value.
    /// </summary>
    internal void SetSlotGenerationForTest(int slot, int generation) => generations[slot] = generation;

    private void ReleaseAtActiveSlot(int activeSlot)
    {
        int poolIndex = activeIndices[activeSlot];
        int lastActiveSlot = activeCount - 1;
        activeIndices[activeSlot] = activeIndices[lastActiveSlot];
        activeIndices[lastActiveSlot] = 0;
        activeCount--;

        unchecked { generations[poolIndex]++; }   // now even: this slot is free, and its rental is over

        T item = items[poolIndex];
        item.Reset();
        freeIndices[freeCount++] = poolIndex;
    }
}
