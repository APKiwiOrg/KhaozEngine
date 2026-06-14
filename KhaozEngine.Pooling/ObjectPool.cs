using System;

namespace KhaozEngine.Pooling;

/// <summary>
/// Fixed-capacity free-list object pool. Items are created up front; <see cref="Rent"/> and
/// <see cref="Return"/> are O(1) (return uses a linear scan of the active set, which is small in practice).
/// Active items are kept in a compacted array via swap-removal, so <see cref="GetActive"/> over
/// <see cref="ActiveCount"/> visits every live item with no gaps.
/// </summary>
public sealed class ObjectPool<T> where T : class, IPoolable
{
    private readonly T[] items;
    private readonly int[] activeIndices;
    private readonly int[] freeIndices;
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

    /// <summary>Rents an item, or returns <c>null</c> if the pool is exhausted.</summary>
    public T? Rent()
    {
        if (freeCount <= 0)
        {
            return null;
        }

        int poolIndex = freeIndices[--freeCount];
        activeIndices[activeCount++] = poolIndex;
        return items[poolIndex];
    }

    /// <summary>
    /// Returns an item to the pool, calling <see cref="IPoolable.Reset"/> on it. Items that did not come from
    /// this pool (out-of-range or unknown <see cref="IPoolable.PoolIndex"/>) are ignored.
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

    /// <summary>Returns the active item at the given slot (<c>0 .. ActiveCount-1</c>).</summary>
    public T GetActive(int activeSlot)
    {
        return items[activeIndices[activeSlot]];
    }

    /// <summary>Returns every active item to the pool, resetting each.</summary>
    public void Clear()
    {
        while (activeCount > 0)
        {
            ReleaseAtActiveSlot(activeCount - 1);
        }
    }

    private void ReleaseAtActiveSlot(int activeSlot)
    {
        int poolIndex = activeIndices[activeSlot];
        int lastActiveSlot = activeCount - 1;
        activeIndices[activeSlot] = activeIndices[lastActiveSlot];
        activeIndices[lastActiveSlot] = 0;
        activeCount--;

        T item = items[poolIndex];
        item.Reset();
        freeIndices[freeCount++] = poolIndex;
    }
}
