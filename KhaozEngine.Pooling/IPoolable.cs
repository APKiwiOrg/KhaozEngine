namespace KhaozEngine.Pooling;

/// <summary>
/// An item that can live in an <see cref="ObjectPool{T}"/>. The pool owns <see cref="PoolIndex"/> (it stamps
/// it on construction and reads it on return) and calls <see cref="Reset"/> when the item is returned.
/// </summary>
public interface IPoolable
{
    /// <summary>Stable slot index assigned by the owning pool. Do not mutate from outside the pool.</summary>
    int PoolIndex { get; set; }

    /// <summary>Clears per-rental state. Called by the pool when the item is returned to the free list.</summary>
    void Reset();
}
