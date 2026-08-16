namespace KhaozEngine.Primitives;

/// <summary>
/// An item that can live in an <see cref="ObjectPool{T}"/>. The pool owns <see cref="PoolIndex"/> (it stamps
/// it on construction and reads it on return) and calls <see cref="Reset"/> when the item is returned.
/// <para>
/// NO RENTAL IDENTITY LIVES HERE, deliberately. The pool tracks which rental currently owns each slot with a
/// generation counter of its own, and hands that out in a <see cref="PoolRental{T}"/> rather than writing it
/// onto the item, because successive rentals of a slot ARE the same object and anything written onto it is
/// overwritten by the next rental. See <see cref="PoolRental{T}"/> for the full reasoning. That keeps this
/// contract at the one property implementers already have.
/// </para>
/// </summary>
public interface IPoolable
{
    /// <summary>Stable slot index assigned by the owning pool. Do not mutate from outside the pool.</summary>
    int PoolIndex { get; set; }

    /// <summary>Clears per-rental state. Called by the pool when the item is returned to the free list.</summary>
    void Reset();
}
