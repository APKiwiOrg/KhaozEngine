using System;

namespace KhaozEngine.Primitives;

/// <summary>
/// A bounded draw off an <see cref="IRandomSource"/> that reserves one logical slot through
/// <see cref="IRandomSource.NextInt"/> or <see cref="IRandomSource.Skip"/>, including a collapsed bound.
/// </summary>
/// <remarks>
/// <see cref="IRandomSource.NextInt"/> documents that a one-wide range consumes nothing. A roll bounded by a
/// number content tunes (an amount list, a one-in-N rate, an accuracy roll) can legally collapse to one, and
/// a bare <see cref="IRandomSource.NextInt"/> would silently omit that slot. A collapsed bound here calls
/// <see cref="IRandomSource.Skip"/> instead. The seeded Skip consumes exactly one underlying
/// <c>NextULong</c>, while a live NextInt may consume additional values during rejection sampling. This
/// helper does not guarantee an identical underlying stream stride for every bound.
/// <para>
/// An adapter onto the seam, not a generator: no state, no stream of its own and no probability of its own.
/// The distribution is <see cref="IRandomSource.NextInt"/>'s uniform draw over the same interval. On a source
/// with no position to keep, such as <see cref="CryptographicRandomSource"/>, the skip does nothing and the
/// collapsed bound still answers its one outcome.
/// </para>
/// </remarks>
public static class RandomDraw
{
    /// <summary>A uniform draw in <c>[0, exclusiveMax)</c> for a bound above one, otherwise 0 after Skip.
    /// Every valid call reserves one logical slot.</summary>
    /// <param name="rng">The caller-owned source. The order of calls is the order of logical slots.</param>
    /// <param name="exclusiveMax">The bound. Zero and one both answer 0 through Skip, while a wider bound
    /// calls NextInt. The underlying draw cost depends on the source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rng"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exclusiveMax"/> is negative, which is a
    /// caller bug rather than a draw, exactly as the seam treats an inverted range.</exception>
    public static int Below(IRandomSource rng, int exclusiveMax)
    {
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentOutOfRangeException.ThrowIfNegative(exclusiveMax);
        if (exclusiveMax <= 1)
        {
            rng.Skip();
            return 0;
        }
        return rng.NextInt(0, exclusiveMax);
    }

    /// <summary>A uniform draw in <c>[0, inclusiveMax]</c>, the inclusive form a roll ladder is quoted in. It
    /// reserves one logical slot, as <see cref="Below"/> does.</summary>
    /// <param name="rng">The stream.</param>
    /// <param name="inclusiveMax">The largest number the draw can answer. It must be below
    /// <see cref="int.MaxValue"/>, because the exclusive bound it becomes is one more.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rng"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inclusiveMax"/> is negative or is
    /// <see cref="int.MaxValue"/>.</exception>
    public static int UpTo(IRandomSource rng, int inclusiveMax)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inclusiveMax);
        ArgumentOutOfRangeException.ThrowIfEqual(inclusiveMax, int.MaxValue);
        return Below(rng, inclusiveMax + 1);
    }
}
