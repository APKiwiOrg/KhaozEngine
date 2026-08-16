using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests;

file sealed class Poolable : IPoolable
{
    public int PoolIndex { get; set; } = -1;
    public int ResetCount { get; private set; }
    public int Payload { get; set; }

    public void Reset()
    {
        ResetCount++;
        Payload = 0;
    }
}

public class ObjectPoolTests
{
    [Fact]
    public void PrewarmsFreeItemsAndStartsWithNoneActive()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 4);
        Assert.Equal(4, pool.FreeCount);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void RentMovesItemFromFreeToActiveAndAssignsPoolIndex()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 4);

        Poolable? item = pool.Rent();

        Assert.NotNull(item);
        Assert.Equal(3, pool.FreeCount);
        Assert.Equal(1, pool.ActiveCount);
        Assert.True(item!.PoolIndex >= 0 && item.PoolIndex < 4);
        Assert.Same(item, pool.GetActive(0));
    }

    [Fact]
    public void RentReturnsNullWhenExhausted()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 2);
        Assert.NotNull(pool.Rent());
        Assert.NotNull(pool.Rent());
        Assert.Null(pool.Rent());
        Assert.Equal(2, pool.ActiveCount);
        Assert.Equal(0, pool.FreeCount);
    }

    [Fact]
    public void ReturnResetsItemAndMakesItRentableAgain()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 2);
        Poolable item = pool.Rent()!;
        item.Payload = 42;

        pool.Return(item);

        Assert.Equal(0, item.Payload);
        Assert.Equal(1, item.ResetCount);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(2, pool.FreeCount);
        Assert.NotNull(pool.Rent());
    }

    [Fact]
    public void ReturnIsIgnoredForItemNotFromThisPool()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 2);
        pool.Rent();
        var foreign = new Poolable { PoolIndex = 99 };

        pool.Return(foreign);

        Assert.Equal(1, pool.ActiveCount);
        Assert.Equal(0, foreign.ResetCount);
    }

    [Fact]
    public void ReturningMiddleActiveItemSwapsLastIntoItsSlot()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 3);
        Poolable a = pool.Rent()!;
        Poolable b = pool.Rent()!;
        Poolable c = pool.Rent()!;

        // Return the middle active item; swap-removal moves the last (c) into b's slot.
        pool.Return(b);

        Assert.Equal(2, pool.ActiveCount);
        Assert.Same(a, pool.GetActive(0));
        Assert.Same(c, pool.GetActive(1));
    }

    [Fact]
    public void ClearReturnsAllActiveItemsAndResetsThem()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 3);
        Poolable a = pool.Rent()!;
        Poolable b = pool.Rent()!;

        pool.Clear();

        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(3, pool.FreeCount);
        Assert.Equal(1, a.ResetCount);
        Assert.Equal(1, b.ResetCount);
    }

    [Fact]
    public void NonPositivePrewarmStillProvidesOneSlot()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 0);
        Assert.Equal(1, pool.FreeCount);
        Assert.NotNull(pool.Rent());
        Assert.Null(pool.Rent());
    }

    // ---- Rental handles: one rental, not one slot (#149) ----

    [Fact]
    public void TryRentHandsOutTheItemAndIsFalseWhenExhausted()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 1);

        Assert.True(pool.TryRent(out PoolRental<Poolable> rental));
        Assert.NotNull(rental.Item);
        Assert.False(rental.IsEmpty);
        Assert.Same(rental.Item, pool.GetActive(0));

        Assert.False(pool.TryRent(out PoolRental<Poolable> exhausted));
        Assert.True(exhausted.IsEmpty);
        Assert.Null(exhausted.Item);
    }

    /// <summary>
    /// The exact trace from #149. A single-slot pool always hands the same object back out, so the stale
    /// reference and the live rental are literally the same object. Only the rental handle can tell them apart.
    /// </summary>
    [Fact]
    public void StaleRentalDoesNotFreeTheSlotAfterItWasRentedAgain()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 1);

        Assert.True(pool.TryRent(out PoolRental<Poolable> a));
        Assert.True(pool.TryReturn(in a));
        Assert.True(pool.TryRent(out PoolRental<Poolable> b));

        // The premise of the bug: b IS a, same reference, because the pool reuses the slot's object.
        Assert.Same(a.Item, b.Item);

        Assert.False(pool.TryReturn(in a));   // the stale return must not land

        Assert.Equal(1, pool.ActiveCount);    // b's rental is untouched
        Assert.Equal(0, pool.FreeCount);
        Assert.Same(b.Item, pool.GetActive(0));
        Assert.True(pool.TryReturn(in b));    // and b can still return it itself
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void StaleRentalReturnThrowsTheNamedRefusalNamingItsSlot()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 1);
        Assert.True(pool.TryRent(out PoolRental<Poolable> a));
        Assert.True(pool.TryReturn(in a));
        Assert.True(pool.TryRent(out PoolRental<Poolable> b));

        var refusal = Assert.Throws<StalePoolReturnException>(() => pool.Return(in a));

        Assert.Equal(0, refusal.Slot);
        Assert.Equal(StalePoolReturnException.BuildMessage(0), refusal.Message);
        Assert.Equal(1, pool.ActiveCount);
        Assert.True(pool.TryReturn(in b));
    }

    [Fact]
    public void ReturningTheSameRentalTwiceThrowsOnceAndIsIdempotentThroughTryReturn()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 2);
        Assert.True(pool.TryRent(out PoolRental<Poolable> rental));

        pool.Return(in rental);
        Assert.Equal(0, pool.ActiveCount);

        Assert.False(pool.TryReturn(in rental));            // the idempotent path absorbs the second return
        Assert.Throws<StalePoolReturnException>(() => pool.Return(in rental));
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(2, pool.FreeCount);                    // and neither one double-freed the slot
        Assert.Equal(1, rental.Item!.ResetCount);           // Reset ran exactly once
    }

    [Fact]
    public void RentalFromAnotherPoolIsRefusedEvenWhenTheSlotNumbersMatch()
    {
        var left = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 1);
        var right = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 1);
        Assert.True(left.TryRent(out PoolRental<Poolable> fromLeft));
        Assert.True(right.TryRent(out PoolRental<Poolable> fromRight));

        // Both rentals name slot 0 at the same generation. The owning-pool check is what separates them.
        var refusal = Assert.Throws<StalePoolReturnException>(() => right.Return(in fromLeft));
        Assert.Equal(-1, refusal.Slot);
        Assert.False(right.TryReturn(in fromLeft));

        Assert.Equal(1, right.ActiveCount);
        Assert.Equal(1, left.ActiveCount);
        Assert.True(right.TryReturn(in fromRight));
        Assert.True(left.TryReturn(in fromLeft));
    }

    [Fact]
    public void EmptyRentalIsRefusedRatherThanFreeingSlotZero()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 1);
        Assert.True(pool.TryRent(out PoolRental<Poolable> live));

        PoolRental<Poolable> empty = default;
        Assert.True(empty.IsEmpty);
        Assert.False(pool.TryReturn(in empty));
        Assert.Throws<StalePoolReturnException>(() => pool.Return(in empty));

        Assert.Equal(1, pool.ActiveCount);
        Assert.True(pool.TryReturn(in live));
    }

    [Fact]
    public void ClearEndsOutstandingRentalsSoReturningOneIsRefused()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 2);
        Assert.True(pool.TryRent(out PoolRental<Poolable> a));
        Assert.True(pool.TryRent(out PoolRental<Poolable> b));

        pool.Clear();
        Assert.True(pool.TryRent(out PoolRental<Poolable> afterClear));

        Assert.False(pool.TryReturn(in a));
        Assert.False(pool.TryReturn(in b));
        Assert.Equal(1, pool.ActiveCount);
        Assert.True(pool.TryReturn(in afterClear));
    }

    /// <summary>
    /// Generation wraparound. The counter is a plain <c>int</c> bumped once per rent and once per release, so it
    /// comes back to a value it already held after 2^31 rent/release cycles of one slot, each cycle advancing it
    /// by 2 through the full mod-2^32 range. That arithmetic preserves the odd-is-rented parity exactly, so a
    /// stale rental is still refused across the boundary. The seam winds the counter to the edge because
    /// performing 2^31 real rent/release cycles is not a test.
    /// </summary>
    [Fact]
    public void GenerationWraparoundStillRefusesAStaleRental()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 1);
        pool.SetSlotGenerationForTest(0, int.MaxValue - 1);   // even, so the slot reads as free

        Assert.True(pool.TryRent(out PoolRental<Poolable> a));   // generation int.MaxValue
        Assert.True(pool.TryReturn(in a));                       // releases, wrapping to int.MinValue
        Assert.True(pool.TryRent(out PoolRental<Poolable> b));   // int.MinValue + 1, same object as a
        Assert.Same(a.Item, b.Item);

        Assert.False(pool.TryReturn(in a));
        Assert.Equal(1, pool.ActiveCount);
        Assert.True(pool.TryReturn(in b));
    }

    /// <summary>
    /// Characterizes what the older item-only pair CANNOT do, which is the whole reason the rental handle
    /// exists. This is not a bug report against <c>Return(T)</c>, it is the pinned limitation: with only the
    /// item reference to go on there is no information anywhere that separates a finished rental from the live
    /// one, because they are the same object. Callers that need the check take the handle.
    /// </summary>
    [Fact]
    public void LegacyItemOnlyReturnCannotSeeAStaleReturnAfterTheSlotIsReRented()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 1);
        Poolable a = pool.Rent()!;
        pool.Return(a);
        Poolable b = pool.Rent()!;
        Assert.Same(a, b);

        pool.Return(a);   // stale by intent, indistinguishable from Return(b)

        Assert.Equal(0, pool.ActiveCount);   // b's live rental was freed under it, as documented
    }
}

/// <summary>
/// The allocation half, in its own class because it belongs in the non-parallel <c>AllocSensitive</c>
/// collection (see <see cref="AllocSensitiveCollection"/>) and the rest of the pool tests do not.
/// The pool exists for zero-allocation hot paths, so the rental handle earns its place only if renting and
/// returning through it still allocates nothing: <see cref="PoolRental{T}"/> is a readonly struct passed by
/// <c>in</c>, and this is what holds that to it.
/// </summary>
[Collection("AllocSensitive")]
public class ObjectPoolAllocationTests
{
    [Fact]
    public void RentAndReturnDoNotAllocatePerCall()
    {
        var pool = new ObjectPool<Poolable>(() => new Poolable(), prewarmCount: 4);

        // A local function, not a method: Poolable is file-local and cannot appear in a member signature of a
        // non-file-local type.
        void Cycle()
        {
            for (int i = 0; i < 200; i++)
            {
                if (pool.TryRent(out PoolRental<Poolable> rental))
                {
                    pool.Return(in rental);
                }

                Poolable? legacy = pool.Rent();
                if (legacy is not null)
                {
                    pool.Return(legacy);
                }
            }
        }

        // Warm up: the first pass through each path JITs it, which allocates and is not per-call cost.
        Cycle();

        AllocAssert.NoPerCallAllocation("ObjectPool rent/return", Cycle);
    }
}
