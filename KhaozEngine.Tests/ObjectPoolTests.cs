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
}
